using System.Diagnostics;
using Microsoft.Playwright;

namespace PS5Tracker.Probe.Fetching;

/// <summary>One Chromium process shared by every browser target.</summary>
public sealed class BrowserHost(ProbeOptions options) : IAsyncDisposable
{
    readonly SemaphoreSlim _gate = new(1, 1);
    IPlaywright? _playwright;
    IBrowser? _browser;

    public async Task<IBrowserContext> NewContextAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _playwright ??= await Playwright.CreateAsync();
            _browser ??= await _playwright.Chromium.LaunchAsync(new() { Headless = options.Headless });
        }
        finally
        {
            _gate.Release();
        }

        // A clean, throwaway profile: never the user's signed-in retailer sessions.
        return await _browser.NewContextAsync(new()
        {
            Locale = "en-US",
            TimezoneId = "America/New_York",
            ViewportSize = new() { Width = 1366, Height = 900 },
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}

/// <summary>Loads a page in a per-target browser context and returns the rendered HTML.</summary>
public sealed class BrowserFetcher(BrowserHost host, ProbeOptions options) : IAsyncDisposable
{
    IBrowserContext? _context;
    IPage? _page;

    public async Task<FetchResult> FetchAsync(TargetOptions target, CancellationToken ct)
    {
        _context ??= await host.NewContextAsync();
        _page ??= await _context.NewPageAsync();

        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _page.GotoAsync(target.Url, new()
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = options.RequestTimeoutSeconds * 1000f,
            });
            var navMs = sw.ElapsedMilliseconds;
            ct.ThrowIfCancellationRequested();

            if (target.WaitForSelector is { } selector)
            {
                try
                {
                    await _page.WaitForSelectorAsync(selector, new() { State = WaitForSelectorState.Attached, Timeout = target.SettleMs });
                }
                catch (TimeoutException)
                {
                    // Expected when the item is out of stock and the element never renders.
                }
            }
            else
            {
                await _page.WaitForTimeoutAsync(target.SettleMs);
            }

            var html = await _page.ContentAsync();
            var headers = resp is null ? FetchResult.NoHeaders : HttpFetcher.PickHeaders(resp.Headers);
            return new FetchResult(resp?.Status, navMs, html, _page.Url, false, null, headers);
        }
        catch (TimeoutException)
        {
            return new FetchResult(null, sw.ElapsedMilliseconds, "", target.Url, true, "navigation timeout", FetchResult.NoHeaders);
        }
        catch (PlaywrightException ex)
        {
            return new FetchResult(null, sw.ElapsedMilliseconds, "", target.Url, false, ex.Message.Split('\n')[0], FetchResult.NoHeaders);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();
    }
}
