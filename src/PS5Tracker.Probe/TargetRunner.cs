using PS5Tracker.Probe.Parsers;

namespace PS5Tracker.Probe;

public sealed class TargetSummary(TargetOptions target)
{
    public TargetOptions Target { get; } = target;
    public int Samples { get; set; }
    public int Parsed { get; set; }
    public int Changes { get; set; }
    public int Alerts { get; set; }
    public int? LastCleanInterval { get; set; }
    public int? TrippedAtInterval { get; set; }
    public string? TripReason { get; set; }
    public bool? HoldClean { get; set; }
    public string? Skipped { get; set; }
    public List<long> Latencies { get; } = [];

    public long? Percentile(double p)
    {
        if (Latencies.Count == 0)
            return null;
        var sorted = Latencies.Order().ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(p * sorted.Count) - 1)];
    }
}

/// <summary>
/// Walks one target down its interval ramp. The first challenge (or a run of plain failures)
/// trips the target: by default it stops for the rest of the run, so a block never repeats.
/// </summary>
public sealed class TargetRunner(
    TargetOptions target,
    ProbeOptions options,
    ProbeLog log,
    Func<CancellationToken, Task<FetchResult>> fetch,
    IStockParser parser,
    bool once)
{
    enum StepOutcome { Clean, Tripped }

    readonly TargetSummary _summary = new(target);
    readonly RampOptions _ramp = target.Ramp ?? options.DefaultRamp;
    readonly CancellationTokenSource _stop = new();
    volatile string? _stopReason;
    DateTimeOffset? _lastStart;
    string? _lastFingerprint;
    int _consecutiveErrors;
    bool _snappedNoParse;

    public TargetOptions Target => target;

    /// <summary>Raised when this target trips, with the reason.</summary>
    public event Action<TargetRunner, string>? Tripped;

    /// <summary>Stops this target from outside, e.g. because a sibling on the same site tripped.</summary>
    public void StopBecause(string reason)
    {
        _stopReason ??= reason;
        _stop.Cancel();
    }

    public async Task<TargetSummary> RunAsync(CancellationToken runToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(runToken, _stop.Token);
        var ct = linked.Token;
        int[] intervals = once ? [0] : _ramp.Intervals;
        var samples = once ? 1 : _ramp.SamplesPerStep;
        try
        {
            for (var step = 0; step < intervals.Length; step++)
            {
                if (await RunStepAsync(step + 1, intervals[step], samples, ct) == StepOutcome.Clean)
                {
                    _summary.LastCleanInterval = intervals[step];
                    continue;
                }

                if (_ramp.OnChallenge == ChallengeAction.CooldownThenHold && !once)
                {
                    var hold = _summary.LastCleanInterval ?? intervals[step] * 4;
                    Say($"cooling down {_ramp.CooldownMinutes} min, then one hold step at {hold}s");
                    await Task.Delay(TimeSpan.FromMinutes(_ramp.CooldownMinutes), ct);
                    _consecutiveErrors = 0;
                    _lastStart = null;
                    _summary.HoldClean = await RunStepAsync(step + 2, hold, samples, ct) == StepOutcome.Clean;
                }
                break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (_stopReason is { } reason)
            {
                _summary.TripReason ??= $"stopped: {reason}";
                Say($"stopped: {reason}");
            }
            else
            {
                Say("stopped (cancelled)");
            }
        }
        return _summary;
    }

    async Task<StepOutcome> RunStepAsync(int step, int interval, int samples, CancellationToken ct)
    {
        for (var s = 1; s <= samples; s++)
        {
            await WaitForSlotAsync(interval, ct);
            if (await SampleAsync(step, interval, s, ct) is { } trip)
            {
                _summary.TrippedAtInterval = interval;
                _summary.TripReason = trip;
                Say($"TRIPPED at {interval}s: {trip}. Backing off.");
                Tripped?.Invoke(this, trip);
                return StepOutcome.Tripped;
            }
        }
        return StepOutcome.Clean;
    }

    /// <summary>Spaces sample starts by the interval (± jitter), however long each fetch took.</summary>
    async Task WaitForSlotAsync(int interval, CancellationToken ct)
    {
        if (_lastStart is { } last && interval > 0)
        {
            var jitter = 1 + (Random.Shared.NextDouble() * 2 - 1) * _ramp.Jitter;
            var due = last + TimeSpan.FromSeconds(interval * jitter);
            var wait = due - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
        }
        _lastStart = DateTimeOffset.UtcNow;
    }

    /// <returns>A trip reason, or null when the target may keep going.</returns>
    async Task<string?> SampleAsync(int step, int interval, int sample, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var f = await fetch(ct);
        var challenge = ChallengeDetector.Detect(f);
        var parse = challenge == ChallengeKind.None
            ? StockParsers.SafeParse(parser, f.Body, target)
            : ParseResult.NotFound($"challenge: {challenge}");
        var alert = AlertRule.Evaluate(parse, target, options.MaxPrice);
        var changed = parse.SignalFound && _lastFingerprint is not null && parse.Fingerprint != _lastFingerprint;
        if (parse.SignalFound)
            _lastFingerprint = parse.Fingerprint;

        log.Write(new SampleRecord(started, target, step, interval, sample, f, challenge, parse, alert, changed));
        _summary.Samples++;
        _summary.Latencies.Add(f.LatencyMs);
        if (parse.SignalFound)
            _summary.Parsed++;
        if (changed)
            _summary.Changes++;
        if (alert.Eligible)
            _summary.Alerts++;

        if (_summary.Samples == 1)
            log.Snapshot(target.Name, "first", f.Body);
        if (challenge != ChallengeKind.None)
            log.Snapshot(target.Name, $"challenge-{challenge}", f.Body);
        else if (changed)
            log.Snapshot(target.Name, "changed", f.Body);
        if (!parse.SignalFound && challenge == ChallengeKind.None && !_snappedNoParse)
        {
            _snappedNoParse = true;
            log.Snapshot(target.Name, "noparse", f.Body);
        }
        if (alert.Eligible)
            log.Snapshot(target.Name, "alert", f.Body);

        Say($"step {step} @{interval}s #{sample}: {f.Status?.ToString() ?? "---"} {f.LatencyMs}ms"
            + (challenge != ChallengeKind.None ? $" CHALLENGE={challenge}" : "")
            + (parse.SignalFound ? $" in_stock={parse.InStock} [{parse.RawSignal}] seller={parse.Seller} price={parse.Price}" : $" no-signal ({parse.Note ?? f.Error})")
            + (changed ? " CHANGED" : ""));
        if (alert.Eligible)
            Say($"*** WOULD ALERT: in stock at {parse.Seller} for {parse.Price} -> {target.ProductUrl ?? target.Url}");

        if (challenge != ChallengeKind.None)
            return challenge.ToString();

        var failed = !parse.SignalFound || f.Status is null or >= 500;
        _consecutiveErrors = failed ? _consecutiveErrors + 1 : 0;
        return _consecutiveErrors >= options.MaxConsecutiveErrors
            ? $"{_consecutiveErrors} consecutive failures (last: {f.Error ?? parse.Note ?? f.Status?.ToString()})"
            : null;
    }

    void Say(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {target.Name,-16} {message}");
}
