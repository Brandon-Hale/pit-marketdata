using MarketData.Domain;
using MarketData.Storage.Local;
using Shouldly;

namespace MarketData.Storage.Tests;

public sealed class LocalCuratedStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-" + Guid.NewGuid().ToString("N"));

    private static DailyBar Bar(int year, decimal close) => new(
        "AAPL", new DateOnly(year, 8, 31),
        127.58m, 131.00m, 126.00m, close, 225_702_700L,
        "USD", "twelvedata",
        new DateTimeOffset(year, 8, 31, 20, 15, 0, TimeSpan.Zero),
        ObservationKind.Inferred, "run-1", "raw/x.json");

    [Fact]
    public async Task Writes_one_part_per_year()
    {
        var store = new LocalCuratedStore(_root);

        var keys = await store.AppendPricesAsync(
            [Bar(2020, 129.04m), Bar(2021, 152.51m)], "run-1", TestContext.Current.CancellationToken);

        keys.Count.ShouldBe(2);
        keys.ShouldContain(k => k.Contains("year=2020"));
        keys.ShouldContain(k => k.Contains("year=2021"));
        keys.ShouldAllBe(k => k.StartsWith("curated/dataset=prices_daily/"));
    }

    [Fact]
    public async Task Creates_a_readable_parquet_file_on_disk()
    {
        var store = new LocalCuratedStore(_root);

        var keys = await store.AppendPricesAsync([Bar(2020, 129.04m)], "run-1", TestContext.Current.CancellationToken);

        var path = Path.Combine(_root, keys[0].Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue();
        new FileInfo(path).Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Two_appends_produce_two_distinct_parts()
    {
        var store = new LocalCuratedStore(_root);

        var first = await store.AppendPricesAsync([Bar(2020, 129.04m)], "run-1", TestContext.Current.CancellationToken);
        var second = await store.AppendPricesAsync([Bar(2020, 129.99m)], "run-2", TestContext.Current.CancellationToken);

        second[0].ShouldNotBe(first[0]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
