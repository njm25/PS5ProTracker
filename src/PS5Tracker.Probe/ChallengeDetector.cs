using System.Text.RegularExpressions;

namespace PS5Tracker.Probe;

/// <summary>
/// Classifies a response as a bot-protection challenge or block. Several vendors inject their
/// scripts into normal pages too, so marker strings only count alongside an error status or a
/// challenge-page title.
/// </summary>
public static partial class ChallengeDetector
{
    public static ChallengeKind Detect(FetchResult r)
    {
        if (r.TimedOut)
            return ChallengeKind.Timeout;

        // Akamai (Best Buy) resets the stream on clients it has classified as bots.
        if (r.Error?.Contains("ERR_HTTP2_PROTOCOL_ERROR", StringComparison.Ordinal) == true)
            return ChallengeKind.Akamai;

        var body = r.Body ?? "";
        var title = Title(body) ?? "";
        var url = r.FinalUrl ?? "";
        var errorStatus = r.Status is >= 400 and not 404;

        if (url.Contains("/checkpoint/", StringComparison.OrdinalIgnoreCase)
            || body.Contains("please click the \"Continue shopping\" button", StringComparison.OrdinalIgnoreCase))
            return ChallengeKind.AmazonCheckpoint;

        if (body.Contains("validateCaptcha", StringComparison.Ordinal)
            || body.Contains("make sure you're not a robot", StringComparison.OrdinalIgnoreCase))
            return ChallengeKind.AmazonCaptcha;

        // Walmart/Sam's: a redirect to /blocked, the "Robot or human?" page, or a 412 whose JSON
        // body carries PerimeterX's redirectUrl/blockScript.
        if (url.Contains("/blocked?", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Robot or human", StringComparison.OrdinalIgnoreCase)
            || (errorStatus && body.Contains("\"blockScript\"", StringComparison.Ordinal)))
            return ChallengeKind.WalmartBlock;

        if (body.Contains("px-captcha", StringComparison.Ordinal)
            && (errorStatus || title.Contains("Access to this page has been denied", StringComparison.OrdinalIgnoreCase)))
            return ChallengeKind.PerimeterX;

        if (title.StartsWith("Just a moment", StringComparison.OrdinalIgnoreCase)
            || (r.Status is 403 or 429 or 503
                && (body.Contains("challenge-platform", StringComparison.Ordinal)
                    || body.Contains("cf-chl", StringComparison.Ordinal)
                    || body.Contains("cf_chl", StringComparison.Ordinal))))
            return ChallengeKind.Cloudflare;

        if (r.Status == 403
            && body.Contains("Access Denied", StringComparison.OrdinalIgnoreCase)
            && (body.Contains("Reference", StringComparison.Ordinal) || body.Contains("edgesuite", StringComparison.Ordinal)))
            return ChallengeKind.Akamai;

        return r.Status switch
        {
            429 => ChallengeKind.RateLimited,
            403 or 412 or 435 => ChallengeKind.Forbidden,
            _ => ChallengeKind.None,
        };
    }

    public static string? Title(string html)
    {
        var m = TitleRegex().Match(html);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();
}
