using MarketData.Domain;
using MarketData.Storage.Local;

namespace MarketData.Query.Tests;

/// <summary>A curated store on disk, built row by row, for one test.</summary>
public sealed class QueryFixture : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "pitmd-q-" + Guid.NewGuid().ToString("N"));

    public CuratedSource Source => CuratedSource.Local(Root);

    public DuckDbMarketDataQuery Query => new(Source);

    public static DailyBar Bar(
        DateOnly effectiveDate,
        decimal close,
        DateTimeOffset observedAt,
        ObservationKind kind = ObservationKind.Inferred,
        string ingestId = "run-1") =>
        new("AAPL", effectiveDate, close, close, close, close, 1_000_000L,
            "USD", "twelvedata", observedAt, kind, ingestId, "raw/x.json", "raw/s.json");

    public static CorporateAction Split(DateOnly exDate, decimal ratio, DateTimeOffset observedAt) =>
        new("AAPL", exDate, CorporateActionType.Split, ratio, null, "USD", "twelvedata",
            observedAt, ObservationKind.Inferred, "run-1", "raw/s.json");

    public async Task WriteBarsAsync(params DailyBar[] bars)
    {
        foreach (var group in bars.GroupBy(b => b.IngestId))
        {
            await new LocalCuratedStore(Root)
                .AppendPricesAsync(group.ToList(), group.Key, CancellationToken.None);
        }
    }

    public async Task WriteActionsAsync(params CorporateAction[] actions)
    {
        if (actions.Length == 0)
        {
            return;
        }

        await new LocalCuratedStore(Root)
            .AppendActionsAsync(actions, actions[0].IngestId, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
