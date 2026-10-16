using System.Text.RegularExpressions;

namespace PS5Tracker.Probe;

/// <summary>
/// The goals-doc rule: in stock, sold by the retailer itself, at or under the max price, and New.
/// Phase 1 only logs the decision; nothing is sent.
/// </summary>
public static partial class AlertRule
{
    public static AlertDecision Evaluate(ParseResult p, TargetOptions t, decimal maxPrice)
    {
        if (!p.SignalFound)
            return new(false, "no stock signal");
        if (p.InStock != true)
            return new(false, "not in stock");
        if (p.Seller is null)
            return new(false, "seller unknown");
        if (t.AllowedSellers.Length > 0
            && !t.AllowedSellers.Any(a => string.Equals(a, p.Seller, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"seller '{p.Seller}' is not the retailer");
        if (p.Price is null)
            return new(false, "price unknown");
        if (p.Price > maxPrice)
            return new(false, $"price {p.Price} > {maxPrice}");
        if (p.Condition is { } c && NotNew().IsMatch(c))
            return new(false, $"condition '{c}'");
        return new(true, "ALERT");
    }

    [GeneratedRegex(@"used|refurb|pre-?owned|open ?box|renewed|restored|damaged", RegexOptions.IgnoreCase)]
    private static partial Regex NotNew();
}
