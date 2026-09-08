using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class CursorRepositoryTests(LocalStackFixture fixture)
{
    private CursorRepository Repo() => new(fixture.Dynamo, fixture.TableName);

    [Fact]
    public async Task Round_trips_recent_closes()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var closes = new Dictionary<DateOnly, decimal>
        {
            [new DateOnly(2026, 9, 1)] = 231.40m,
            [new DateOnly(2026, 9, 2)] = 229.85m
        };

        await Repo().SaveAsync(
            new Cursor("prices_daily", "RC1", new DateOnly(2026, 9, 2), "sha256:x", closes),
            TestContext.Current.CancellationToken);

        var read = await Repo().GetAsync("prices_daily", "RC1", TestContext.Current.CancellationToken);

        read!.RecentCloses.ShouldNotBeNull();
        read.RecentCloses![new DateOnly(2026, 9, 1)].ShouldBe(231.40m);
        read.RecentCloses[new DateOnly(2026, 9, 2)].ShouldBe(229.85m);
    }

    [Fact]
    public async Task Keeps_only_the_ten_newest_closes()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var closes = Enumerable.Range(1, 25)
            .ToDictionary(d => new DateOnly(2026, 1, d), d => 100m + d);

        await Repo().SaveAsync(
            new Cursor("prices_daily", "RC2", new DateOnly(2026, 1, 25), "sha256:x", closes),
            TestContext.Current.CancellationToken);

        var read = await Repo().GetAsync("prices_daily", "RC2", TestContext.Current.CancellationToken);

        read!.RecentCloses!.Count.ShouldBe(10);
        read.RecentCloses.ShouldContainKey(new DateOnly(2026, 1, 25));
        read.RecentCloses.ShouldNotContainKey(new DateOnly(2026, 1, 15));
    }

    [Fact]
    public async Task A_cursor_without_closes_reads_back_empty()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await Repo().SaveAsync(
            new Cursor("prices_daily", "RC3", new DateOnly(2026, 1, 1), "sha256:x"),
            TestContext.Current.CancellationToken);

        var read = await Repo().GetAsync("prices_daily", "RC3", TestContext.Current.CancellationToken);

        (read!.RecentCloses is null || read.RecentCloses.Count == 0).ShouldBeTrue();
    }

    [Fact]
    public async Task Decimals_survive_a_culture_with_a_comma_separator()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var original = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            await Repo().SaveAsync(
                new Cursor("prices_daily", "RC4", new DateOnly(2026, 9, 2), "sha256:x",
                    new Dictionary<DateOnly, decimal> { [new DateOnly(2026, 9, 1)] = 231.40m }),
                TestContext.Current.CancellationToken);

            var read = await Repo().GetAsync("prices_daily", "RC4", TestContext.Current.CancellationToken);

            read!.RecentCloses![new DateOnly(2026, 9, 1)].ShouldBe(231.40m);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
