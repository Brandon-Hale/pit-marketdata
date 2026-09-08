using Shouldly;

namespace MarketData.Query.Tests;

public sealed class CuratedSourceTests
{
    [Fact]
    public void Local_builds_a_recursive_glob_with_forward_slashes()
    {
        var source = CuratedSource.Local("/tmp/store");

        source.Glob("prices_daily").ShouldBe("/tmp/store/curated/dataset=prices_daily/**/*.parquet");
    }

    [Fact]
    public void S3_builds_an_s3_glob()
    {
        var source = CuratedSource.S3("pit-marketdata-data-bzun6w");

        source.Glob("corporate_actions")
            .ShouldBe("s3://pit-marketdata-data-bzun6w/curated/dataset=corporate_actions/**/*.parquet");
    }

    [Fact]
    public void A_windows_local_root_still_produces_forward_slashes()
    {
        var source = CuratedSource.Local(@"C:\store");

        source.Glob("prices_daily").ShouldNotContain(@"\");
    }
}
