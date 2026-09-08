namespace MarketData.Domain;

/// <summary>
/// US equity publication clock: the regular session closes at 16:00
/// America/New_York, and a daily bar is public shortly after.
/// </summary>
public sealed class UsEquityPublicationClock : IPublicationClock
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>16:00 close plus a 15 minute settle-and-publish lag.</summary>
    private static readonly TimeSpan CloseWithLag = new(16, 15, 0);

    public DateTimeOffset InferredPublication(DateOnly effectiveDate)
    {
        var local = effectiveDate.ToDateTime(TimeOnly.MinValue).Add(CloseWithLag);

        // 16:15 never falls inside a US DST transition window (transitions occur
        // at 02:00 local), so the offset is unambiguous and no adjustment is needed.
        var offset = Eastern.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
