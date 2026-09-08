namespace MarketData.Domain;

/// <summary>
/// Reference data for one tradeable instrument. <see cref="Cik"/> is the SEC
/// identifier used to join fundamentals, and is null for instruments that do not
/// file (ETFs, indices).
/// </summary>
public sealed record Instrument(
    string Symbol,
    string Exchange,
    string MicCode,
    string Name,
    string Currency,
    string? Cik,
    string? Sector,
    DateOnly? ListingDate,
    DateTimeOffset FirstSeenAt,
    bool IsActive);
