namespace MarketData.Storage.Dynamo;

/// <summary>
/// What one scheduled run did. The only visibility into a job nobody watches, and the
/// reason the table carries a TTL attribute.
/// </summary>
public sealed record RunRecord(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int SymbolsAttempted,
    int SymbolsSucceeded,
    int RowsWritten,
    int ActionsWritten,
    int SkippedUnchanged,
    IReadOnlyList<string> Errors)
{
    /// <summary>Unix seconds at which DynamoDB may expire this record.</summary>
    public long ExpiresAt => StartedAt.AddDays(90).ToUnixTimeSeconds();
}
