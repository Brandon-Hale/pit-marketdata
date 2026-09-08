namespace MarketData.Sources;

/// <summary>Outcome of ingesting one symbol, recorded against the run.</summary>
public sealed record IngestResult(
    string Symbol,
    bool SkippedUnchanged,
    int RowsWritten,
    string? RawKey,
    IReadOnlyList<string> CuratedKeys,
    int ActionsWritten = 0);
