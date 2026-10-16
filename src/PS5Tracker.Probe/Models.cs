namespace PS5Tracker.Probe;

public enum ChallengeKind
{
    None,
    RateLimited,
    Forbidden,
    PerimeterX,
    Cloudflare,
    Akamai,
    AmazonCaptcha,
    AmazonCheckpoint,
    WalmartBlock,
    Timeout,
}

public sealed record FetchResult(
    int? Status,
    long LatencyMs,
    string Body,
    string FinalUrl,
    bool TimedOut,
    string? Error,
    IReadOnlyDictionary<string, string> Headers)
{
    public static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();
}

public sealed record ParseResult(
    bool SignalFound,
    bool? InStock,
    string? RawSignal,
    string? Seller,
    decimal? Price,
    string? Condition,
    string? Note = null)
{
    public static ParseResult NotFound(string note) => new(false, null, null, null, null, null, note);

    /// <summary>What we compare between samples to spot a change.</summary>
    public string Fingerprint => $"{InStock}|{RawSignal}|{Seller}|{Price}|{Condition}";
}

public sealed record AlertDecision(bool Eligible, string Reason);
