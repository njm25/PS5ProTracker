using PS5Tracker.Probe.Parsers;

namespace PS5Tracker.Probe.Tests;

public class TargetRunnerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ps5probe-tests-" + Guid.NewGuid().ToString("N"));
    readonly ProbeOptions _options = new();

    const string InStockJson = """{"products":[{"price":{"value":899},"stock":{"stockLevelStatus":"outOfStock"}}]}""";

    static TargetOptions Target(string name) => new()
    {
        Name = name,
        Retailer = "Shop",
        Parser = "psdirect-api",
        Ramp = new RampOptions { IntervalsSeconds = [60], SamplesPerStep = 3, Jitter = 0 },
    };

    static Func<CancellationToken, Task<FetchResult>> Respond(int status, string body) =>
        _ => Task.FromResult(new FetchResult(status, 5, body, "https://shop.example/p", false, null, FetchResult.NoHeaders));

    [Fact]
    public async Task Challenge_trips_target_and_stops_sibling_on_same_retailer()
    {
        using var log = new ProbeLog(_dir);
        var parser = StockParsers.All["psdirect-api"];
        var blocked = new TargetRunner(Target("a"), _options, log, Respond(429, "slow down"), parser, once: false);
        var healthy = new TargetRunner(Target("b"), _options, log, Respond(200, InStockJson), parser, once: false);
        blocked.Tripped += (_, reason) => healthy.StopBecause($"a tripped ({reason})");

        var healthyRun = healthy.RunAsync(CancellationToken.None); // first sample, then waits 60s
        var a = await blocked.RunAsync(CancellationToken.None);
        var b = await healthyRun.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, a.Samples);
        Assert.Equal("RateLimited", a.TripReason);
        Assert.Equal(60, a.TrippedAtInterval);
        Assert.Equal(1, b.Samples);
        Assert.Equal("stopped: a tripped (RateLimited)", b.TripReason);
    }

    [Fact]
    public async Task Repeated_parse_failures_trip_after_the_limit()
    {
        using var log = new ProbeLog(_dir);
        var t = Target("c");
        t.Ramp = new RampOptions { IntervalsSeconds = [0], SamplesPerStep = 10 };
        var runner = new TargetRunner(t, _options, log, Respond(200, "{}"), StockParsers.All["psdirect-api"], once: false);

        var s = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(_options.MaxConsecutiveErrors, s.Samples);
        Assert.StartsWith("3 consecutive failures", s.TripReason);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
