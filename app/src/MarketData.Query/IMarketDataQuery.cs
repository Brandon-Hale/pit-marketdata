using MarketData.Domain;

namespace MarketData.Query;

/// <summary>
/// The as-of read path. Every method takes an <c>asOf</c> and returns only what was
/// knowable at that instant. There is deliberately no overload without one.
/// </summary>
public interface IMarketDataQuery
{
    Task<IReadOnlyList<DailyBar>> GetPricesAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTimeOffset asOf,
        PriceAdjustment adjustment,
        ObservationMode observations,
        CancellationToken ct);

    /// <summary>
    /// Actions visible at <paramref name="asOf"/>. Takes no <see cref="ObservationMode"/>:
    /// the mode applies to bars only, because every backfilled action is INFERRED and
    /// filtering them would make adjustment silently return unadjusted prices.
    /// </summary>
    Task<IReadOnlyList<CorporateAction>> GetCorporateActionsAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTimeOffset asOf,
        CancellationToken ct);
}
