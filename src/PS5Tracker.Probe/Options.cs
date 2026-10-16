namespace PS5Tracker.Probe;

public sealed class ProbeOptions
{
    public string OutputDir { get; set; } = "runs";
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
    public bool Headless { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 20;
    public decimal MaxPrice { get; set; } = 899.99m;

    /// <summary>Consecutive non-challenge failures (5xx, network errors, unparsable pages) that count as a block.</summary>
    public int MaxConsecutiveErrors { get; set; } = 3;

    public int? MaxRunMinutes { get; set; }
    public RampOptions DefaultRamp { get; set; } = new();
    public CredentialOptions Credentials { get; set; } = new();
    public List<TargetOptions> Targets { get; set; } = [];

    /// <summary>Named runs (--profile): which targets to run and what to override on them.</summary>
    public Dictionary<string, ProfileOptions> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProfileOptions
{
    public bool? Headless { get; set; }
    public int? MaxRunMinutes { get; set; }

    /// <summary>Keyed by target name. Ramp fields set here override the target's own ramp.</summary>
    public Dictionary<string, TargetOverride> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TargetOverride
{
    public RampOptions? Ramp { get; set; }
    public int? SettleMs { get; set; }
}

public enum ChallengeAction
{
    /// <summary>Stop probing the target for the rest of the run.</summary>
    Stop,

    /// <summary>Pause, then run one final step at the last clean interval and stop.</summary>
    CooldownThenHold,
}

public sealed class RampOptions
{
    public static readonly int[] DefaultIntervals = [120, 60, 30, 15];

    /// <summary>Descending poll intervals; each step runs <see cref="SamplesPerStep"/> samples.</summary>
    public int[]? IntervalsSeconds { get; set; }
    public int SamplesPerStep { get; set; } = 10;
    public double Jitter { get; set; } = 0.15;
    public ChallengeAction OnChallenge { get; set; } = ChallengeAction.Stop;
    public int CooldownMinutes { get; set; } = 60;

    public int[] Intervals => IntervalsSeconds is { Length: > 0 } i ? i : DefaultIntervals;
}

public sealed class CredentialOptions
{
    public string? BestBuyApiKey { get; set; }
    public string? WalmartConsumerId { get; set; }
    public string WalmartKeyVersion { get; set; } = "1";
    public string? WalmartPrivateKeyPath { get; set; }
}

public enum ProbeMethod
{
    Http,
    Browser,
    BestBuyApi,
    WalmartAffiliateApi,
}

public sealed class TargetOptions
{
    public string Name { get; set; } = "";
    public string Retailer { get; set; } = "";
    public ProbeMethod Method { get; set; }
    public string Url { get; set; } = "";

    /// <summary>Link to put in an alert; defaults to <see cref="Url"/>.</summary>
    public string? ProductUrl { get; set; }
    public string Parser { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Params { get; set; } = [];

    /// <summary>HTTP only: extra request headers.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Seller names (as the parser reports them) that count as the retailer itself.</summary>
    public string[] AllowedSellers { get; set; } = [];
    public RampOptions? Ramp { get; set; }

    /// <summary>Browser only: selector to wait for after DOMContentLoaded (up to <see cref="SettleMs"/>).</summary>
    public string? WaitForSelector { get; set; }
    public int SettleMs { get; set; } = 3000;

    public string Param(string key) =>
        Params.TryGetValue(key, out var v) ? v : throw new InvalidOperationException($"Target '{Name}' needs Params:{key}");

    public string? ParamOrNull(string key) => Params.TryGetValue(key, out var v) ? v : null;
}
