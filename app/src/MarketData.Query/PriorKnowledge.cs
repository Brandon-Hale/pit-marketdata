using MarketData.Domain;

namespace MarketData.Query;

/// <summary>
/// Adapts <see cref="IMarketDataQuery"/> to the prior-knowledge delegate IngestService
/// expects, without either project referencing the other. Sources cannot reference Query:
/// Query already references Domain, and the reverse edge would make the graph cyclic.
/// </summary>
public static class PriorKnowledge
{
    public static Func<string, DateOnly, CancellationToken, Task<decimal?>> From(
        IMarketDataQuery query,
        TimeProvider time) =>
        async (symbol, date, ct) =>
        {
            // PriceAdjustment.None is essential: the incoming close is unadjusted, so an
            // adjusted comparison would report a restatement on every split.
            var bars = await query.GetPricesAsync(
                symbol, date, date, time.GetUtcNow(),
                PriceAdjustment.None, ObservationMode.All, ct);

            return bars.Count == 0 ? null : bars[0].Close;
        };
}
