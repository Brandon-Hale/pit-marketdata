namespace MarketData.Domain;

/// <summary>
/// One unadjusted daily bar, exactly as a vendor reported it, carrying both the
/// date it is about (<see cref="EffectiveDate"/>) and the instant it was learned
/// (<see cref="ObservedAt"/>).
/// </summary>
public sealed record DailyBar(
    string Symbol,
    DateOnly EffectiveDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    string Currency,
    string Source,
    DateTimeOffset ObservedAt,
    ObservationKind ObservedAtKind,
    string IngestId,
    string RawKey)
{
    private readonly DateTimeOffset _observedAt = RequireUtc(ObservedAt);

    /// <summary>The UTC instant this fact was learned. Always zero-offset.</summary>
    public DateTimeOffset ObservedAt
    {
        get => _observedAt;
        init => _observedAt = RequireUtc(value);
    }

    internal static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException(
                $"ObservedAt must be UTC (zero offset) but had offset {value.Offset}. " +
                "A non-UTC instant makes as-of comparisons silently wrong.",
                nameof(value));
}
