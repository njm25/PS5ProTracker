using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using PS5Tracker.Probe;
using PS5Tracker.Probe.Fetching;
using PS5Tracker.Probe.Parsers;

var once = args.Contains("--once");
var only = ArgValue("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (args.Contains("--install-browsers"))
    return Microsoft.Playwright.Program.Main(["install", "chromium"]);

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables("PS5_")
    .Build();
var options = config.GetSection("Probe").Get<ProbeOptions>() ?? new ProbeOptions();

// A target's Ramp only overrides the fields it sets; the rest come from DefaultRamp.
for (var i = 0; i < options.Targets.Count; i++)
{
    var section = config.GetSection($"Probe:Targets:{i}:Ramp");
    if (section.Exists())
        options.Targets[i].Ramp = MergeRamp(options.DefaultRamp, section);
}

// A profile picks targets by name and layers ramp/settle overrides on top of them.
var profileName = ArgValue("--profile");
ProfileOptions? profile = null;
if (profileName is not null)
{
    if (!options.Profiles.TryGetValue(profileName, out profile))
    {
        Console.Error.WriteLine($"Unknown profile '{profileName}'. Known: {string.Join(", ", options.Profiles.Keys)}");
        return 2;
    }
    foreach (var (name, over) in profile.Targets)
    {
        var t = options.Targets.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (t is null)
        {
            Console.Error.WriteLine($"Profile '{profileName}' names unknown target '{name}'.");
            return 2;
        }
        var section = config.GetSection($"Probe:Profiles:{profileName}:Targets:{name}:Ramp");
        if (section.Exists())
            t.Ramp = MergeRamp(t.Ramp ?? options.DefaultRamp, section);
        if (over.SettleMs is { } settle)
            t.SettleMs = settle;
    }
    if (profile.Headless is { } headless)
        options.Headless = headless;
    if (profile.MaxRunMinutes is { } minutes)
        options.MaxRunMinutes = minutes;
}
if (args.Contains("--headed"))
    options.Headless = false;

if (args.Contains("--list"))
{
    foreach (var t in options.Targets)
        Console.WriteLine($"{t.Name,-18} {(t.Enabled ? "on " : "off")} {t.Method,-20} {t.Parser,-22} {string.Join(",", (t.Ramp ?? options.DefaultRamp).Intervals)}s  {t.Url}");
    foreach (var (name, p) in options.Profiles)
        Console.WriteLine($"profile {name}: {string.Join(", ", p.Targets.Keys)}{(p.Headless == false ? " (headed)" : "")}");
    return 0;
}

var selected = only ?? profile?.Targets.Keys.ToArray();
var targets = options.Targets
    .Where(t => selected is null ? t.Enabled : selected.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
    .ToList();
if (only?.Except(targets.Select(t => t.Name), StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } unknown)
{
    Console.Error.WriteLine($"Unknown target(s): {string.Join(", ", unknown)}. Use --list.");
    return 2;
}

var runDir = Path.Combine(
    ResolveOutputDir(options.OutputDir),
    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (profileName is null ? "" : $"-{profileName}") + (once ? "-once" : ""));
using var log = new ProbeLog(runDir);
File.WriteAllText(Path.Combine(runDir, "targets.json"), JsonSerializer.Serialize(targets, new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
}));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
if (options.MaxRunMinutes is { } max && !once)
    cts.CancelAfter(TimeSpan.FromMinutes(max));

Console.WriteLine($"Run dir: {runDir}");
Console.WriteLine($"Targets: {string.Join(", ", targets.Select(t => t.Name))}{(once ? " (one sample each)" : "")}{(options.Headless ? "" : " [headed]")}");

await using var browserHost = new BrowserHost(options);
var disposables = new List<IAsyncDisposable>();
var skipped = new List<TargetSummary>();
var runners = new List<TargetRunner>();

foreach (var t in targets)
{
    if (!StockParsers.All.TryGetValue(t.Parser, out var parser))
    {
        skipped.Add(new TargetSummary(t) { Skipped = $"unknown parser '{t.Parser}'" });
        continue;
    }
    if (BuildFetch(t) is not { } fetch)
        continue;
    var runner = new TargetRunner(t, options, log, fetch, parser, once);
    // Targets of one retailer share this IP's reputation there: one trip stops them all.
    runner.Tripped += (tripped, reason) =>
    {
        foreach (var sibling in runners.Where(r => r != tripped && r.Target.Retailer == tripped.Target.Retailer))
            sibling.StopBecause($"{tripped.Target.Name} tripped ({reason})");
    };
    runners.Add(runner);
}

var runs = runners.Select(r => r.RunAsync(cts.Token)).ToList();
var summaries = skipped.Concat(await Task.WhenAll(runs)).ToList();
foreach (var d in disposables)
    await d.DisposeAsync();

WriteSummary(summaries);
return 0;

Func<CancellationToken, Task<FetchResult>>? BuildFetch(TargetOptions t)
{
    switch (t.Method)
    {
        case ProbeMethod.Http:
        {
            var http = new HttpFetcher(options);
            disposables.Add(new AsyncDisposer(http.Dispose));
            // {nonce} makes every URL unique, to get past a shared cache.
            return ct => http.FetchAsync(
                t.Url.Replace("{nonce}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
                t.Headers, ct);
        }
        case ProbeMethod.Browser:
        {
            var browser = new BrowserFetcher(browserHost, options);
            disposables.Add(browser);
            return ct => browser.FetchAsync(t, ct);
        }
        case ProbeMethod.BestBuyApi:
        {
            if (string.IsNullOrWhiteSpace(options.Credentials.BestBuyApiKey))
                return Skip(t, "no Credentials:BestBuyApiKey");
            var key = options.Credentials.BestBuyApiKey;
            var http = new HttpFetcher(options);
            disposables.Add(new AsyncDisposer(http.Dispose));
            return async ct =>
            {
                var r = await http.FetchAsync(t.Url.Replace("{BestBuyApiKey}", key), null, ct);
                return r with { FinalUrl = r.FinalUrl.Replace(key, "REDACTED") };
            };
        }
        case ProbeMethod.WalmartAffiliateApi:
        {
            var c = options.Credentials;
            if (string.IsNullOrWhiteSpace(c.WalmartConsumerId) || string.IsNullOrWhiteSpace(c.WalmartPrivateKeyPath))
                return Skip(t, "no Credentials:WalmartConsumerId / WalmartPrivateKeyPath");
            var signer = new WalmartSigner(c.WalmartConsumerId, c.WalmartKeyVersion, File.ReadAllText(c.WalmartPrivateKeyPath));
            var http = new HttpFetcher(options);
            disposables.Add(new AsyncDisposer(() => { http.Dispose(); signer.Dispose(); }));
            return ct => http.FetchAsync(t.Url, signer.Headers(DateTimeOffset.UtcNow), ct);
        }
        default:
            return Skip(t, $"unsupported method {t.Method}");
    }
}

Func<CancellationToken, Task<FetchResult>>? Skip(TargetOptions t, string reason)
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {t.Name,-16} skipped: {reason}");
    skipped.Add(new TargetSummary(t) { Skipped = reason });
    return null;
}

void WriteSummary(List<TargetSummary> all)
{
    var csv = new StringBuilder("target,method,samples,parsed,changes,would_alert,last_clean_interval_s,tripped_at_interval_s,trip_reason,hold_clean,latency_p50_ms,latency_p95_ms,skipped\n");
    Console.WriteLine();
    Console.WriteLine($"{"target",-16} {"samples",7} {"parsed",6} {"clean@",7} {"trip@",6} {"p50ms",6} {"p95ms",6}  reason");
    foreach (var s in all.OrderBy(s => s.Target.Name))
    {
        csv.AppendLine(string.Join(',',
            s.Target.Name, s.Target.Method, s.Samples, s.Parsed, s.Changes, s.Alerts,
            s.LastCleanInterval, s.TrippedAtInterval, Csv(s.TripReason), s.HoldClean,
            s.Percentile(0.5), s.Percentile(0.95), Csv(s.Skipped)));
        Console.WriteLine($"{s.Target.Name,-16} {s.Samples,7} {s.Parsed,6} {Fmt(s.LastCleanInterval),7} {Fmt(s.TrippedAtInterval),6} {s.Percentile(0.5),6} {s.Percentile(0.95),6}  {s.Skipped ?? s.TripReason}");
    }
    File.WriteAllText(Path.Combine(runDir, "summary.csv"), csv.ToString());
    Console.WriteLine($"\nWrote {Path.Combine(runDir, "samples.csv")} and summary.csv");

    static string Fmt(int? v) => v is null ? "-" : $"{v}s";
    static string Csv(string? v) => v is null ? "" : $"\"{v.Replace("\"", "\"\"")}\"";
}

static RampOptions MergeRamp(RampOptions baseRamp, IConfigurationSection section)
{
    var merged = new RampOptions
    {
        SamplesPerStep = baseRamp.SamplesPerStep,
        Jitter = baseRamp.Jitter,
        OnChallenge = baseRamp.OnChallenge,
        CooldownMinutes = baseRamp.CooldownMinutes,
    };
    section.Bind(merged); // IntervalsSeconds left null first: binding onto an existing array appends to it.
    merged.IntervalsSeconds ??= baseRamp.IntervalsSeconds;
    return merged;
}

// Relative output dirs land in the repo (the folder holding the .slnx), wherever the exe runs from.
static string ResolveOutputDir(string dir)
{
    if (Path.IsPathRooted(dir))
        return dir;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (d.EnumerateFiles("*.slnx").Any() || d.EnumerateFiles("*.sln").Any())
            return Path.Combine(d.FullName, dir);
    return Path.GetFullPath(dir);
}

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

sealed class AsyncDisposer(Action dispose) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        dispose();
        return ValueTask.CompletedTask;
    }
}
