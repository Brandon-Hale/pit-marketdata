namespace MarketData.Domain;

/// <summary>
/// Whether reconstructed observations are included. Applies to bars only — never to
/// corporate actions, whose ex-dates are a matter of public record.
/// </summary>
public enum ObservationMode
{
    All,

    /// <summary>Only bars whose observed_at was captured live.</summary>
    ObservedOnly
}
