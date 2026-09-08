namespace MarketData.Storage;

/// <summary>
/// The permanent store of record. Objects are written once and never modified.
/// </summary>
public interface IRawStore
{
    /// <summary>Writes an envelope and returns the key it was written to.</summary>
    Task<string> WriteAsync(RawEnvelope envelope, DateOnly runDate, CancellationToken ct);

    Task<RawEnvelope> ReadAsync(string key, CancellationToken ct);

    IAsyncEnumerable<string> ListAsync(string keyPrefix, CancellationToken ct);
}
