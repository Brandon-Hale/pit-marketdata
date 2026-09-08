using Amazon.S3;
using MarketData.Domain;
using MarketData.Query;
using MarketData.Storage.Local;
using MarketData.Storage.S3;
using Shouldly;

namespace MarketData.Integration.Tests;

/// <summary>
/// Two read paths that can disagree are worse than one, so this asserts a local directory
/// and an <c>s3://</c> prefix return the same rows for the same data.
/// </summary>
/// <remarks>
/// This runs against <b>real S3</b>, not LocalStack, and is opt-in via
/// <c>PITMD_S3_TEST_BUCKET</c>. DuckDB's httpfs ignores the <c>ENDPOINT</c> override on a
/// secret and reaches real AWS regardless, so a LocalStack run fails with AWS's own
/// <c>InvalidAccessKeyId: "test"</c> rather than ever contacting the container. Pointing it
/// at real S3 is what the Stage 4 design's risks section prescribes for exactly this case,
/// and it is stronger evidence anyway: it exercises the production credential chain.
///
/// Verified manually on 2026-09-08 against a throwaway bucket in ap-southeast-2: a bar
/// written through <see cref="S3CuratedStore"/> read back through
/// <see cref="DuckDbMarketDataQuery"/> with matching values, confirming the <c>**</c> glob
/// expands correctly over S3.
///
/// To run: create a scratch bucket, set PITMD_S3_TEST_BUCKET to its name, and delete it
/// afterwards. Never point this at the production data bucket — it writes a fake AAPL row.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class S3ReadPathTests
{
    private static DailyBar Bar() => new(
        "AAPL", new DateOnly(2020, 6, 15), 340m, 345m, 339m, 342.99m, 1_000_000L,
        "USD", "twelvedata",
        new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero),
        ObservationKind.Inferred, "run-1", "raw/x.json", "raw/s.json");

    [Fact]
    public async Task Local_and_s3_return_identical_rows()
    {
        var bucket = Environment.GetEnvironmentVariable("PITMD_S3_TEST_BUCKET");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(bucket),
            "PITMD_S3_TEST_BUCKET is not set. See the remarks on this class: DuckDB's httpfs " +
            "cannot be pointed at LocalStack, so read-path equivalence is checked against a " +
            "scratch bucket in real S3 instead.");

        var root = Path.Combine(Path.GetTempPath(), "pitmd-eq-" + Guid.NewGuid().ToString("N"));
        var bar = Bar();

        try
        {
            using var s3Client = new AmazonS3Client(Amazon.RegionEndpoint.APSoutheast2);

            await new LocalCuratedStore(root)
                .AppendPricesAsync([bar], "run-1", TestContext.Current.CancellationToken);
            await new S3CuratedStore(s3Client, bucket!)
                .AppendPricesAsync([bar], "run-1", TestContext.Current.CancellationToken);

            var local = await Read(CuratedSource.Local(root));
            var s3 = await Read(CuratedSource.S3(bucket!));

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
