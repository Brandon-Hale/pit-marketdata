using MarketData.Storage;
using MarketData.Storage.Local;
using Shouldly;

namespace MarketData.Storage.Tests;

public sealed class LocalRawStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-" + Guid.NewGuid().ToString("N"));

    private static RawEnvelope Envelope(string payload = "{\"ok\":true}") => new(
        SourceId: "twelvedata",
        Symbol: "AAPL",
        Dataset: "prices_daily",
        ObservedAt: new DateTimeOffset(2026, 9, 8, 22, 15, 3, TimeSpan.Zero),
        RequestUrl: "https://api.twelvedata.com/time_series?symbol=AAPL&apikey=REDACTED",
        ContentHash: ContentHash.Sha256(payload),
        Payload: payload);

    [Fact]
    public async Task Builds_the_documented_key_layout()
    {
        var store = new LocalRawStore(_root);

        var key = await store.WriteAsync(Envelope(), new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);

        key.ShouldBe("raw/source=twelvedata/dataset=prices_daily/dt=2026-09-08/AAPL.json");
    }

    [Fact]
    public async Task Round_trips_an_envelope_including_the_exact_payload()
    {
        var store = new LocalRawStore(_root);
        var original = Envelope("{\"values\":[{\"close\":\"328.31000\"}]}");

        var key = await store.WriteAsync(original, new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);
        var read = await store.ReadAsync(key, TestContext.Current.CancellationToken);

        read.ShouldBe(original);
        read.Payload.ShouldBe(original.Payload);
        read.ObservedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Lists_keys_under_a_prefix()
    {
        var store = new LocalRawStore(_root);
        await store.WriteAsync(Envelope(), new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);
        await store.WriteAsync(Envelope() with { Symbol = "MSFT" }, new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);

        var keys = new List<string>();
        await foreach (var k in store.ListAsync("raw/source=twelvedata", TestContext.Current.CancellationToken))
        {
            keys.Add(k);
        }

        keys.Count.ShouldBe(2);
        keys.ShouldAllBe(k => k.StartsWith("raw/source=twelvedata"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
