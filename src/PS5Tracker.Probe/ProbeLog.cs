using System.Globalization;
using System.Text;

namespace PS5Tracker.Probe;

public sealed record SampleRecord(
    DateTimeOffset Timestamp,
    TargetOptions Target,
    int Step,
    int IntervalSeconds,
    int Sample,
    FetchResult Fetch,
    ChallengeKind Challenge,
    ParseResult Parse,
    AlertDecision Alert,
    bool Changed);

/// <summary>Appends one CSV row per sample and keeps body snapshots for anything worth re-reading.</summary>
public sealed class ProbeLog : IDisposable
{
    static readonly string[] Columns =
    [
        "timestamp_utc", "target", "retailer", "method", "step", "interval_s", "sample",
        "http_status", "latency_ms", "bytes", "challenge", "signal_parsed", "in_stock", "raw_signal",
        "seller", "price", "condition", "alert_eligible", "alert_reason", "changed",
        "cache_headers", "final_url", "error", "note",
    ];

    readonly StreamWriter _csv;
    readonly Lock _lock = new();

    public string RunDir { get; }

    public ProbeLog(string runDir)
    {
        RunDir = runDir;
        Directory.CreateDirectory(runDir);
        _csv = new StreamWriter(Path.Combine(runDir, "samples.csv"), append: false, Encoding.UTF8) { AutoFlush = true };
        _csv.WriteLine(string.Join(',', Columns));
    }

    public void Write(SampleRecord r)
    {
        var f = r.Fetch;
        var p = r.Parse;
        string[] row =
        [
            r.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            r.Target.Name, r.Target.Retailer, r.Target.Method.ToString(),
            Num(r.Step), Num(r.IntervalSeconds), Num(r.Sample),
            f.Status?.ToString(CultureInfo.InvariantCulture) ?? "", Num(f.LatencyMs), Num(Encoding.UTF8.GetByteCount(f.Body)),
            r.Challenge.ToString(), Bool(p.SignalFound), p.InStock is { } s ? Bool(s) : "", p.RawSignal ?? "",
            p.Seller ?? "", p.Price?.ToString(CultureInfo.InvariantCulture) ?? "", p.Condition ?? "",
            Bool(r.Alert.Eligible), r.Alert.Reason, Bool(r.Changed),
            string.Join("; ", f.Headers.Select(h => $"{h.Key}={h.Value}")), f.FinalUrl, f.Error ?? "", p.Note ?? "",
        ];
        var line = string.Join(',', row.Select(Escape));
        lock (_lock)
            _csv.WriteLine(line);
    }

    public string Snapshot(string target, string reason, string body)
    {
        var dir = Path.Combine(RunDir, "snapshots", target);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{reason}.html");
        File.WriteAllText(path, body);
        return path;
    }

    public void Dispose() => _csv.Dispose();

    static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
    static string Bool(bool b) => b ? "true" : "false";

    static string Escape(string v) =>
        v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
}
