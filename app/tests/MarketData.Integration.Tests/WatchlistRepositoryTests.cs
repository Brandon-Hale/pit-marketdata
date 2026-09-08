using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class WatchlistRepositoryTests(LocalStackFixture fixture)
{
    private static readonly DateTimeOffset Added = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Removed = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private WatchlistRepository Repo() => new(fixture.Dynamo, fixture.TableName);

    [Fact]
    public async Task Symbol_is_invisible_before_it_was_added()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var repo = Repo();
        await repo.AddAsync("NVDA", Added, TestContext.Current.CancellationToken);

        var active = await repo.ActiveAsync(
            new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        active.ShouldNotContain("NVDA");
    }

    [Fact]
    public async Task Symbol_is_visible_between_added_and_removed()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var repo = Repo();
        await repo.AddAsync("TSLA", Added, TestContext.Current.CancellationToken);
        await repo.RemoveAsync("TSLA", Removed, TestContext.Current.CancellationToken);

        var active = await repo.ActiveAsync(
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        active.ShouldContain("TSLA");
    }

    [Fact]
    public async Task Symbol_is_invisible_after_it_was_removed()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var repo = Repo();
        await repo.AddAsync("META", Added, TestContext.Current.CancellationToken);
        await repo.RemoveAsync("META", Removed, TestContext.Current.CancellationToken);

        var active = await repo.ActiveAsync(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        active.ShouldNotContain("META");
    }

    [Fact]
    public async Task Removal_is_a_soft_delete_that_preserves_the_record()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var repo = Repo();
        await repo.AddAsync("AMD", Added, TestContext.Current.CancellationToken);
        await repo.RemoveAsync("AMD", Removed, TestContext.Current.CancellationToken);

        // Still visible as at a date before removal: the row was not deleted.
        var active = await repo.ActiveAsync(Added.AddDays(1), TestContext.Current.CancellationToken);

        active.ShouldContain("AMD");
    }
}
