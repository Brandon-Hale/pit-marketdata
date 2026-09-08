namespace MarketData.Sources;

/// <summary>A bar exactly as the vendor stated it, before any timestamp is decided.</summary>
public sealed record ParsedBar(
    DateOnly EffectiveDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    string Currency);
