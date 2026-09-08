namespace MarketData.Storage;

/// <summary>
/// Immutable wrapper written to <c>raw/</c>. The fetcher stamps
/// <see cref="ObservedAt"/> here exactly once; normalisers copy it rather than
/// reading a clock, which is what makes normalisation a pure function of raw.
/// </summary>
/// <param name="Payload">
/// The vendor response as verbatim text. Kept as a string, not a parsed object,
/// so the bytes that produced <see cref="ContentHash"/> are exactly what is stored.
/// </param>
public sealed record RawEnvelope(
    string SourceId,
    string Symbol,
    string Dataset,
    DateTimeOffset ObservedAt,
    string RequestUrl,
    string ContentHash,
    string Payload);
