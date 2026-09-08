using MarketData.Storage.Dynamo;

namespace MarketData.Lambda;

/// <summary>What one symbol's ingest did, flattened so the runner needs no Sources reference.</summary>
public sealed record IngestOutcome(int RowsWritten, int ActionsWritten, bool Skipped);

/// <summary>
/// Walks the watchlist, ingests each symbol, and records the result. Symbols run
/// sequentially: at twenty symbols against an eight-per-minute vendor limit, concurrency
/// buys nothing and risks a 429 that would look like missing data.
/// </summary>
public sealed class IngestRunner(
    IWatchlistRepository watchlist,
    Func<string, CancellationToken, Task<IngestOutcome>> ingest,
    IRunRecordRepository runRecords,
    TimeProvider timeProvider)
{
    public async Task<RunRecord> RunAsync(string runId, CancellationToken ct)
    {
        var startedAt = timeProvider.GetUtcNow();
        var symbols = await watchlist.ActiveAsync(startedAt, ct);

        var succeeded = 0;
        var rows = 0;
        var actions = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var symbol in symbols)
        {
            try
            {
                var outcome = await ingest(symbol, ct);

                succeeded++;
                rows += outcome.RowsWritten;
                actions += outcome.ActionsWritten;

                if (outcome.Skipped)
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad symbol must not take down the schedule. The error is recorded
                // against the run and the remaining symbols still ingest. Cancellation is
                // deliberately not caught: it means shutdown, and continuing would waste
                // the remaining symbols' vendor credits.
                errors.Add($"{symbol}: {ex.Message}");
            }
        }

        var record = new RunRecord(
            runId, startedAt, timeProvider.GetUtcNow(),
            symbols.Count, succeeded, rows, actions, skipped, errors);

        await runRecords.SaveAsync(record, ct);

        return record;
    }
}
