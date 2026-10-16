using PS5Tracker.Probe.Parsers;

namespace PS5Tracker.Probe.Tests;

/// <summary>Minimal fixtures shaped like what each site returned on 2026-10-15.</summary>
public class ParserTests
{
    static TargetOptions Target(params (string k, string v)[] ps) =>
        new() { Name = "t", Params = ps.ToDictionary(p => p.k, p => p.v) };

    static ParseResult Run(string parser, string body, TargetOptions? t = null) =>
        StockParsers.SafeParse(StockParsers.All[parser], body, t ?? Target());

    const string BestBuyLd = """
        <script type="application/ld+json">{"@type":"Product","sku":"6601524","offers":[{"@type":"Offer","price":899.99,
        "availability":"https://schema.org/InStock","itemCondition":"https://schema.org/NewCondition","seller":{"name":"Best Buy"}}]}</script>
        """;

    [Fact]
    public void BestBuy_sold_out_button_wins_over_jsonld_and_carousel()
    {
        var html = $"""
            <html><head>{BestBuyLd}</head><body>
            <button data-testid="pdp-sold-out-6601524" disabled>Sold Out</button>
            <button data-testid="carousel-add-to-cart-6503672">Add to cart</button>
            </body></html>
            """;
        var r = Run("bestbuy-page", html, Target(("Sku", "6601524")));
        Assert.True(r.SignalFound);
        Assert.False(r.InStock);
        Assert.Equal("pdp-sold-out-6601524 [disabled]", r.RawSignal);
        Assert.Equal("Best Buy", r.Seller);
        Assert.Equal(899.99m, r.Price);
        Assert.Equal("New", r.Condition);
    }

    [Fact]
    public void BestBuy_add_to_cart_for_the_sku_is_in_stock()
    {
        var html = $"<html><head>{BestBuyLd}</head><body><button data-testid=\"pdp-add-to-cart-6601524\">Add to cart</button></body></html>";
        var r = Run("bestbuy-page", html, Target(("Sku", "6601524")));
        Assert.True(r.InStock);
        Assert.True(AlertRule.Evaluate(r, new() { AllowedSellers = ["Best Buy"] }, 899.99m).Eligible);
    }

    [Fact]
    public void BestBuy_carousel_only_is_not_a_signal()
    {
        var r = Run("bestbuy-page", "<button data-testid=\"carousel-add-to-cart-123\">Add to cart</button>", Target(("Sku", "6601524")));
        Assert.False(r.SignalFound);
    }

    static string NextData(string product) =>
        """<html><body><h1>Uh-oh...</h1><script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"initialData":{"data":{"product":"""
        + product + "}}}}}</script></body></html>";

    [Fact]
    public void Walmart_out_of_stock_page_still_parses()
    {
        var r = Run("walmart-nextdata", NextData("""{"availabilityStatus":"OUT_OF_STOCK","name":"","sellerName":null}"""));
        Assert.True(r.SignalFound);
        Assert.False(r.InStock);
        Assert.Equal("availabilityStatus=OUT_OF_STOCK", r.RawSignal);
    }

    [Fact]
    public void Walmart_marketplace_seller_is_rejected_by_alert_rule()
    {
        var r = Run("walmart-nextdata", NextData("""{"availabilityStatus":"IN_STOCK","sellerName":"HyperTech","sellerType":"EXTERNAL","priceInfo":{"currentPrice":{"price":1644.99}}}"""));
        Assert.True(r.InStock);
        var d = AlertRule.Evaluate(r, new() { AllowedSellers = ["Walmart.com", "INTERNAL"] }, 899.99m);
        Assert.False(d.Eligible);
        Assert.Contains("HyperTech", d.Reason);
    }

    [Fact]
    public void Walmart_redirect_to_marketplace_item_is_not_in_stock()
    {
        var r = Run("walmart-nextdata",
            NextData("""{"usItemId":"18235967161","availabilityStatus":"IN_STOCK","sellerName":"HyperTech","priceInfo":{"currentPrice":{"price":1644.99}}}"""),
            Target(("ItemId", "11839818124")));
        Assert.True(r.SignalFound);
        Assert.False(r.InStock);
        Assert.Equal("redirected to usItemId=18235967161", r.RawSignal);
    }

    [Fact]
    public void SamsClub_internal_seller_in_stock_alerts()
    {
        var r = Run("walmart-nextdata", NextData("""{"availabilityStatus":"IN_STOCK","sellerName":null,"sellerType":"INTERNAL","priceInfo":{"currentPrice":{"price":899}}}"""));
        Assert.Equal("INTERNAL", r.Seller);
        Assert.True(AlertRule.Evaluate(r, new() { AllowedSellers = ["Sam's Club", "INTERNAL"] }, 899.99m).Eligible);
    }

    [Theory]
    [InlineData("outOfStock", false)]
    [InlineData("lowStock", true)]
    [InlineData("inStock", true)]
    public void PsDirect_uses_stockLevelStatus_not_purchasable(string status, bool expected)
    {
        var json = "{\"products\":[{\"code\":\"1000050928\",\"purchasable\":true,\"maxOrderQuantity\":0,\"price\":{\"value\":899},"
            + "\"stock\":{\"stockLevelStatus\":\"" + status + "\"}}]}";
        var r = Run("psdirect-api", json);
        Assert.Equal(expected, r.InStock);
        Assert.Equal(899m, r.Price);
    }

    [Fact]
    public void PsDirect_page_microdata_offer()
    {
        var html = """
            <div itemscope itemtype="http://schema.org/Product"><meta itemprop="price" content="899.00"/>
            <div itemscope itemtype="http://schema.org/Offer"><link itemprop="availability" href="https://schema.org/OutOfStock"/></div></div>
            """;
        var r = Run("schema-offer", html, Target(("DefaultSeller", "PlayStation Direct")));
        Assert.True(r.SignalFound);
        Assert.False(r.InStock);
        Assert.Equal("microdata=OutOfStock", r.RawSignal);
        Assert.Equal("PlayStation Direct", r.Seller);
    }

    [Fact]
    public void GameStop_picks_new_offer_and_checks_button()
    {
        var html = """
            <script type="application/ld+json">[{"@type":"Product","offers":[
              {"@type":"Offer","name":"Pre-Owned","sku":"416189","price":"1329.99","availability":"https://schema.org/InStock","itemCondition":"https://schema.org/UsedCondition","seller":{"name":"GameStop"}},
              {"@type":"Offer","name":"New","sku":"416188","price":"899.99","availability":"https://schema.org/OutOfStock","itemCondition":"https://schema.org/NewCondition","seller":{"name":"GameStop"}}]}]</script>
            <button class="js-add-to-cart add-to-cart btn disabled" data-pid="20015604" disabled>Not Available</button>
            """;
        var r = Run("gamestop-page", html, Target(("Sku", "416188"), ("Pid", "20015604")));
        Assert.False(r.InStock);
        Assert.Equal(899.99m, r.Price);
        Assert.Equal("New", r.Condition);
        Assert.Contains("atc='Not Available' [disabled]", r.RawSignal);
    }

    [Fact]
    public void GameStop_used_offer_in_stock_does_not_alert_when_filtered_to_new_sku()
    {
        var html = """
            <script type="application/ld+json">{"@type":"Product","offers":[
              {"@type":"Offer","sku":"416189","price":"1329.99","availability":"https://schema.org/InStock","itemCondition":"https://schema.org/UsedCondition","seller":{"name":"GameStop"}}]}</script>
            <button class="add-to-cart" data-pid="20015604">Add to Cart</button>
            """;
        var r = Run("gamestop-page", html, Target(("Sku", "416188"), ("Pid", "20015604")));
        Assert.False(AlertRule.Evaluate(r, new() { AllowedSellers = ["GameStop"] }, 899.99m).Eligible);
    }

    [Fact]
    public void Newegg_marketplace_buybox_is_not_newegg()
    {
        var html = """
            <script>window.__initialState__ = {"ItemDetail":{"Instock":true,"FinalPrice":1599,"SellerType":"D",
              "RankInfo":{"ItemNumber":"9SIAN2HKUK0984"},"Seller":{"SellerName":"Ozeal Electronics"},
              "Description":{"Title":"PlayStation 5 Pro Console"}}};</script>
            """;
        var r = Run("newegg-page", html);
        Assert.True(r.InStock);
        Assert.Equal("Ozeal Electronics", r.Seller);
        Assert.False(AlertRule.Evaluate(r, new() { AllowedSellers = ["Newegg"] }, 899.99m).Eligible);
    }

    [Fact]
    public void Newegg_first_party_item()
    {
        var html = """<script>window.__initialState__ = {"ItemDetail":{"Instock":true,"FinalPrice":899.99,"ItemNumber":"N82E16868110346","Seller":null}};</script>""";
        var r = Run("newegg-page", html);
        Assert.Equal("Newegg", r.Seller);
        Assert.Equal("New", r.Condition);
        Assert.True(AlertRule.Evaluate(r, new() { AllowedSellers = ["Newegg"] }, 899.99m).Eligible);
    }

    [Fact]
    public void Target_empty_buybox_reads_as_out_of_stock()
    {
        var r = Run("target-page", """<html><body><h1>PlayStation 5 Pro Console</h1><script>{\"relationship_type_code\":\"SA\"}</script></body></html>""");
        Assert.True(r.SignalFound);
        Assert.False(r.InStock);
        Assert.Equal("Target", r.Seller);
    }

    [Fact]
    public void Target_enabled_ship_button_is_in_stock()
    {
        var r = Run("target-page", """<div data-test="product-price">$899.99</div><button data-test="shipItButton">Ship it</button><p>Sold and shipped by Target</p>""");
        Assert.True(r.InStock);
        Assert.Equal(899.99m, r.Price);
        Assert.Equal("Target", r.Seller);
    }

    [Fact]
    public void Price_above_max_does_not_alert()
    {
        var p = new ParseResult(true, true, "x", "Best Buy", 900.00m, "New");
        Assert.False(AlertRule.Evaluate(p, new() { AllowedSellers = ["Best Buy"] }, 899.99m).Eligible);
    }

    [Fact]
    public void Parser_crash_becomes_not_found()
    {
        var r = Run("bestbuy-page", "<html></html>", Target()); // missing Sku param throws inside
        Assert.False(r.SignalFound);
        Assert.StartsWith("parser error", r.Note);
    }
}
