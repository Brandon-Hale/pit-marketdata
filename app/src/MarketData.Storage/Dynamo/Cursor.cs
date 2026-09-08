namespace MarketData.Storage.Dynamo;

/// <summary>Watermark for one (dataset, symbol) fetch stream.</summary>
/// <param name="RecentCloses">
/// The newest closes by effective date, so a scheduled run can answer "did I already know
/// this?" without a query layer. Trimmed to the ten newest on save: a daily run only ever
/// looks a day or two back, and an unbounded map would grow without limit.
/// </param>
public sealed record Cursor(
    string Dataset,
    string Symbol,
    DateOnly? LastEffectiveDate,
    string? LastContentHash,
    IReadOnlyDictionary<DateOnly, decimal>? RecentCloses = null);
