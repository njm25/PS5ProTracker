namespace PS5Tracker.Probe.Parsers;

public interface IStockParser
{
    string Name { get; }
    ParseResult Parse(string body, TargetOptions target);
}

public static class StockParsers
{
    public static readonly IReadOnlyDictionary<string, IStockParser> All = new IStockParser[]
    {
        new BestBuyPageParser(),
        new BestBuyApiParser(),
        new WalmartNextDataParser(),
        new WalmartAffiliateApiParser(),
        new PsDirectApiParser(),
        new JsonLdOfferParser(),
        new GameStopPageParser(),
        new TargetPageParser(),
        new NeweggPageParser(),
        new AmazonPageParser(),
    }.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs a parser, turning a crash on unexpected markup into a "signal not found" row.</summary>
    public static ParseResult SafeParse(IStockParser parser, string body, TargetOptions target)
    {
        if (string.IsNullOrEmpty(body))
            return ParseResult.NotFound("empty body");
        try
        {
            return parser.Parse(body, target);
        }
        catch (Exception ex)
        {
            return ParseResult.NotFound($"parser error: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
