using MarketData.Cli.Commands;
using MarketData.Domain;
using MarketData.Sources;
using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Cli.Tests;

public sealed class QueryCommandTests
{
    private static readonly Func<string, DateOnly, DateOnly, DateTimeOffset, PriceAdjustment,
        ObservationMode, CancellationToken, Task> NoOp = (_, _, _, _, _, _, _) => Task.CompletedTask;

    [Fact]
    public void Query_without_as_of_is_a_usage_error()
    {
        // The single most important assertion in the CLI: a default here would reintroduce
        // lookahead bias at every interactive call site.
        var result = QueryCommand.Build(NoOp).Parse(["AAPL", "--on", "2020-06-15"]);

        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Query_with_as_of_parses_cleanly()
    {
        var result = QueryCommand.Build(NoOp)
            .Parse(["AAPL", "--on", "2020-06-15", "--as-of", "2020-07-01"]);

        result.Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("none", PriceAdjustment.None)]
    [InlineData("splits", PriceAdjustment.SplitsOnly)]
    [InlineData("all", PriceAdjustment.SplitsAndDividends)]
    [InlineData("SPLITS", PriceAdjustment.SplitsOnly)]
    public void Adjust_maps_to_the_right_enum(string text, PriceAdjustment expected)
    {
        QueryCommand.ParseAdjustment(text).ShouldBe(expected);
    }

    [Fact]
    public void An_unknown_adjust_value_is_rejected_rather_than_defaulted()
    {
        Should.Throw<ArgumentException>(() => QueryCommand.ParseAdjustment("adjusted"))
            .Message.ShouldContain("none, splits or all");
    }
}

public sealed class WatchlistCommandTests
{
    [Fact]
    public async Task Add_records_the_symbol_upper_cased_with_the_supplied_instant()
    {
        var repo = new RecordingWatchlist();
        var at = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

        await WatchlistCommand.AddAsync(repo, " aapl ", at, TestContext.Current.CancellationToken);

        // The store must not end up with both AAPL and aapl.
        repo.Added.ShouldHaveSingleItem().ShouldBe(("AAPL", at));
    }

    [Fact]
    public async Task List_passes_the_as_of_through()
    {
        var repo = new RecordingWatchlist();
        var asOf = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await WatchlistCommand.ListAsync(repo, asOf, TestContext.Current.CancellationToken);

        repo.AsOfQueried.ShouldHaveSingleItem().ShouldBe(asOf);
    }
}

public sealed class BackfillCommandTests
{
    [Fact]
    public async Task Waits_between_symbols_to_respect_the_per_minute_limit()
    {
        var delays = new List<TimeSpan>();

        await BackfillCommand.RunAsync(
            ["AAPL", "MSFT", "NVDA"],
            new DateOnly(2015, 1, 1),
            new DateOnly(2026, 9, 8),
            new RateLimit(RequestsPerDay: 800, RequestsPerMinute: 8),
            (_, _, _, _) => Task.FromResult(0),
            (span, _) => { delays.Add(span); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        // Three credits per symbol against eight per minute: 22.5s between symbols.
        delays.Count.ShouldBe(2);
        delays.ShouldAllBe(d => d == TimeSpan.FromSeconds(22.5));
    }

    [Fact]
    public async Task A_single_symbol_needs_no_pause()
    {
        var delays = new List<TimeSpan>();

        await BackfillCommand.RunAsync(
            ["AAPL"], new DateOnly(2015, 1, 1), new DateOnly(2026, 9, 8),
            new RateLimit(800, 8),
            (_, _, _, _) => Task.FromResult(5),
            (span, _) => { delays.Add(span); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task Symbols_are_normalised_before_ingest()
    {
        var seen = new List<string>();

        await BackfillCommand.RunAsync(
            [" aapl "], new DateOnly(2015, 1, 1), new DateOnly(2026, 9, 8),
            new RateLimit(800, 8),
            (symbol, _, _, _) => { seen.Add(symbol); return Task.FromResult(0); },
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        seen.ShouldHaveSingleItem().ShouldBe("AAPL");
    }
}

file sealed class RecordingWatchlist : IWatchlistRepository
{
    public List<(string Symbol, DateTimeOffset At)> Added { get; } = [];

    public List<DateTimeOffset> AsOfQueried { get; } = [];

    public Task AddAsync(string symbol, DateTimeOffset addedAt, CancellationToken ct)
    {
        Added.Add((symbol, addedAt));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string symbol, DateTimeOffset removedAt, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> ActiveAsync(DateTimeOffset asOf, CancellationToken ct)
    {
        AsOfQueried.Add(asOf);
        return Task.FromResult<IReadOnlyList<string>>(["AAPL"]);
    }
}
