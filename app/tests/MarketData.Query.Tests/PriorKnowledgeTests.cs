using MarketData.Domain;
using MarketData.Sources;
using MarketData.Sources.Tests;
using MarketData.Sources.TwelveData;
using MarketData.Storage.Local;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace MarketData.Query.Tests;

/// <summary>
/// The cursor watermark only caught a restatement whose close differed on a bar at or
/// before it. These assert the real per-date lookup replaces that guess.
/// </summary>
public sealed class PriorKnowledgeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-pk-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 22, 15, 3, TimeSpan.Zero);

    private const string Prices =
        """{"meta":{"symbol":"AAPL","currency":"USD"},"values":[{"datetime":"2020-06-15","open":"342.00","high":"345.00","low":"341.00","close":"342.99","volume":"34702000"}]}""";

    private IngestService Build(
        CapturingCuratedStore curated,
        Func<string, DateOnly, CancellationToken, Task<decimal?>>? lookup)
    {
        var handler = new RoutingStubHandler(new Dictionary<string, string>
        {
            ["time_series"] = Prices,
            ["splits"] = """{"splits":[]}"""
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };

        return new IngestService(
            new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now)),
            new TwelveDataPriceNormaliser(),
            new TwelveDataActionsNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            curated,
            new InMemoryCursorRepository(),
            lookup);
    }

    [Fact]
    public async Task An_unchanged_close_reported_by_the_lookup_writes_nothing()
    {
        var curated = new CapturingCuratedStore();

        // The lookup says we already know this exact close, on a date the cursor
        // watermark would not have covered at all.
        await Build(curated, (_, _, _) => Task.FromResult<decimal?>(342.99m))
            .IngestSymbolAsync("AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
                "run-1", TestContext.Current.CancellationToken);

        curated.Prices.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_changed_close_reported_by_the_lookup_is_a_restatement()
    {
        var curated = new CapturingCuratedStore();

        await Build(curated, (_, _, _) => Task.FromResult<decimal?>(300.00m))
            .IngestSymbolAsync("AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
                "run-1", TestContext.Current.CancellationToken);

        var bar = curated.Prices.ShouldHaveSingleItem();
        bar.Close.ShouldBe(342.99m);
        // A restatement takes the real fetch time, never an inferred one.
        bar.ObservedAtKind.ShouldBe(ObservationKind.Observed);
        bar.ObservedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task A_null_from_the_lookup_means_unseen_and_is_backfilled()
    {
        var curated = new CapturingCuratedStore();

        await Build(curated, (_, _, _) => Task.FromResult<decimal?>(null))
            .IngestSymbolAsync("AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
                "run-1", TestContext.Current.CancellationToken);

        var bar = curated.Prices.ShouldHaveSingleItem();
        bar.ObservedAtKind.ShouldBe(ObservationKind.Inferred);
        bar.ObservedAt.ShouldBe(new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task PriorKnowledge_From_reads_the_unadjusted_close()
    {
        // A split exists, so an adjusted read would return 85.7475 and every subsequent
        // ingest would see a spurious restatement.
        var fixture = new QueryFixture();

        try
        {
            await fixture.WriteBarsAsync(QueryFixture.Bar(
                new DateOnly(2020, 6, 15), 342.99m,
                new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero)));
            await fixture.WriteActionsAsync(QueryFixture.Split(
                new DateOnly(2020, 8, 31), 0.25m,
                new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero)));

            var lookup = PriorKnowledge.From(fixture.Query, new FakeTimeProvider(Now));

            var known = await lookup("AAPL", new DateOnly(2020, 6, 15), TestContext.Current.CancellationToken);

            known.ShouldBe(342.99m);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task PriorKnowledge_From_returns_null_for_an_unseen_date()
    {
        var fixture = new QueryFixture();

        try
        {
            var lookup = PriorKnowledge.From(fixture.Query, new FakeTimeProvider(Now));

            var known = await lookup("AAPL", new DateOnly(2020, 6, 15), TestContext.Current.CancellationToken);

            known.ShouldBeNull();
        }
        finally
        {
            fixture.Dispose();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
