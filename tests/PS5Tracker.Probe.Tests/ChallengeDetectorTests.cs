namespace PS5Tracker.Probe.Tests;

public class ChallengeDetectorTests
{
    static FetchResult R(int? status, string body, string url = "https://example.com/p", bool timedOut = false) =>
        new(status, 100, body, url, timedOut, null, FetchResult.NoHeaders);

    [Fact]
    public void Target_perimeterx_435() =>
        Assert.Equal(ChallengeKind.PerimeterX, ChallengeDetector.Detect(R(435,
            "<title>Access to this page has been denied</title><meta name=\"description\" content=\"px-captcha\">")));

    [Fact]
    public void PerimeterX_script_on_a_normal_page_is_not_a_challenge() =>
        Assert.Equal(ChallengeKind.None, ChallengeDetector.Detect(R(200, "<title>PS5 Pro</title><script>window._pxAppId='x'; // px-captcha</script>")));

    [Fact]
    public void Cloudflare_403_challenge() =>
        Assert.Equal(ChallengeKind.Cloudflare, ChallengeDetector.Detect(R(403, "<title>Just a moment...</title><script src=\"/cdn-cgi/challenge-platform/h/b\"></script>")));

    [Fact]
    public void Cloudflare_beacon_on_a_200_page_is_fine() =>
        Assert.Equal(ChallengeKind.None, ChallengeDetector.Detect(R(200, "<title>GameStop</title><script src=\"/cdn-cgi/challenge-platform/scripts/jsd/main.js\"></script>")));

    [Fact]
    public void Amazon_checkpoint_by_url() =>
        Assert.Equal(ChallengeKind.AmazonCheckpoint, ChallengeDetector.Detect(R(200, "<html></html>", "https://www.amazon.com/checkpoint/continue?token=x")));

    [Fact]
    public void Walmart_404_with_data_is_not_a_challenge() =>
        Assert.Equal(ChallengeKind.None, ChallengeDetector.Detect(R(404, "<script id=\"__NEXT_DATA__\">{}</script>")));

    [Fact]
    public void Walmart_412_perimeterx_json() =>
        Assert.Equal(ChallengeKind.WalmartBlock, ChallengeDetector.Detect(R(412,
            """{"redirectUrl":"/blocked?url=Lw==","appId":"PXu6b0qd2S","blockScript":"/px/PXu6b0qd2S/captcha/captcha.js"}""")));

    [Fact]
    public void BestBuy_http2_reset_is_akamai() =>
        Assert.Equal(ChallengeKind.Akamai, ChallengeDetector.Detect(
            new FetchResult(null, 100, "", "https://www.bestbuy.com/x", false, "net::ERR_HTTP2_PROTOCOL_ERROR at https://www.bestbuy.com/x", FetchResult.NoHeaders)));

    [Fact]
    public void Timeout_counts_as_block() =>
        Assert.Equal(ChallengeKind.Timeout, ChallengeDetector.Detect(R(null, "", timedOut: true)));

    [Fact]
    public void Status_429_is_rate_limited() =>
        Assert.Equal(ChallengeKind.RateLimited, ChallengeDetector.Detect(R(429, "slow down")));
}
