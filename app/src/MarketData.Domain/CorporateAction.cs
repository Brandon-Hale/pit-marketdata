namespace MarketData.Domain;

/// <summary>Kind of corporate action. A reverse split is a <see cref="Split"/> with ratio &gt; 1.</summary>
public enum CorporateActionType
{
    Split,
    Dividend
}

/// <summary>
/// One corporate action. <see cref="Ratio"/> is populated for splits and
/// <see cref="Amount"/> for dividends; the other is null.
/// </summary>
public sealed record CorporateAction(
    string Symbol,
    DateOnly ExDate,
    CorporateActionType ActionType,
    decimal? Ratio,
    decimal? Amount,
    string Currency,
    string Source,
    DateTimeOffset ObservedAt,
    ObservationKind ObservedAtKind,
    string IngestId,
    string RawKey)
{
    private readonly DateTimeOffset _observedAt = DailyBar.RequireUtc(ObservedAt);

    /// <summary>The UTC instant this fact was learned. Always zero-offset.</summary>
    public DateTimeOffset ObservedAt
    {
        get => _observedAt;
        init => _observedAt = DailyBar.RequireUtc(value);
    }
}
