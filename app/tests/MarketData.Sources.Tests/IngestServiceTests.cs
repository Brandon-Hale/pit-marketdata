using MarketData.Domain;
using MarketData.Sources.TwelveData;
using MarketData.Storage;
using MarketData.Storage.Local;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class IngestServiceTests : IDisposable
{
    private const string Body =
        """{"meta":{"symbol":"AAPL","currency":"USD"},"values":[{"datetime":"2020-08-31","open":"127.58","high":"131.00","low":"126.00","close":"129.04","volume":"225702700"}]}""";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 22, 15, 3, TimeSpan.Zero);

    private IngestService Build(
        string body,
        out InMemoryCursorRepository cursors,
        ICuratedStore? curated = null)
    {
        var handler = new StubHandler(body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var source = new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now));

        cursors = new InMemoryCursorRepository();

        return new IngestService(
            source,
            new TwelveDataPriceNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            curated ?? new LocalCuratedStore(_root),
            cursors);
    }

    [Fact]
    public async Task First_run_writes_raw_and_curated_and_advances_the_cursor()
    {
        var service = Build(Body, out var cursors);

        var result = await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1", TestContext.Current.CancellationToken);

        result.RowsWritten.ShouldBe(1);
        result.SkippedUnchanged.ShouldBeFalse();
        result.RawKey.ShouldNotBeNull();

        var cursor = await cursors.GetAsync("prices_daily", "AAPL", TestContext.Current.CancellationToken);
        cursor!.LastEffectiveDate.ShouldBe(new DateOnly(2020, 8, 31));
        cursor.LastContentHash.ShouldBe(ContentHash.Sha256(Body));
    }

    [Fact]
    public async Task Identical_payload_short_circuits_without_writing_anything()
    {
        var service = Build(Body, out _);
        await service.IngestSymbolAsync("AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1", TestContext.Current.CancellationToken);

        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length;

        var second = await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-2", TestContext.Current.CancellationToken);

        second.SkippedUnchanged.ShouldBeTrue();
        second.RowsWritten.ShouldBe(0);
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length.ShouldBe(before);
    }

    [Fact]
    public async Task Backfilled_bars_are_tagged_inferred()
    {
        // Asserted against what reaches the curated store rather than by reading
        // Parquet back: Task 12 already proves the writer round-trips through
        // DuckDB, and referencing DuckDB here would copy ~315MB of native
        // binaries for five platforms into a second test project.
        var curated = new CapturingCuratedStore();
        var service = Build(Body, out _, curated);

        await service.IngestSymbolAsync("AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1", TestContext.Current.CancellationToken);

        var bar = curated.Prices.ShouldHaveSingleItem();
        bar.ObservedAtKind.ShouldBe(ObservationKind.Inferred);
        bar.ObservedAt.ShouldBe(new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero));
        bar.RawKey.ShouldNotBeNullOrEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>Cursor repository backed by a dictionary, so these tests need no Docker.</summary>
public sealed class InMemoryCursorRepository : Storage.Dynamo.ICursorRepository
{
    private readonly Dictionary<string, Storage.Dynamo.Cursor> _cursors = [];

    public Task<Storage.Dynamo.Cursor?> GetAsync(string dataset, string symbol, CancellationToken ct) =>
        Task.FromResult(_cursors.GetValueOrDefault($"{dataset}#{symbol}"));

    public Task SaveAsync(Storage.Dynamo.Cursor cursor, CancellationToken ct)
    {
        _cursors[$"{cursor.Dataset}#{cursor.Symbol}"] = cursor;
        return Task.CompletedTask;
    }
}

/// <summary>Captures what the service appends, so assertions need no Parquet reader.</summary>
public sealed class CapturingCuratedStore : ICuratedStore
{
    public List<DailyBar> Prices { get; } = [];

    public List<CorporateAction> Actions { get; } = [];

    public Task<IReadOnlyList<string>> AppendPricesAsync(
        IReadOnlyList<DailyBar> bars, string ingestId, CancellationToken ct)
    {
        Prices.AddRange(bars);

        return Task.FromResult<IReadOnlyList<string>>([$"curated/dataset=prices_daily/part-{ingestId}.parquet"]);
    }

    public Task<IReadOnlyList<string>> AppendActionsAsync(
        IReadOnlyList<CorporateAction> actions, string ingestId, CancellationToken ct)
    {
        Actions.AddRange(actions);

        return Task.FromResult<IReadOnlyList<string>>([$"curated/dataset=corporate_actions/part-{ingestId}.parquet"]);
    }
}
