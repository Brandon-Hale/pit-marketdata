namespace MarketData.Domain;

/// <summary>
/// Whether an <c>observed_at</c> instant was recorded live or reconstructed.
/// </summary>
public enum ObservationKind
{
    /// <summary>Captured live: the instant is the real fetch time.</summary>
    Observed,

    /// <summary>Backfilled: the instant is a reconstructed publication time.</summary>
    Inferred
}
