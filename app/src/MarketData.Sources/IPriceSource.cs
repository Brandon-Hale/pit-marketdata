using MarketData.Storage;

namespace MarketData.Sources;

/// <summary>
/// Fetches vendor payloads and wraps them in an immutable envelope. The envelope's
/// <c>ObservedAt</c> is stamped here, exactly once, and copied by everything downstream.
/// </summary>
public interface IPriceSource
{
    string SourceId { get; }

    RateLimit Limits { get; }

    Task<RawEnvelope> FetchDailyBarsAsync(string symbol, DateOnly from, DateOnly to, CancellationToken ct);

    Task<RawEnvelope> FetchSplitsAsync(string symbol, CancellationToken ct);

    Task<RawEnvelope> FetchDividendsAsync(string symbol, CancellationToken ct);
}
