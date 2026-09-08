using MarketData.Domain;
using Shouldly;

namespace MarketData.Query.Tests;

public sealed class AdjustedPricesTests : IDisposable
{
    private readonly QueryFixture _fixture = new();

    private static readonly DateOnly Day = new(2020, 6, 15);
    private static readonly DateTimeOffset BarObserved = new(2020, 6, 15, 20, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SplitObserved = new(2020, 8, 31, 20, 15, 0, TimeSpan.Zero);

    private async Task SeedAsync()
    {
        await _fixture.WriteBarsAsync(QueryFixture.Bar(Day, 342.99m, BarObserved));
        await _fixture.WriteActionsAsync(
            QueryFixture.Split(new DateOnly(2020, 8, 31), 0.25m, SplitObserved));
    }

    [Fact]
    public async Task Unadjusted_returns_the_price_quoted_that_day()
    {
        await SeedAsync();

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day, DateTimeOffset.UtcNow,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.Single().Close.ShouldBe(342.99m);
    }

    [Fact]
    public async Task Split_adjusted_applies_a_known_split()
    {
        await SeedAsync();

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day, DateTimeOffset.UtcNow,
            PriceAdjustment.SplitsOnly, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.Single().Close.ShouldBe(85.7475m);
    }

    [Fact]
    public async Task Volume_is_adjusted_inversely()
    {
        await SeedAsync();

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day, DateTimeOffset.UtcNow,
            PriceAdjustment.SplitsOnly, ObservationMode.All, TestContext.Current.CancellationToken);

        // 1,000,000 traded unadjusted becomes 4,000,000 post-split-equivalent shares.
        bars.Single().Volume.ShouldBe(4_000_000L);
    }

    [Fact]
    public async Task Corporate_actions_come_back_filtered_by_as_of()
    {
        await SeedAsync();

        var before = await _fixture.Query.GetCorporateActionsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
            new DateTimeOffset(2020, 7, 1, 0, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken);

        var after = await _fixture.Query.GetCorporateActionsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
            DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        before.ShouldBeEmpty();
        after.ShouldHaveSingleItem().Ratio.ShouldBe(0.25m);
    }

    public void Dispose() => _fixture.Dispose();
}
