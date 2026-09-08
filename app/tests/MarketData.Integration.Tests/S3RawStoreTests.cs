using MarketData.Storage;
using MarketData.Storage.S3;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class S3RawStoreTests(LocalStackFixture fixture)
{
    private static RawEnvelope Envelope(string symbol = "AAPL")
    {
        const string payload = "{\"values\":[{\"close\":\"328.31000\"}]}";

        return new RawEnvelope(
            "twelvedata", symbol, "prices_daily",
            new DateTimeOffset(2026, 9, 8, 22, 15, 3, TimeSpan.Zero),
            "https://api.twelvedata.com/time_series?symbol=AAPL&apikey=REDACTED",
            ContentHash.Sha256(payload), payload);
    }

    [Fact]
    public async Task Round_trips_through_s3_with_the_same_key_layout_as_local()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var store = new S3RawStore(fixture.S3, fixture.Bucket);
        var original = Envelope();

        var key = await store.WriteAsync(original, new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);
        var read = await store.ReadAsync(key, TestContext.Current.CancellationToken);

        key.ShouldBe("raw/source=twelvedata/dataset=prices_daily/dt=2026-09-08/AAPL.json");
        read.ShouldBe(original);
    }

    [Fact]
    public async Task Lists_keys_under_a_prefix()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var store = new S3RawStore(fixture.S3, fixture.Bucket);
        await store.WriteAsync(Envelope("MSFT"), new DateOnly(2026, 9, 9), TestContext.Current.CancellationToken);

        var keys = new List<string>();
        await foreach (var k in store.ListAsync("raw/source=twelvedata", TestContext.Current.CancellationToken))
        {
            keys.Add(k);
        }

        keys.ShouldContain(k => k.EndsWith("MSFT.json"));
    }
}
