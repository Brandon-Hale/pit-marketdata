using MarketData.Storage.Dynamo;

namespace MarketData.Sources;

/// <summary>
/// Prior knowledge from the cursor's recent-closes map, for callers that cannot carry a
/// query layer. Same delegate shape as <c>PriorKnowledge.From</c> in MarketData.Query, so
/// <see cref="IngestService"/> cannot tell which it was given.
/// </summary>
/// <remarks>
/// Bounded by what the cursor holds — about ten trading days. A restatement of an older bar
/// is not detected here and is left to Stage 6's reprocess, which runs with the full query
/// layer. Use <c>PriorKnowledge.From</c> wherever DuckDB is available.
/// </remarks>
public static class CursorPriorKnowledge
{
    public static Func<string, DateOnly, CancellationToken, Task<decimal?>> From(
        ICursorRepository cursors,
        string dataset)
    {
        // One read per symbol, held for the life of the delegate: a backfill asks about
        // thousands of dates and the answer for a symbol does not change mid-run.
        var cache = new Dictionary<string, IReadOnlyDictionary<DateOnly, decimal>?>();

        return async (symbol, date, ct) =>
        {
            if (!cache.TryGetValue(symbol, out var closes))
            {
                var cursor = await cursors.GetAsync(dataset, symbol, ct);
                closes = cursor?.RecentCloses;
                cache[symbol] = closes;
            }

            return closes is not null && closes.TryGetValue(date, out var close)
                ? close
                : null;
        };
    }
}
