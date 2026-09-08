using MarketData.Domain;

namespace MarketData.Storage;

/// <summary>
/// Derived, append-only Parquet store. Every append writes new part files;
/// nothing is ever updated or deleted.
/// </summary>
public interface ICuratedStore
{
    Task<IReadOnlyList<string>> AppendPricesAsync(
        IReadOnlyList<DailyBar> bars, string ingestId, CancellationToken ct);

    Task<IReadOnlyList<string>> AppendActionsAsync(
        IReadOnlyList<CorporateAction> actions, string ingestId, CancellationToken ct);
}
