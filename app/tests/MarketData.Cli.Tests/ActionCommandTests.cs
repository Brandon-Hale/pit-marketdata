using System.CommandLine;
using MarketData.Cli.Commands;
using MarketData.Domain;
using Shouldly;

namespace MarketData.Cli.Tests;

public sealed class ActionCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static (Command Command, List<CorporateAction> Saved) Build()
    {
        var saved = new List<CorporateAction>();

        var command = ActionCommand.Build(
            (action, _) => { saved.Add(action); return Task.CompletedTask; },
            new UsEquityPublicationClock(),
            () => Now);

        return (command, saved);
    }

    [Fact]
    public async Task Records_a_ten_for_one_split_as_an_exact_ratio()
    {
        var (command, saved) = Build();

        await command.Parse(["add", "NVDA", "--ex-date", "2024-06-10", "--split", "--from", "1", "--to", "10"])
            .InvokeAsync(null, TestContext.Current.CancellationToken);

        var action = saved.ShouldHaveSingleItem();
        action.Symbol.ShouldBe("NVDA");
        action.ActionType.ShouldBe(CorporateActionType.Split);
        // 1/10 exactly, the factor an old price is multiplied by.
        action.Ratio.ShouldBe(0.1m);
        action.Source.ShouldBe("MANUAL");
    }

    [Fact]
    public async Task A_seven_for_one_split_keeps_full_precision()
    {
        // The whole reason factors are taken rather than a typed ratio: 1/7 cannot be
        // written as a decimal literal without losing precision.
        var (command, saved) = Build();

        await command.Parse(["add", "AAPL", "--ex-date", "2014-06-09", "--split", "--from", "1", "--to", "7"])
            .InvokeAsync(null, TestContext.Current.CancellationToken);

        saved.Single().Ratio.ShouldBe(1m / 7m);
    }

    [Fact]
    public async Task Observed_at_is_the_ex_date_not_now()
    {
        // Stamping "now" would hide the split from every query before today, which is the
        // opposite of what a point-in-time store must do.
        var (command, saved) = Build();

        await command.Parse(["add", "NVDA", "--ex-date", "2024-06-10", "--split", "--from", "1", "--to", "10"])
            .InvokeAsync(null, TestContext.Current.CancellationToken);

        saved.Single().ObservedAt.ShouldBe(new DateTimeOffset(2024, 6, 10, 20, 15, 0, TimeSpan.Zero));
        saved.Single().ObservedAtKind.ShouldBe(ObservationKind.Inferred);
    }

    [Fact]
    public async Task Records_a_dividend()
    {
        var (command, saved) = Build();

        await command.Parse(["add", "AMZN", "--ex-date", "2026-05-22", "--dividend", "--amount", "0.25"])
            .InvokeAsync(null, TestContext.Current.CancellationToken);

        var action = saved.ShouldHaveSingleItem();
        action.ActionType.ShouldBe(CorporateActionType.Dividend);
        action.Amount.ShouldBe(0.25m);
        action.Ratio.ShouldBeNull();
    }

    [Fact]
    public async Task Neither_split_nor_dividend_is_rejected()
    {
        var (command, saved) = Build();

        var result = await command.Parse(["add", "NVDA", "--ex-date", "2024-06-10"])
            .InvokeAsync(null, TestContext.Current.CancellationToken);

        result.ShouldNotBe(0);
        saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_split_without_factors_is_rejected_rather_than_defaulted()
    {
        var (command, saved) = Build();

        var result = await command.Parse(["add", "NVDA", "--ex-date", "2024-06-10", "--split"])
            .InvokeAsync(null, TestContext.Current.CancellationToken);

        result.ShouldNotBe(0);
        saved.ShouldBeEmpty();
    }

    [Fact]
    public void An_ex_date_is_required()
    {
        var (command, _) = Build();

        command.Parse(["add", "NVDA", "--split", "--from", "1", "--to", "10"])
            .Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Symbols_are_normalised()
    {
        var (command, saved) = Build();

        await command.Parse(["add", " nvda ", "--ex-date", "2024-06-10", "--split", "--from", "1", "--to", "10"])
            .InvokeAsync(null, TestContext.Current.CancellationToken);

        saved.Single().Symbol.ShouldBe("NVDA");
    }
}
