using MarketData.Domain;
using Shouldly;

namespace MarketData.Domain.Tests;

public sealed class AdjustmentCalculatorTests
{
    private static CorporateAction Split(DateOnly exDate, decimal ratio) => new(
        "AAPL", exDate, CorporateActionType.Split, ratio, null, "USD", "twelvedata",
        new DateTimeOffset(exDate.ToDateTime(new TimeOnly(20, 15)), TimeSpan.Zero),
        ObservationKind.Inferred, "run-1", "raw/x.json");

    // The two real AAPL splits, as the vendor reports them.
    private static readonly CorporateAction FourForOne = Split(new DateOnly(2020, 8, 31), 0.25m);
    private static readonly CorporateAction SevenForOne = Split(new DateOnly(2014, 6, 9), 1m / 7m);

    [Fact]
    public void No_actions_gives_exactly_one()
    {
        AdjustmentCalculator.SplitFactor([], new DateOnly(2020, 6, 15)).ShouldBe(1m);
    }

    [Fact]
    public void A_later_split_scales_an_earlier_bar()
    {
        var factor = AdjustmentCalculator.SplitFactor([FourForOne], new DateOnly(2020, 6, 15));

        factor.ShouldBe(0.25m);
        // Apple really closed at 342.99 that day; the vendor reports 85.7475.
        (342.99m * factor).ShouldBe(85.7475m);
    }

    [Fact]
    public void A_split_on_the_bar_date_itself_is_not_applied()
    {
        // 2020-08-31 closed at 129.04, already post-split.
        AdjustmentCalculator.SplitFactor([FourForOne], new DateOnly(2020, 8, 31)).ShouldBe(1m);
    }

    [Fact]
    public void An_earlier_split_does_not_affect_a_later_bar()
    {
        AdjustmentCalculator.SplitFactor([SevenForOne], new DateOnly(2020, 6, 15)).ShouldBe(1m);
    }

    [Fact]
    public void Factors_compound_across_two_splits()
    {
        var factor = AdjustmentCalculator.SplitFactor(
            [FourForOne, SevenForOne], new DateOnly(2014, 6, 6));

        // 7-for-1 then 4-for-1 = 28x. 645.57 / 28 = 23.05607...
        (645.57m * factor).ShouldBe(23.056071m, tolerance: 0.000001m);
    }

    [Fact]
    public void Dividends_do_not_contribute_to_the_split_factor()
    {
        var dividend = new CorporateAction(
            "AAPL", new DateOnly(2020, 8, 7), CorporateActionType.Dividend, null, 0.205m,
            "USD", "twelvedata", new DateTimeOffset(2020, 8, 7, 20, 15, 0, TimeSpan.Zero),
            ObservationKind.Inferred, "run-1", "raw/x.json");

        AdjustmentCalculator.SplitFactor([dividend], new DateOnly(2020, 6, 15)).ShouldBe(1m);
    }

    [Fact]
    public void A_split_with_a_null_ratio_is_ignored_rather_than_throwing()
    {
        var malformed = new CorporateAction(
            "AAPL", new DateOnly(2020, 8, 31), CorporateActionType.Split, null, null,
            "USD", "twelvedata", new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero),
            ObservationKind.Inferred, "run-1", "raw/x.json");

        AdjustmentCalculator.SplitFactor([malformed], new DateOnly(2020, 6, 15)).ShouldBe(1m);
    }
}
