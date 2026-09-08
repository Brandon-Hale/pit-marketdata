using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Lambda.Tests;

public sealed class IngestRunnerTests
{
    [Fact]
    public async Task A_failing_symbol_does_not_stop_the_others()
    {
        var runner = new IngestRunner(
            new StubWatchlist(["BAD", "GOOD"]),
            (symbol, _) => symbol == "BAD"
                ? throw new HttpRequestException("vendor returned 429")
                : Task.FromResult(new IngestOutcome(RowsWritten: 3, ActionsWritten: 1, Skipped: false)),
            new StubRunRecords(),
            TimeProvider.System);

        var record = await runner.RunAsync("run-1", TestContext.Current.CancellationToken);

        record.SymbolsAttempted.ShouldBe(2);
        record.SymbolsSucceeded.ShouldBe(1);
        record.RowsWritten.ShouldBe(3);
        record.Errors.ShouldHaveSingleItem().ShouldContain("BAD");
    }

    [Fact]
    public async Task Skipped_symbols_are_counted_separately_from_failures()
    {
        var runner = new IngestRunner(
            new StubWatchlist(["AAPL"]),
            (_, _) => Task.FromResult(new IngestOutcome(0, 0, Skipped: true)),
            new StubRunRecords(),
            TimeProvider.System);

        var record = await runner.RunAsync("run-1", TestContext.Current.CancellationToken);

        record.SkippedUnchanged.ShouldBe(1);
        record.SymbolsSucceeded.ShouldBe(1);
        record.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_run_record_is_saved()
    {
        var records = new StubRunRecords();
        var runner = new IngestRunner(
            new StubWatchlist(["AAPL"]),
            (_, _) => Task.FromResult(new IngestOutcome(1, 0, false)),
            records,
            TimeProvider.System);

        await runner.RunAsync("run-1", TestContext.Current.CancellationToken);

        var saved = records.Saved.ShouldHaveSingleItem();
        saved.RunId.ShouldBe("run-1");
        saved.ExpiresAt.ShouldBeGreaterThan(saved.StartedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_as_a_symbol_error()
    {
        // A cancelled run means shutdown; continuing would waste the remaining symbols'
        // vendor credits and record a misleading success.
        var runner = new IngestRunner(
            new StubWatchlist(["AAPL", "MSFT"]),
            (_, _) => throw new OperationCanceledException(),
            new StubRunRecords(),
            TimeProvider.System);

        await Should.ThrowAsync<OperationCanceledException>(
            () => runner.RunAsync("run-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_empty_watchlist_is_a_clean_no_op()
    {
        var records = new StubRunRecords();
        var runner = new IngestRunner(
            new StubWatchlist([]),
            (_, _) => throw new InvalidOperationException("must not be called"),
            records,
            TimeProvider.System);

        var record = await runner.RunAsync("run-1", TestContext.Current.CancellationToken);

        record.SymbolsAttempted.ShouldBe(0);
        record.Errors.ShouldBeEmpty();
        records.Saved.ShouldHaveSingleItem();
    }
}

file sealed class StubWatchlist(IReadOnlyList<string> symbols) : IWatchlistRepository
{
    public Task AddAsync(string symbol, DateTimeOffset addedAt, CancellationToken ct) =>
        Task.CompletedTask;

    public Task RemoveAsync(string symbol, DateTimeOffset removedAt, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> ActiveAsync(DateTimeOffset asOf, CancellationToken ct) =>
        Task.FromResult(symbols);
}

file sealed class StubRunRecords : IRunRecordRepository
{
    public List<RunRecord> Saved { get; } = [];

    public Task SaveAsync(RunRecord record, CancellationToken ct)
    {
        Saved.Add(record);
        return Task.CompletedTask;
    }

    public Task<RunRecord?> GetAsync(string runId, CancellationToken ct) =>
        Task.FromResult(Saved.FirstOrDefault(r => r.RunId == runId));
}
