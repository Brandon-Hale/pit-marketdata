namespace MarketData.Storage.Dynamo;

/// <summary>Watermark for one (dataset, symbol) fetch stream.</summary>
public sealed record Cursor(
    string Dataset,
    string Symbol,
    DateOnly? LastEffectiveDate,
    string? LastContentHash);
