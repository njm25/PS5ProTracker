# PS5 Pro Tracker

Detects when a PS5 Pro is in stock at retail price, sold by the retailer itself, and New. Buying is manual. Goals and scope live in the original goals doc; findings are in [docs/](docs/).

This is **Phase 1**: a measurement harness, `src/PS5Tracker.Probe`. It polls each retailer on a slowly tightening interval and logs what happens: status, latency, blocks, and whether the stock signal parsed. Phase 2 pollers then get intervals from real data.

## Run it

```bash
dotnet run --project src/PS5Tracker.Probe -- --install-browsers   # once: Playwright Chromium
dotnet run --project src/PS5Tracker.Probe -- --list               # show targets
dotnet run --project src/PS5Tracker.Probe -- --once               # one sample per enabled target
dotnet run --project src/PS5Tracker.Probe -- --only psdirect-api,samsclub-page
dotnet run --project src/PS5Tracker.Probe -- --headed             # visible Chromium for browser targets
dotnet run --project src/PS5Tracker.Probe                         # full ramp, all enabled targets
dotnet run --project src/PS5Tracker.Probe -- --profile fast       # push below the measured clean floors
dotnet run --project src/PS5Tracker.Probe -- --profile headed     # Best Buy / GameStop / Newegg in visible Chromium
```

Ctrl+C stops cleanly and still writes the summary. `MaxRunMinutes` (default 180) caps a full run.

### Profiles

`Profiles` in appsettings picks targets by name and overrides their ramp, `SettleMs`, `Headless` or `MaxRunMinutes`. Disabled targets still run if a profile names them. `--only` takes precedence over a profile's target list.

- **fast**: starts each target at the fastest interval that was clean on 2026-10-15 and goes lower. It also runs `psdirect-api-bust` next to `psdirect-api`. The bust variant has a unique `_={nonce}` per request and sends `Cache-Control: no-cache`, so the two can be compared for CDN staleness.
- **headed**: Best Buy, GameStop and Newegg in a visible Chromium window, from 300s down to 30s. Headless was rejected by all three.

When any target trips, other targets with the same `Retailer` stop as well, since they share this IP's standing with that site.

Output goes to `runs/<timestamp>[-profile]/` at the repo root, wherever the exe is launched from (git-ignored):
- `samples.csv`: one row per request. Columns: timestamp, method, interval, HTTP status, latency, challenge, signal parsed, in stock, raw signal, seller, price, condition, would-alert and why, changed-since-last, cache headers, error.
- `summary.csv`: per target, the last clean interval, the interval and reason it tripped at, and p50/p95 latency.
- `snapshots/<target>/`: page bodies for the first sample, any challenge, any signal change, the first unparsable page, and anything that would alert.

## How the ramp and backoff work

Each target walks its `IntervalsSeconds` from slowest to fastest, taking `SamplesPerStep` samples at each step (±15% jitter). A target **trips** on the first challenge or block, or after `MaxConsecutiveErrors` (3) plain failures in a row.

- `OnChallenge: "Stop"` (default): the target is done for the run. The last clean interval is your measured safe rate.
- `OnChallenge: "CooldownThenHold"`: waits `CooldownMinutes`, runs one step at the last clean interval to check that it holds, then stops.

These count as a challenge or block: 429, 403, 412, 435, timeouts, Cloudflare "Just a moment" pages, PerimeterX captcha pages and 412 JSON blocks, Walmart `/blocked`, Akamai access-denied pages and HTTP/2 stream resets, and Amazon captcha and checkpoint pages. Vendor scripts that appear on normal 200 pages are not counted.

## Config

Settings live in `src/PS5Tracker.Probe/appsettings.json`. Put secrets in `appsettings.Local.json` next to it (git-ignored), or in environment variables like `PS5_Probe__Credentials__BestBuyApiKey`.

```json
{ "Probe": { "Credentials": {
  "BestBuyApiKey": "...",
  "WalmartConsumerId": "...", "WalmartKeyVersion": "1", "WalmartPrivateKeyPath": "C:/keys/walmart.pem"
} } }
```

A target's `Ramp` overrides only the fields it sets; the rest come from `DefaultRamp`. API targets without credentials are skipped.

| Target | Method | Signal | Status |
|---|---|---|---|
| `psdirect-api` | HTTP JSON | `stock.stockLevelStatus` | clean at 5s on 10-15; **Akamai 403 at 00:33 on 10-16** |
| `psdirect-api-bust` | HTTP JSON, unique URL + no-cache | same | off by default; in the `fast` profile |
| `psdirect-page` | HTTP | schema.org microdata `availability` | works over plain HTTP |
| `samsclub-page` | HTTP | `__NEXT_DATA__` `availabilityStatus` + `sellerType` | works over plain HTTP |
| `walmart-page` | HTTP | same as Sam's; flags a redirect to another item | **PerimeterX-blocked (412) on 2026-10-15** |
| `walmart-api` | Walmart.io affiliate API, RSA-signed | `availableOnline`, `stock`, `sellerInfo` | needs keys; field names unverified |
| `bestbuy-api` | Best Buy Products API | `onlineAvailability`, `orderable` | needs a key |
| `bestbuy-page` | Playwright | `data-testid="pdp-…-6601524"` button | headless reset by Akamai; try `--headed` |
| `target-page` | Playwright | Target buy buttons; empty buy box = out of stock | works headless; in-stock markup unverified |
| `gamestop-page` | Playwright | New JSON-LD offer (SKU 416188) + add-to-cart button | headless gets Cloudflare 403; try `--headed` |
| `newegg-page` | Playwright | `__initialState__.ItemDetail` + seller must be Newegg | headless gets Cloudflare 403; try `--headed` |
| `amazon-page` | Playwright | `#availability`, buy box seller | **disabled**: checkpoint on first load |

Not built: an Amazon Creators API (OffersV2) poller. Its auth and request format still need checking against the docs once there's an Associates account.

The browser targets use a fresh, throwaway Chromium profile, never your signed-in sessions, and no fingerprint spoofing.

## Tests

```bash
dotnet test
```

Parser and challenge-detector tests use small fixtures shaped like what each site returned on 2026-10-15.

## Git

Commits are authored as `njm25 <njm25@users.noreply.github.com>`, unsigned. This is set in the repo's local git config.
