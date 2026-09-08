using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class CursorPriorKnowledgeTests
{
    private static async Task<ICursorRepository> SeededAsync(
        IReadOnlyDictionary<DateOnly, decimal>? closes)
    {
        var repo = new InMemoryCursorRepository();
        await repo.SaveAsync(
            new Cursor("prices_daily", "AAPL", new DateOnly(2026, 9, 5), "sha256:x", closes),
            CancellationToken.None);

        return repo;
    }

    [Fact]
    public async Task Returns_a_close_that_is_in_the_map()
    {
        var repo = await SeededAsync(new Dictionary<DateOnly, decimal>
        {
            [new DateOnly(2026, 9, 4)] = 231.40m
        });

        var lookup = CursorPriorKnowledge.From(repo, "prices_daily");

        (await lookup("AAPL", new DateOnly(2026, 9, 4), TestContext.Current.CancellationToken))
            .ShouldBe(231.40m);
    }

    [Fact]
    public async Task Returns_null_for_a_date_outside_the_map()
    {
        var repo = await SeededAsync(new Dictionary<DateOnly, decimal>
        {
            [new DateOnly(2026, 9, 4)] = 231.40m
        });

        var lookup = CursorPriorKnowledge.From(repo, "prices_daily");

        (await lookup("AAPL", new DateOnly(2020, 1, 2), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_cursor_at_all()
    {
        var lookup = CursorPriorKnowledge.From(new InMemoryCursorRepository(), "prices_daily");

        (await lookup("NOPE", new DateOnly(2026, 9, 4), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Reads_the_cursor_once_per_symbol_not_once_per_date()
    {
        // A backfill asks for thousands of dates; one DynamoDB read each would be absurd.
        var repo = new CountingCursorRepository();
        await repo.SaveAsync(
            new Cursor("prices_daily", "AAPL", new DateOnly(2026, 9, 5), "sha256:x",
                new Dictionary<DateOnly, decimal> { [new DateOnly(2026, 9, 4)] = 231.40m }),
            CancellationToken.None);

        var lookup = CursorPriorKnowledge.From(repo, "prices_daily");

        for (var i = 0; i < 5; i++)
        {
            await lookup("AAPL", new DateOnly(2026, 9, 4), TestContext.Current.CancellationToken);
        }

        repo.Gets.ShouldBe(1);
    }
}

/// <summary>Counts reads, so caching can be asserted rather than assumed.</summary>
public sealed class CountingCursorRepository : ICursorRepository
{
    private readonly InMemoryCursorRepository _inner = new();

    public int Gets { get; private set; }

    public Task<Cursor?> GetAsync(string dataset, string symbol, CancellationToken ct)
    {
        Gets++;
        return _inner.GetAsync(dataset, symbol, ct);
    }

    public Task SaveAsync(Cursor cursor, CancellationToken ct) => _inner.SaveAsync(cursor, ct);
}
