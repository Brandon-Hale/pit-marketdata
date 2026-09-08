using MarketData.Domain;
using MarketData.Storage.Parquet;
using Parquet.Serialization;

namespace MarketData.Storage.Local;

/// <summary>Filesystem-backed <see cref="ICuratedStore"/>.</summary>
public sealed class LocalCuratedStore(string rootDirectory) : ICuratedStore
{
    public Task<IReadOnlyList<string>> AppendPricesAsync(
        IReadOnlyList<DailyBar> bars, string ingestId, CancellationToken ct) =>
        WritePartsAsync("prices_daily", bars, b => b.EffectiveDate.Year, ToRow, ingestId, ct);

    public Task<IReadOnlyList<string>> AppendActionsAsync(
        IReadOnlyList<CorporateAction> actions, string ingestId, CancellationToken ct) =>
        WritePartsAsync("corporate_actions", actions, a => a.ExDate.Year, ToRow, ingestId, ct);

    private async Task<IReadOnlyList<string>> WritePartsAsync<TFact, TRow>(
        string dataset,
        IReadOnlyList<TFact> facts,
        Func<TFact, int> yearOf,
        Func<TFact, TRow> toRow,
        string ingestId,
        CancellationToken ct)
        where TRow : new()
    {
        var keys = new List<string>();

        foreach (var group in facts.GroupBy(yearOf).OrderBy(g => g.Key))
        {
            var key = $"curated/dataset={dataset}/year={group.Key}/part-{ingestId}-{Guid.NewGuid():N}.parquet";
            var path = Path.Combine(rootDirectory, key.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var rows = group.Select(toRow).ToList();
            await using var stream = File.Create(path);
            await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: ct);

            keys.Add(key);
        }

        return keys;
    }

    private static PriceRow ToRow(DailyBar b) => new()
    {
        Symbol = b.Symbol,
        EffectiveDate = b.EffectiveDate,
        Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
        Volume = b.Volume,
        Currency = b.Currency,
        Source = b.Source,
        ObservedAt = b.ObservedAt.UtcDateTime,
        ObservedAtKind = b.ObservedAtKind.ToString().ToUpperInvariant(),
        IngestId = b.IngestId,
        RawKey = b.RawKey,
        SplitsRawKey = b.SplitsRawKey
    };

    private static CorporateActionRow ToRow(CorporateAction a) => new()
    {
        Symbol = a.Symbol,
        ExDate = a.ExDate,
        ActionType = a.ActionType.ToString().ToUpperInvariant(),
        Ratio = a.Ratio,
        Amount = a.Amount,
        Currency = a.Currency,
        Source = a.Source,
        ObservedAt = a.ObservedAt.UtcDateTime,
        ObservedAtKind = a.ObservedAtKind.ToString().ToUpperInvariant(),
        IngestId = a.IngestId,
        RawKey = a.RawKey
    };
}
