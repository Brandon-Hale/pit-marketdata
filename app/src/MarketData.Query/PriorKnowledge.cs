using MarketData.Domain;

namespace MarketData.Query;

/// <summary>
/// Adapts <see cref="IMarketDataQuery"/> to the prior-knowledge delegate IngestService
/// expects, without either project referencing the other. Sources cannot reference Query:
/// Query already references Domain, and the reverse edge would make the graph cyclic.
/// </summary>
public static class PriorKnowledge
{
    /// <summary>
    /// Bars are loaded once per symbol and indexed by date. A per-date query would open a
    /// DuckDB connection, install httpfs and hit S3 for every bar in the range — a backfill
    /// asks about thousands of dates, which made an eleven-year backfill run for over ten
    /// minutes without producing a single curated row.
    /// </summary>
    public static Func<string, DateOnly, CancellationToken, Task<decimal?>> From(
        IMarketDataQuery query,
        TimeProvider time)
    {
        var cache = new Dictionary<string, IReadOnlyDictionary<DateOnly, decimal>>(
            StringComparer.OrdinalIgnoreCase);

        return async (symbol, date, ct) =>
        {
            if (!cache.TryGetValue(symbol, out var closes))
            {
                // PriceAdjustment.None is essential: prior knowledge is compared against an
                // incoming unadjusted close, so an adjusted read would report a restatement
                // on every split.
                var bars = await query.GetPricesAsync(
                    symbol, EarliestDate, LatestDate, time.GetUtcNow(),
                    PriceAdjustment.None, ObservationMode.All, ct);

                closes = bars.ToDictionary(b => b.EffectiveDate, b => b.Close);
                cache[symbol] = closes;
            }

            return closes.TryGetValue(date, out var close) ? close : null;
        };
    }

    // Wider than any equity history and narrower than DateOnly's extremes, which some
    // Parquet and DuckDB date paths handle poorly.
    private static readonly DateOnly EarliestDate = new(1900, 1, 1);

    private static readonly DateOnly LatestDate = new(2999, 12, 31);
}
