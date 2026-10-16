using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace PS5Tracker.Probe.Parsers;

internal static class Json
{
    public static JsonElement? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? Get(this JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v
            : null;

    public static JsonElement? Path(this JsonElement? e, params string[] names)
    {
        foreach (var n in names)
            e = e.Get(n);
        return e;
    }

    public static string? Str(this JsonElement? e) => e?.ValueKind switch
    {
        JsonValueKind.String => e.Value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.Value.GetRawText(),
        _ => null,
    };

    public static bool? Bool(this JsonElement? e) => e?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(e.Value.GetString(), out var b) => b,
        _ => null,
    };

    public static decimal? Dec(this JsonElement? e) => e?.ValueKind switch
    {
        JsonValueKind.Number => e.Value.GetDecimal(),
        JsonValueKind.String => Prices.Parse(e.Value.GetString()),
        _ => null,
    };

    public static IEnumerable<JsonElement> Items(this JsonElement? e) => e?.ValueKind switch
    {
        JsonValueKind.Array => e.Value.EnumerateArray(),
        JsonValueKind.Object => [e.Value],
        _ => [],
    };
}

internal static partial class Prices
{
    public static decimal? Parse(string? text)
    {
        if (text is null)
            return null;
        var m = PriceRegex().Match(text.Replace(",", ""));
        return m.Success && decimal.TryParse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    [GeneratedRegex(@"\d+(\.\d{1,2})?")]
    private static partial Regex PriceRegex();
}

internal static class Html
{
    static readonly HtmlParser Parser = new();

    public static IHtmlDocument Doc(string html) => Parser.ParseDocument(html);

    /// <summary>Every JSON-LD node on the page, with arrays and @graph flattened.</summary>
    public static IEnumerable<JsonElement> JsonLd(IHtmlDocument doc)
    {
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            if (Json.Parse(script.TextContent) is not { } root)
                continue;
            foreach (var node in Flatten(root))
                yield return node;
        }

        static IEnumerable<JsonElement> Flatten(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray())
                    foreach (var y in Flatten(x))
                        yield return y;
            }
            else if (e.ValueKind == JsonValueKind.Object)
            {
                if (((JsonElement?)e).Get("@graph") is { } graph)
                    foreach (var y in Flatten(graph))
                        yield return y;
                else
                    yield return e;
            }
        }
    }

    public static bool IsType(JsonElement e, string type) =>
        ((JsonElement?)e).Get("@type") is { } t
        && (t.ValueKind == JsonValueKind.String
            ? t.GetString() == type
            : t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.GetString() == type));

    /// <summary>Offers of a JSON-LD Product, unwrapping AggregateOffer.</summary>
    public static IEnumerable<JsonElement> Offers(JsonElement product)
    {
        foreach (var o in ((JsonElement?)product).Get("offers").Items())
        {
            if (IsType(o, "AggregateOffer") && ((JsonElement?)o).Get("offers") is { } inner)
                foreach (var x in ((JsonElement?)inner).Items())
                    yield return x;
            else
                yield return o;
        }
    }

    /// <summary>schema.org availability URL → buyable now? PreOrder counts: it can be bought at retail.</summary>
    public static bool? Buyable(string? availability)
    {
        if (availability is null)
            return null;
        var a = availability[(availability.LastIndexOf('/') + 1)..];
        return a is "InStock" or "LimitedAvailability" or "OnlineOnly" or "PreOrder" or "PreSale";
    }

    /// <summary>schema.org itemCondition URL → short label.</summary>
    public static string? Condition(string? itemCondition) =>
        itemCondition?[(itemCondition.LastIndexOf('/') + 1)..].Replace("Condition", "") switch
        {
            null or "" => null,
            var c => c,
        };

    public static bool Disabled(AngleSharp.Dom.IElement e) =>
        e.HasAttribute("disabled")
        || string.Equals(e.GetAttribute("aria-disabled"), "true", StringComparison.OrdinalIgnoreCase)
        || (e.GetAttribute("class") ?? "").Split(' ').Contains("disabled");
}
