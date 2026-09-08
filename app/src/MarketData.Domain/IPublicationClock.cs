namespace MarketData.Domain;

/// <summary>
/// Reconstructs when a daily bar for a given trading date became public.
/// Used only for backfilled history, which has no true observation instant.
/// </summary>
public interface IPublicationClock
{
    /// <summary>The UTC instant a bar for <paramref name="effectiveDate"/> became knowable.</summary>
    DateTimeOffset InferredPublication(DateOnly effectiveDate);
}
