using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace PS5Tracker.Probe.Fetching;

/// <summary>Plain HTTP GET with its own cookie jar, so each target looks like one steady visitor.</summary>
public sealed class HttpFetcher : IDisposable
{
    readonly HttpClient _client;
    readonly TimeSpan _timeout;
    readonly string _userAgent;

    public HttpFetcher(ProbeOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        _userAgent = options.UserAgent;
    }

    public async Task<FetchResult> FetchAsync(
        string url, IReadOnlyDictionary<string, string>? extraHeaders, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        foreach (var (k, v) in extraHeaders ?? FetchResult.NoHeaders)
        {
            req.Headers.Remove(k);
            req.Headers.TryAddWithoutValidation(k, v);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await resp.Content.ReadAsStringAsync(timeout.Token);
            return new FetchResult(
                (int)resp.StatusCode, sw.ElapsedMilliseconds, body,
                resp.RequestMessage?.RequestUri?.ToString() ?? url, false, null,
                PickHeaders(resp.Headers.Concat(resp.Content.Headers)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchResult(null, sw.ElapsedMilliseconds, "", url, true, "timeout", FetchResult.NoHeaders);
        }
        catch (HttpRequestException ex)
        {
            return new FetchResult(null, sw.ElapsedMilliseconds, "", url, false, ex.Message, FetchResult.NoHeaders);
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Headers that say something about caching, freshness or rate limiting.</summary>
    internal static IReadOnlyDictionary<string, string> PickHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) =>
        PickHeaders(headers.Select(h => new KeyValuePair<string, string>(h.Key, string.Join(",", h.Value))));

    internal static IReadOnlyDictionary<string, string> PickHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers)
        {
            var key = k.ToLowerInvariant();
            if (key is "age" or "cache-control" or "cf-cache-status" or "retry-after" or "server" or "last-modified"
                    or "date" or "expires" or "etag" or "via" or "x-served-by"
                || key.StartsWith("x-cache", StringComparison.Ordinal) || key.StartsWith("x-check-cacheable", StringComparison.Ordinal)
                || key.StartsWith("x-akamai", StringComparison.Ordinal) || key.StartsWith("akamai", StringComparison.Ordinal)
                || key.StartsWith("x-ratelimit", StringComparison.Ordinal) || key.StartsWith("ratelimit", StringComparison.Ordinal))
                picked[key] = v;
        }
        return picked;
    }
}
