using MarketData.Domain;
using MarketData.Sources;
using MarketData.Sources.Tests;
using MarketData.Sources.TwelveData;
using MarketData.Storage.Local;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace MarketData.Query.Tests;

/// <summary>
/// Ingest divides by the split factor and query multiplies by it. If those two directions
/// ever drift apart, this is the test that notices.
/// </summary>
public sealed class RoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-rt-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 22, 15, 3, TimeSpan.Zero);

    [Fact]
    public async Task Un_adjust_then_re_adjust_returns_the_vendors_number()
    {
        const string prices =
            """{"meta":{"symbol":"AAPL","currency":"USD"},"values":[{"datetime":"2020-06-15","open":"85.00000","high":"86.00000","low":"84.00000","close":"85.74750","volume":"34702000"}]}""";
        const string splits =
            """{"meta":{"symbol":"AAPL","currency":"USD"},"splits":[{"date":"2020-08-31","ratio":0.25,"from_factor":4,"to_factor":1}]}""";

        var handler = new RoutingStubHandler(new Dictionary<string, string>
        {
            ["time_series"] = prices,
            ["splits"] = splits
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };

        var service = new IngestService(
            new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now)),
            new TwelveDataPriceNormaliser(),
            new TwelveDataActionsNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            new LocalCuratedStore(_root),
            new InMemoryCursorRepository());

        await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1",
            TestContext.Current.CancellationToken);

        var query = new DuckDbMarketDataQuery(CuratedSource.Local(_root));
        var day = new DateOnly(2020, 6, 15);

        var unadjusted = await query.GetPricesAsync(
            "AAPL", day, day, Now,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        var adjusted = await query.GetPricesAsync(
            "AAPL", day, day, Now,
            PriceAdjustment.SplitsOnly, ObservationMode.All, TestContext.Current.CancellationToken);

        // Stored: the price actually quoted that day.
        unadjusted.Single().Close.ShouldBe(342.99m);
        // Adjusted back to today's basis: exactly what the vendor reported.
        adjusted.Single().Close.ShouldBe(85.7475m);
    }

    [Fact]
    public async Task Volume_also_round_trips()
    {
        const string prices =
            """{"meta":{"symbol":"AAPL","currency":"USD"},"values":[{"datetime":"2020-06-15","open":"85.00000","high":"86.00000","low":"84.00000","close":"85.74750","volume":"34702000"}]}""";
        const string splits =
            """{"meta":{"symbol":"AAPL","currency":"USD"},"splits":[{"date":"2020-08-31","ratio":0.25,"from_factor":4,"to_factor":1}]}""";

        var handler = new RoutingStubHandler(new Dictionary<string, string>
        {
            ["time_series"] = prices,
            ["splits"] = splits
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };

        var service = new IngestService(
            new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now)),
            new TwelveDataPriceNormaliser(),
            new TwelveDataActionsNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            new LocalCuratedStore(_root),
            new InMemoryCursorRepository());

        await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1",
            TestContext.Current.CancellationToken);

        var query = new DuckDbMarketDataQuery(CuratedSource.Local(_root));
        var day = new DateOnly(2020, 6, 15);

        var adjusted = await query.GetPricesAsync(
            "AAPL", day, day, Now,
            PriceAdjustment.SplitsOnly, ObservationMode.All, TestContext.Current.CancellationToken);

        adjusted.Single().Volume.ShouldBe(34_702_000L);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
