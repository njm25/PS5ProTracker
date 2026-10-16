using System.Text.Json;
using System.Text.RegularExpressions;

namespace PS5Tracker.Probe.Parsers;

/// <summary>
/// Best Buy product page. The JSON-LD reported InStock while the page said Sold Out (2026-10-15),
/// so stock comes only from the SKU-specific buy button (data-testid="pdp-…-{sku}"). Carousel
/// "Add to cart" buttons for other SKUs are ignored.
/// </summary>
public sealed class BestBuyPageParser : IStockParser
{
    public string Name => "bestbuy-page";

    public ParseResult Parse(string body, TargetOptions t)
    {
        var sku = t.Param("Sku");
        var doc = Html.Doc(body);

        var offer = Html.JsonLd(doc)
            .Where(e => Html.IsType(e, "Product") && ((JsonElement?)e).Get("sku").Str() == sku)
            .SelectMany(Html.Offers)
            .Select(o => (JsonElement?)o)
            .FirstOrDefault();
        var seller = offer.Path("seller", "name").Str();
        var price = offer.Get("price").Dec();
        var condition = Html.Condition(offer.Get("itemCondition").Str());
        var ldNote = offer.Get("availability").Str() is { } a ? $"jsonld={a[(a.LastIndexOf('/') + 1)..]} (ignored)" : null;

        var buttons = doc.QuerySelectorAll("button[data-testid]")
            .Where(b => b.GetAttribute("data-testid") is { } id && id.StartsWith("pdp-", StringComparison.Ordinal) && id.EndsWith($"-{sku}", StringComparison.Ordinal))
            .ToList();
        if (buttons.Count == 0)
            return new ParseResult(false, null, null, seller, price, condition, $"no pdp-*-{sku} button; {ldNote}");

        var primary = buttons.FirstOrDefault(b => b.GetAttribute("data-testid")!.Contains("add-to-cart")) ?? buttons[0];
        var testId = primary.GetAttribute("data-testid")!;
        var disabled = Html.Disabled(primary);
        return new ParseResult(
            true, testId.Contains("add-to-cart") && !disabled, $"{testId}{(disabled ? " [disabled]" : "")}",
            seller, price, condition, ldNote);
    }
}

/// <summary>Best Buy Products API (developer.bestbuy.com). The API covers Best Buy's own catalog.</summary>
public sealed class BestBuyApiParser : IStockParser
{
    public string Name => "bestbuy-api";

    public ParseResult Parse(string body, TargetOptions t)
    {
        var root = Json.Parse(body);
        if ((root.Get("errorMessage").Str() ?? root.Path("error", "message").Str()) is { } err)
            return ParseResult.NotFound($"api error: {err}");
        var online = root.Get("onlineAvailability").Bool();
        if (online is null)
            return ParseResult.NotFound("no onlineAvailability");
        var orderable = root.Get("orderable").Str();
        return new ParseResult(
            true, online == true, $"onlineAvailability={online}; orderable={orderable}",
            "Best Buy", root.Get("salePrice").Dec(), root.Get("condition").Str());
    }
}

/// <summary>
/// Walmart and Sam's Club product pages (same Next.js platform): props.pageProps.initialData.data.product.
/// Walmart answers 404 with the data still present when it has no offer, so status is not checked here.
/// </summary>
public sealed class WalmartNextDataParser : IStockParser
{
    public string Name => "walmart-nextdata";

    public ParseResult Parse(string body, TargetOptions t)
    {
        var script = Html.Doc(body).QuerySelector("script#__NEXT_DATA__")?.TextContent;
        if (script is null)
            return ParseResult.NotFound("no __NEXT_DATA__");
        var product = Json.Parse(script).Path("props", "pageProps", "initialData", "data", "product");
        if (product is null)
            return ParseResult.NotFound("no data.product");
        var status = product.Get("availabilityStatus").Str();
        if (status is null)
            return ParseResult.NotFound("no availabilityStatus");

        // With no first-party offer, Walmart redirected the item to a marketplace listing (2026-10-15).
        var itemId = product.Get("usItemId").Str();
        if (t.ParamOrNull("ItemId") is { } expected && itemId is not null && itemId != expected)
            return new ParseResult(true, false, $"redirected to usItemId={itemId}", product.Get("sellerName").Str(),
                product.Path("priceInfo", "currentPrice", "price").Dec(), null, $"expected {expected}");

        var sellerType = product.Get("sellerType").Str();
        var seller = product.Get("sellerName").Str() ?? product.Get("sellerDisplayName").Str() ?? sellerType;
        var condition = product.Get("conditionType").Str()
            ?? (product.Get("isPreowned").Bool() == true ? "Pre-owned" : null);
        return new ParseResult(
            true, status == "IN_STOCK", $"availabilityStatus={status}",
            seller, product.Path("priceInfo", "currentPrice", "price").Dec(), condition,
            $"sellerType={sellerType}; showAtc={product.Get("showAtc").Str()}");
    }
}

/// <summary>Walmart.io affiliate Product Lookup (v2/items/{id}). Field names unverified against a live response.</summary>
public sealed class WalmartAffiliateApiParser : IStockParser
{
    public string Name => "walmart-affiliate-api";

    public ParseResult Parse(string body, TargetOptions t)
    {
        var root = Json.Parse(body);
        if (root.Path("errors").Items().FirstOrDefault() is { ValueKind: JsonValueKind.Object } e)
            return ParseResult.NotFound($"api error: {((JsonElement?)e).Get("message").Str()}");
        var online = root.Get("availableOnline").Bool();
        if (online is null)
            return ParseResult.NotFound("no availableOnline");
        var stock = root.Get("stock").Str();
        return new ParseResult(
            true, online == true && !string.Equals(stock, "Not available", StringComparison.OrdinalIgnoreCase),
            $"availableOnline={online}; stock={stock}",
            root.Get("sellerInfo").Str(), root.Get("salePrice").Dec(), root.Get("condition").Str());
    }
}

/// <summary>
/// PlayStation Direct productList API. stock.stockLevelStatus is the signal; "purchasable" stays
/// true while out of stock, so it is only logged.
/// </summary>
public sealed class PsDirectApiParser : IStockParser
{
    public string Name => "psdirect-api";

    public ParseResult Parse(string body, TargetOptions t)
    {
        var product = Json.Parse(body).Get("products").Items().Select(p => (JsonElement?)p).FirstOrDefault();
        var status = product.Path("stock", "stockLevelStatus").Str();
        if (status is null)
            return ParseResult.NotFound("no stock.stockLevelStatus");
        return new ParseResult(
            true, status is "inStock" or "lowStock",
            $"stockLevelStatus={status}; maxOrderQuantity={product.Get("maxOrderQuantity").Str()}; purchasable={product.Get("purchasable").Str()}",
            "PlayStation Direct", product.Path("price", "value").Dec(), "New");
    }
}

/// <summary>
/// Generic schema.org Offer reader: JSON-LD first, then microdata (PS Direct's server-rendered page
/// uses itemprop="availability"). Params: Sku (optional, matches offer or product sku),
/// DefaultSeller (used when the offer names none). Prefers a New-condition offer.
/// </summary>
public sealed class JsonLdOfferParser : IStockParser
{
    public string Name => "schema-offer";

    public ParseResult Parse(string body, TargetOptions t) => ParseDoc(Html.Doc(body), t);

    internal static ParseResult ParseDoc(AngleSharp.Html.Dom.IHtmlDocument doc, TargetOptions t)
    {
        var sku = t.ParamOrNull("Sku");
        var offers = Html.JsonLd(doc)
            .Where(e => Html.IsType(e, "Product"))
            .SelectMany(p => Html.Offers(p).Select(o => (product: p, offer: o)))
            .Where(x => sku is null
                || ((JsonElement?)x.offer).Get("sku").Str() == sku
                || ((JsonElement?)x.product).Get("sku").Str() == sku)
            .Select(x => (JsonElement?)x.offer)
            .ToList();
        if (offers.Count == 0)
            return sku is null ? Microdata(doc, t) : ParseResult.NotFound($"no JSON-LD offer for sku {sku}");

        var offer = offers.FirstOrDefault(o => Html.Condition(o.Get("itemCondition").Str()) == "New") ?? offers[0];
        var availability = offer.Get("availability").Str();
        if (availability is null)
            return ParseResult.NotFound("offer has no availability");
        return new ParseResult(
            true, Html.Buyable(availability), $"jsonld={availability[(availability.LastIndexOf('/') + 1)..]}",
            offer.Path("seller", "name").Str() ?? t.ParamOrNull("DefaultSeller"),
            offer.Get("price").Dec(), Html.Condition(offer.Get("itemCondition").Str()),
            offers.Count > 1 ? $"{offers.Count} offers on page" : null);
    }

    static ParseResult Microdata(AngleSharp.Html.Dom.IHtmlDocument doc, TargetOptions t)
    {
        var offers = doc.QuerySelectorAll("[itemtype*='schema.org/Offer']").ToList();
        static string? Prop(AngleSharp.Dom.IElement scope, string name) =>
            scope.QuerySelector($"[itemprop='{name}']") is { } e
                ? e.GetAttribute("href") ?? e.GetAttribute("content") ?? e.TextContent.Trim()
                : null;

        var offer = offers.FirstOrDefault(o => Prop(o, "availability") is not null);
        if (offer is null)
            return ParseResult.NotFound("no JSON-LD or microdata offer");
        var availability = Prop(offer, "availability")!;
        return new ParseResult(
            true, Html.Buyable(availability), $"microdata={availability[(availability.LastIndexOf('/') + 1)..]}",
            Prop(offer, "seller") ?? t.ParamOrNull("DefaultSeller"),
            Prices.Parse(Prop(offer, "price") ?? Prop(doc.DocumentElement, "price")),
            Html.Condition(Prop(offer, "itemCondition")) ?? "New",
            offers.Count > 1 ? $"{offers.Count} microdata offers" : null);
    }
}

/// <summary>
/// GameStop: one page holds New, Pre-Owned and Refurbished. Uses the New JSON-LD offer
/// (Params:Sku = the New variant SKU) and cross-checks the add-to-cart button for Params:Pid.
/// </summary>
public sealed class GameStopPageParser : IStockParser
{
    public string Name => "gamestop-page";

    public ParseResult Parse(string body, TargetOptions t)
    {
        var doc = Html.Doc(body);
        var ld = JsonLdOfferParser.ParseDoc(doc, t);
        var pid = t.Param("Pid");
        var atc = doc.QuerySelector($"button.add-to-cart[data-pid='{pid}']");
        if (atc is null)
            return ld with { Note = Join(ld.Note, "no add-to-cart button") };

        var atcEnabled = !Html.Disabled(atc);
        var atcText = atc.TextContent.Trim();
        if (!ld.SignalFound)
            return new ParseResult(true, atcEnabled, $"atc='{atcText}'{(atcEnabled ? "" : " [disabled]")}", null, null, null,
                Join(ld.Note, "stock from button only"));

        var agree = ld.InStock == atcEnabled;
        return ld with
        {
            InStock = ld.InStock == true && atcEnabled,
            RawSignal = $"{ld.RawSignal}; atc='{atcText}'{(atcEnabled ? "" : " [disabled]")}",
            Note = Join(ld.Note, agree ? null : "JSON-LD and button disagree"),
        };
    }

    static string? Join(string? a, string? b) => (a, b) switch
    {
        (null, _) => b,
        (_, null) => a,
        _ => $"{a}; {b}",
    };
}

/// <summary>
/// Target (rendered DOM). On 2026-10-15 an out-of-stock page showed no price and no buy buttons at
/// all, so "nothing rendered" is read as out of stock (low confidence until seen in stock).
/// </summary>
public sealed partial class TargetPageParser : IStockParser
{
    static readonly string[] BuyButtons = ["shipItButton", "orderPickupButton", "scheduledDeliveryButton", "addToCartButton"];

    public string Name => "target-page";

    public ParseResult Parse(string body, TargetOptions t)
    {
        var doc = Html.Doc(body);
        var price = Prices.Parse(doc.QuerySelector("[data-test='product-price']")?.TextContent);
        var rel = RelationshipCode().Match(body);
        var seller = SoldBy().Match(doc.Body?.TextContent ?? "") is { Success: true } m
            ? m.Groups[1].Value.Trim()
            : rel.Success ? (rel.Groups[1].Value == "SA" ? "Target" : $"Target Plus ({rel.Groups[1].Value})") : null;

        var buttons = doc.QuerySelectorAll("button[data-test]")
            .Where(b => BuyButtons.Contains(b.GetAttribute("data-test")))
            .ToList();
        if (buttons.Count > 0)
        {
            var raw = string.Join(",", buttons.Select(b => b.GetAttribute("data-test") + (Html.Disabled(b) ? "[disabled]" : "")));
            return new ParseResult(true, buttons.Any(b => !Html.Disabled(b)), raw, seller, price, null);
        }

        if (OutOfStockText().Match(doc.Body?.TextContent ?? "") is { Success: true } oos)
            return new ParseResult(true, false, $"text='{oos.Value}'", seller, price, null);

        return price is null
            ? new ParseResult(true, false, "no buy box (no price, no buttons)", seller, null, null, "low confidence")
            : ParseResult.NotFound("price shown but no known buy buttons") with { Price = price, Seller = seller };
    }

    [GeneratedRegex(@"relationship_type_code\\?""\s*:\s*\\?""(\w+)")]
    private static partial Regex RelationshipCode();

    [GeneratedRegex(@"Sold (?:and shipped )?by ([^\n]{2,40}?)(?:\n|$)")]
    private static partial Regex SoldBy();

    [GeneratedRegex(@"(Temporarily out of stock|Out of stock|Sold out|Notify me when it's back)", RegexOptions.IgnoreCase)]
    private static partial Regex OutOfStockText();
}

/// <summary>
/// Newegg: window.__initialState__.ItemDetail. The buy box is often a marketplace seller on the
/// same URL, so the seller is "Newegg" only for an N82E item number with no third-party seller.
/// </summary>
public sealed partial class NeweggPageParser : IStockParser
{
    public string Name => "newegg-page";

    public ParseResult Parse(string body, TargetOptions t)
    {
        var m = InitialState().Match(body);
        if (!m.Success)
            return ParseResult.NotFound("no window.__initialState__");
        var item = Json.Parse(m.Groups[1].Value).Get("ItemDetail");
        var instock = item.Get("Instock").Bool();
        if (item is null || instock is null)
            return ParseResult.NotFound("no ItemDetail.Instock");

        var itemNumber = item.Get("ItemNumber").Str() ?? item.Path("RankInfo", "ItemNumber").Str() ?? item.Path("Item", "ItemNumber").Str() ?? "";
        var sellerName = item.Path("Seller", "SellerName").Str();
        var seller = itemNumber.StartsWith("N82E", StringComparison.Ordinal)
                     && (sellerName is null || sellerName.Contains("Newegg", StringComparison.OrdinalIgnoreCase))
            ? "Newegg"
            : sellerName ?? $"unknown ({itemNumber})";
        var title = item.Path("Description", "Title").Str() ?? "";
        var condition = itemNumber.EndsWith('R') ? "Open Box"
            : NotNewTitle().Match(title) is { Success: true } c ? c.Value
            : "New";
        return new ParseResult(
            true, instock, $"Instock={instock}; ItemNumber={itemNumber}; SellerType={item.Get("SellerType").Str()}",
            seller, item.Get("FinalPrice").Dec(), condition);
    }

    [GeneratedRegex(@"window\.__initialState__\s*=\s*(\{.*?\})\s*;?\s*</script>", RegexOptions.Singleline)]
    private static partial Regex InitialState();

    [GeneratedRegex(@"refurbished|renewed|open box|used", RegexOptions.IgnoreCase)]
    private static partial Regex NotNewTitle();
}

/// <summary>Amazon product page buy box. Disabled by default: a checkpoint page appeared on first load.</summary>
public sealed partial class AmazonPageParser : IStockParser
{
    public string Name => "amazon-page";

    public ParseResult Parse(string body, TargetOptions t)
    {
        var doc = Html.Doc(body);
        var availability = doc.QuerySelector("#availability")?.TextContent.Trim();
        var atc = doc.QuerySelector("#add-to-cart-button");
        if (availability is null && atc is null)
            return ParseResult.NotFound("no #availability or #add-to-cart-button");

        var price = Prices.Parse(
            (doc.QuerySelector("#corePrice_feature_div .a-offscreen") ?? doc.QuerySelector("#corePriceDisplay_desktop_feature_div .a-offscreen"))?.TextContent);
        var seller = doc.QuerySelector("#merchantInfoFeature_feature_div .offer-display-feature-text-message")?.TextContent.Trim()
            ?? doc.QuerySelector("#sellerProfileTriggerId")?.TextContent.Trim()
            ?? (SoldBy().Match(doc.QuerySelector("#tabular-buybox")?.TextContent ?? "") is { Success: true } m ? m.Groups[1].Value.Trim() : null);
        var inStock = atc is not null && (availability is null || !Unavailable().IsMatch(availability));
        return new ParseResult(
            true, inStock, $"atc={(atc is null ? "absent" : "present")}; availability='{availability}'",
            seller, price, doc.QuerySelector("#usedOnlyBuybox") is null ? "New" : "Used");
    }

    [GeneratedRegex(@"Sold by\s+(\S[^\n]{1,40})")]
    private static partial Regex SoldBy();

    [GeneratedRegex(@"unavailable|out of stock|invitation", RegexOptions.IgnoreCase)]
    private static partial Regex Unavailable();
}
