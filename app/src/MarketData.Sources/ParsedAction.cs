using MarketData.Domain;

namespace MarketData.Sources;

/// <summary>A corporate action as the vendor stated it, before any timestamp is decided.</summary>
public sealed record ParsedAction(
    DateOnly ExDate,
    CorporateActionType ActionType,
    decimal? Ratio,
    decimal? Amount,
    string Currency);
