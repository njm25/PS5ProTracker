# Phase 1 probe: 2026-10-15 (evening ET)

One-shot recon, not rate-limit testing. Each site got one page load in a real browser (fresh profile, signed out, ZIP 32308), plus one plain `curl` request with a Chrome user agent. Everything was out of stock, so the in-stock values below are inferred. The out-of-stock values were observed.

## Summary

| Retailer | Corrected ID / URL | Best method | Stock signal (observed OOS value) | Seller / condition check | Price seen | Plain HTTP | Bot protection seen |
|---|---|---|---|---|---|---|---|
| Best Buy | SKU 6601524 | Products API (or Playwright) | Button `data-testid="pdp-sold-out-6601524"`, `disabled`. Expect `pdp-add-to-cart-6601524` when in stock. | JSON-LD `seller.name == "Best Buy"`, `itemCondition NewCondition` | $899.99 | **Hangs** (0 bytes, 20 s timeout) | Akamai stalls non-browser clients |
| Amazon | ASIN B0DGY63Z2H | Creators API only | n/a: page never reached | API `MerchantInfo.Name == "Amazon.com"` | n/a | not tried | **`/checkpoint/continue` interstitial on first load** |
| Walmart | item 11839818124 (the ID is right) | Plain HTTP + `__NEXT_DATA__`, or Affiliate API | `props.pageProps.initialData.data.product.availabilityStatus = "OUT_OF_STOCK"` | `sellerName` / `sellerType` (both null while there's no offer) | none (no offer) | **404 but full body**; data still present | none on 1 request |
| PlayStation Direct | **SKU 1000050928**, URL **`/en-us/buy-consoles/playstation5-pro-console-2-tb`** (the old URL 404s) | Plain HTTP JSON API | `products[0].stock.stockLevelStatus = "outOfStock"` | Sony is the only seller | **$899.00** | 200, 0.33 s | none; response is `Cache-Control: public, max-age=120` |
| Sam's Club | item 18717057017 | Plain HTTP + `__NEXT_DATA__` | `availabilityStatus = "OUT_OF_STOCK"` (Walmart platform) | `sellerType = "INTERNAL"` | $899 | 200, 0.77 s | none on 1 request |
| Target | TCIN 93620188 | Playwright (page) | No price, no Add to cart, no "Out of Stock" text rendered at all | `relationship_type_code: "SA"` (sold by Target) in page data | none shown | not tried | **PerimeterX: redsky fulfillment API returned HTTP 435 + `px-captcha` on 1st call** |
| GameStop | pid 20015604; **New variant SKU 416188** | Playwright | JSON-LD Offer `name:"New"`, `availability OutOfStock`; `.product-availability[data-available="false"]`; ATC button text "Not Available", `disabled` | Offer `seller.name == "GameStop"`, `itemCondition NewCondition` | $899.99 New; Pre-Owned/Refurb $1,329.99 | **403 Cloudflare challenge** | Cloudflare |
| Newegg | **N82E16868110346** (new; open box = `…R`) | Playwright | `window.__initialState__.ItemDetail.Instock` | **Buy box is a marketplace seller**: `Seller.SellerName "Ozeal Electronics"`, `SellerType "D"`, item `9SIAN2HKUK0984`, $1,599, `Instock: true`. Only alert when the item number starts `N82E` / seller is Newegg. | $1,599 (3P) | **403 Cloudflare challenge** | Cloudflare |

## Notes per site

**Best Buy**
- JSON-LD says `availability: InStock` while the page shows Sold Out, so **don't use JSON-LD here**.
- Matching on "Add to cart" text gives false positives: the recommendation carousel has ~18 enabled `carousel-add-to-cart-<othersku>` buttons. Match on the SKU-specific `data-testid` instead.
- The default store was "Southlake", not Tallahassee. Set the store if pickup matters.

**Amazon**
- A fresh, signed-out browser got the "Continue shopping" checkpoint before the product page. I didn't click through it. Plan on the Creators API; page scraping from your IP will hit this fast.

**Walmart**
- The product page renders "Uh-oh..." with an empty `name` when Walmart has no offer, but the JSON still carries `availabilityStatus`. Plain HTTP returns **404** in this state. **Don't treat 404 as an error** for this item.
- The body contains several `availabilityStatus` keys (related items). Parse the `data.product` object specifically.
- Search results are all marketplace (HyperTech $1,404–$1,959, Antonline bundle). Require `sellerName` to be Walmart.com.
- An older search snippet shows this item "Sold and shipped by Walmart.com" at $749. That matches the "$749 cached" note in the goals doc.

**PlayStation Direct**
- The API is `GET https://api.direct.playstation.com/commercewebservices/ps-direct-us/users/anonymous/products/productList?fields=BASIC&productCodes=1000050928`. No auth, and the page itself calls it.
- `purchasable` is `true` while out of stock, so **ignore it**. `maxOrderQuantity` was 0 (may flip with stock).
- The CDN cache is `max-age=120`. Polling faster than ~2 minutes may just return cached data; measure whether a cache-busting query param changes `Age` / freshness.
- No queue was visible today. The page JSON-LD also shows `OutOfStock` over plain HTTP as a second signal.

**Sam's Club**
- Same Next.js / `__NEXT_DATA__` shape as Walmart. Store 6279 (Tallahassee).
- `isConditionNew` was `false` even though the item is new, so that field is unreliable; rely on `sellerType` and the item ID.

**Target**
- Target is **PerimeterX/HUMAN** protected (this wasn't confirmed in the goals doc). The page HTML loaded, but a single direct call to `redsky.target.com/.../product_fulfillment_and_variation_hierarchy_v1` was challenged. I stopped all Target requests after that.
- Out-of-stock state today = no price and no buy button rendered. The goals doc's "Out of Stock" / "Notify me" text didn't appear.

**GameStop**
- The pid page holds all conditions. Pick the JSON-LD Offer with `itemCondition NewCondition`, or the SKU `416188` offer URL.

## Harness smoke run (22:42 ET, `--once`, headless Chromium)

| Target | Result |
|---|---|
| psdirect-api | 200, 168 ms, `outOfStock`, $899 |
| psdirect-page | 200, 193 ms, microdata `OutOfStock` (the plain-HTTP page uses microdata, not JSON-LD) |
| samsclub-page | 200, 786 ms, `OUT_OF_STOCK`, seller INTERNAL, $899 |
| walmart-page | **Redirected to marketplace item 18235967161 (HyperTech), then 412 PerimeterX block.** This was Walmart's ~4th request from this IP today, and the earlier curl got a 404 with data. Walmart is sensitive; use the affiliate API. |
| bestbuy-page | `net::ERR_HTTP2_PROTOCOL_ERROR`: Akamai resets headless Chromium |
| gamestop-page | 403 Cloudflare (headless), though the headed in-app browser loaded fine earlier |
| newegg-page | 403 Cloudflare (headless), same as GameStop |
| target-page | 200, empty buy box → out of stock |

Headless Chromium is rejected by all three Akamai/Cloudflare sites. Headed mode (`--headed`) is the next thing to measure for those, starting at the slow end of the ramp and not before a few hours have passed.

## First full ramp run (2026-10-15 22:44 → 10-16 00:18 ET, headless, default ramps)

`runs/20261015-224443` (written under `bin/` before the output-dir fix). 157 samples. Nothing came in stock, so no change was observed and freshness is still unmeasured.

| Target | Fastest clean interval | Samples | p50 / p95 latency | Notes |
|---|---|---|---|---|
| psdirect-api | **5s** (fastest tried) | 48 | 36–107 / up to 287 ms | `cache-control: public, max-age=120`; no `Age` header, so whether responses were cached can't be seen |
| psdirect-page | **30s** (fastest tried) | 30 | 79–228 / up to 627 ms | `server: Apache`, no cache headers |
| samsclub-page | **15s** (fastest tried) | 40 | 540–685 / up to 1,283 ms | `no-store` |
| walmart-page | **60s** (fastest tried) | 18 | 716–906 / up to 1,323 ms | all 404-with-data on the correct item; no redirect, no 412 this time |
| target-page | **120s** (fastest tried) | 18 | 542–778 / up to 1,102 ms | empty buy box each time |
| bestbuy-page | none | 1 | n/a | headless: HTTP/2 reset (Akamai) on first request |
| gamestop-page | none | 1 | n/a | headless: 403 Cloudflare on first request |
| newegg-page | none | 1 | n/a | headless: 403 Cloudflare on first request |

None of the HTTP targets or Target tripped, so these are floors that held, not limits.

**10-16 00:33:** a one-off check of `psdirect-api` and the new cache-bust variant got **Akamai 403 "Access Denied"** on both. That request carried Akamai debug `Pragma` headers (since removed). Unclear whether those headers or the earlier 5s polling caused it. Recheck once after a few hours before running the `fast` profile.

## Implications for Phase 2
- **Plain HTTP pollers:** PS Direct (API), Walmart, Sam's Club. These are cheap and fast.
- **Official APIs:** Best Buy, Amazon. Get the keys first.
- **Playwright pollers:** GameStop, Newegg (Cloudflare), Target (PerimeterX), and Best Buy as a fallback. Keep these intervals conservative: Target and Amazon already challenged after 1–2 requests.
- Still unmeasured: actual rate limits, latency under repeated polling, and how fast each source flips when stock lands. That's the next step and needs the harness.
