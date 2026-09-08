namespace MarketData.Domain;

/// <summary>What timestamp a newly seen value should carry, and why.</summary>
public sealed record ObservationDecision(DateTimeOffset ObservedAt, ObservationKind Kind);

/// <summary>
/// Decides the <c>observed_at</c> of a value. Backfilled history has no true
/// observation instant, so its publication time is reconstructed; anything learned
/// close to publication, and every restatement, carries the real fetch instant.
/// </summary>
public sealed class ObservationPolicy(IPublicationClock clock, TimeSpan liveWindow)
{
    /// <summary>
    /// The clock behind inferred timestamps, reused to stamp corporate actions. Exposed
    /// so IngestService need not be handed the same clock a second time.
    /// </summary>
    public IPublicationClock Clock => clock;

    /// <summary>
    /// Returns null when nothing was learned — the value is already known and unchanged.
    /// </summary>
    public ObservationDecision? Decide(
        DateOnly effectiveDate,
        decimal close,
        decimal? knownClose,
        DateTimeOffset fetchedAt)
    {
        var fetched = fetchedAt.ToUniversalTime();

        if (knownClose is { } known)
        {
            // A restatement is genuine news, learned now. Never inferred: inferring
            // here would claim a corrected value was knowable at the original date.
            return known == close ? null : new ObservationDecision(fetched, ObservationKind.Observed);
        }

        var published = clock.InferredPublication(effectiveDate);

        return fetched - published <= liveWindow
            ? new ObservationDecision(fetched, ObservationKind.Observed)
            : new ObservationDecision(published, ObservationKind.Inferred);
    }
}
