using MarketData.Domain;
using MarketData.Query;
using MarketData.Storage.Local;
using MarketData.Storage.S3;
using Shouldly;

namespace MarketData.Integration.Tests;

/// <summary>
/// Two read paths that can disagree are worse than one. These assert the local directory
/// and the s3:// prefix return the same rows for the same data.
/// </summary>
[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class S3ReadPathTests(LocalStackFixture fixture)
{
    private static DailyBar Bar() => new(
        "AAPL", new DateOnly(2020, 6, 15), 340m, 345m, 339m, 342.99m, 1_000_000L,
        "USD", "twelvedata",
        new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero),
        ObservationKind.Inferred, "run-1", "raw/x.json", "raw/s.json");

    [Fact]
    public async Task Local_and_s3_return_identical_rows()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var root = Path.Combine(Path.GetTempPath(), "pitmd-eq-" + Guid.NewGuid().ToString("N"));
        var bar = Bar();

        try
        {
            await new LocalCuratedStore(root)
                .AppendPricesAsync([bar], "run-1", TestContext.Current.CancellationToken);
            await new S3CuratedStore(fixture.S3, fixture.Bucket)
                .AppendPricesAsync([bar], "run-1", TestContext.Current.CancellationToken);

            var local = await Read(CuratedSource.Local(root));
            var s3 = await Read(CuratedSource.S3(fixture.Bucket, fixture.Endpoint));

            s3.Count.ShouldBe(local.Count);
            s3.Single().Close.ShouldBe(local.Single().Close);
            s3.Single().EffectiveDate.ShouldBe(local.Single().EffectiveDate);
            s3.Single().ObservedAt.ShouldBe(local.Single().ObservedAt);
            s3.Single().ObservedAtKind.ShouldBe(local.Single().ObservedAtKind);
            s3.Single().SplitsRawKey.ShouldBe(local.Single().SplitsRawKey);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task<IReadOnlyList<DailyBar>> Read(CuratedSource source) =>
        new DuckDbMarketDataQuery(source).GetPricesAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
            DateTimeOffset.UtcNow, PriceAdjustment.None, ObservationMode.All,
            TestContext.Current.CancellationToken);
}
