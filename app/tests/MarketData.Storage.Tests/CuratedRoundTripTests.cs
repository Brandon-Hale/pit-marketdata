using MarketData.Domain;
using MarketData.Storage.Local;
using Shouldly;

namespace MarketData.Storage.Tests;

public sealed class CuratedRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-" + Guid.NewGuid().ToString("N"));

    private static DailyBar Bar(decimal close, DateTimeOffset observedAt, ObservationKind kind) => new(
        "AAPL", new DateOnly(2020, 8, 31),
        127.58m, 131.00m, 126.00m, close, 225_702_700L,
        "USD", "twelvedata", observedAt, kind, "run-1", "raw/x.json");

    [Fact]
    public async Task Values_survive_the_write_exactly()
    {
        var store = new LocalCuratedStore(_root);
        var observedAt = new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero);
        await store.AppendPricesAsync(
            [Bar(328.31000m, observedAt, ObservationKind.Inferred)], "run-1", TestContext.Current.CancellationToken);

        using var duck = new DuckDbReader();
        var rows = duck.Query(
            $"SELECT Symbol, CAST(EffectiveDate AS DATE) AS d, Close, Volume, ObservedAtKind " +
            $"FROM read_parquet('{DuckDbReader.Glob(_root, "prices_daily")}')");

        rows.Count.ShouldBe(1);
        rows[0]["Symbol"]!.ToString().ShouldBe("AAPL");
        // DuckDB returns a DATE column as DateOnly, not DateTime. The explicit
        // CAST keeps the Parquet-side mapping from being load-bearing either way.
        rows[0]["d"].ShouldBe(new DateOnly(2020, 8, 31));
        Convert.ToDecimal(rows[0]["Close"]).ShouldBe(328.31000m);
        Convert.ToInt64(rows[0]["Volume"]).ShouldBe(225_702_700L);
        rows[0]["ObservedAtKind"]!.ToString().ShouldBe("INFERRED");
    }

    [Fact]
    public async Task Observed_at_is_stored_as_utc()
    {
        var store = new LocalCuratedStore(_root);
        var observedAt = new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero);
        await store.AppendPricesAsync(
            [Bar(129.04m, observedAt, ObservationKind.Observed)], "run-1", TestContext.Current.CancellationToken);

        using var duck = new DuckDbReader();
        var rows = duck.Query(
            $"SELECT ObservedAt FROM read_parquet('{DuckDbReader.Glob(_root, "prices_daily")}')");

        Convert.ToDateTime(rows[0]["ObservedAt"]).ShouldBe(new DateTime(2020, 8, 31, 20, 15, 0));
    }

    [Fact]
    public async Task Multiple_parts_are_read_as_one_dataset()
    {
        var store = new LocalCuratedStore(_root);
        var at = new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero);
        await store.AppendPricesAsync([Bar(129.04m, at, ObservationKind.Inferred)], "run-1", TestContext.Current.CancellationToken);
        await store.AppendPricesAsync([Bar(129.99m, at.AddDays(1), ObservationKind.Observed)], "run-2", TestContext.Current.CancellationToken);

        using var duck = new DuckDbReader();
        var rows = duck.Query(
            $"SELECT COUNT(*) AS n FROM read_parquet('{DuckDbReader.Glob(_root, "prices_daily")}')");

        Convert.ToInt64(rows[0]["n"]).ShouldBe(2L);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
