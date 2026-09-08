using MarketData.Domain;
using Shouldly;

namespace MarketData.Query.Tests;

public sealed class VisibleBarsTests : IDisposable
{
    private readonly QueryFixture _fixture = new();

    private static readonly DateOnly Day = new(2020, 6, 15);

    [Fact]
    public async Task Returns_a_bar_observed_before_the_as_of()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 342.99m, new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero)));

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day,
            new DateTimeOffset(2020, 7, 1, 0, 0, 0, TimeSpan.Zero),
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.ShouldHaveSingleItem().Close.ShouldBe(342.99m);
    }

    [Fact]
    public async Task Returns_nothing_when_the_store_is_empty()
    {
        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day, DateTimeOffset.UtcNow,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.ShouldBeEmpty();
    }

    [Fact]
    public async Task Filters_by_symbol_and_date_range()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(new DateOnly(2020, 6, 15), 342.99m, new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero)),
            QueryFixture.Bar(new DateOnly(2020, 7, 15), 380.00m, new DateTimeOffset(2020, 7, 15, 20, 15, 0, TimeSpan.Zero)));

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", new DateOnly(2020, 7, 1), new DateOnly(2020, 7, 31),
            DateTimeOffset.UtcNow,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.ShouldHaveSingleItem().EffectiveDate.ShouldBe(new DateOnly(2020, 7, 15));
    }

    [Fact]
    public async Task Returns_bars_in_ascending_date_order()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(new DateOnly(2020, 7, 15), 380.00m, new DateTimeOffset(2020, 7, 15, 20, 15, 0, TimeSpan.Zero)),
            QueryFixture.Bar(new DateOnly(2020, 6, 15), 342.99m, new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero)));

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
            DateTimeOffset.UtcNow,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.Select(b => b.EffectiveDate)
            .ShouldBe([new DateOnly(2020, 6, 15), new DateOnly(2020, 7, 15)]);
    }

    public void Dispose() => _fixture.Dispose();
}
