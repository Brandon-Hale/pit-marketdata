using MarketData.Domain;
using MarketData.Sources.TwelveData;
using MarketData.Storage;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class TwelveDataActionsNormaliserTests
{
    private const string SplitsBody =
        """
        {"meta":{"symbol":"AAPL","currency":"USD"},
         "splits":[
           {"date":"2020-08-31","description":"4-for-1 split","ratio":0.25,"from_factor":4,"to_factor":1},
           {"date":"2014-06-09","description":"7-for-1 split","ratio":0.14286,"from_factor":7,"to_factor":1}
         ]}
        """;

    private const string DividendsBody =
        """
        {"meta":{"symbol":"AAPL","currency":"USD"},
         "dividends":[{"ex_date":"2026-08-10","amount":0.27},{"ex_date":"2026-05-11","amount":0.27}]}
        """;

    private static RawEnvelope Envelope(string dataset, string payload) => new(
        "twelvedata", "AAPL", dataset,
        new DateTimeOffset(2026, 9, 8, 22, 15, 3, TimeSpan.Zero),
        "https://api.twelvedata.com/x?apikey=REDACTED",
        ContentHash.Sha256(payload), payload);

    private readonly TwelveDataActionsNormaliser _normaliser = new();

    [Fact]
    public void Parses_the_aapl_four_for_one_split()
    {
        var actions = _normaliser.ParseSplits(Envelope("splits", SplitsBody));

        var split = actions.Single(a => a.ExDate == new DateOnly(2020, 8, 31));
        split.ActionType.ShouldBe(CorporateActionType.Split);
        split.Ratio.ShouldBe(0.25m);
        split.Amount.ShouldBeNull();
    }

    [Fact]
    public void Derives_the_ratio_from_the_exact_factors_not_the_rounded_field()
    {
        // The vendor reports 0.14286 for a 7-for-1 split. The factors are exact
        // integers, so the derived ratio is 1/7 to full decimal precision. Trusting
        // the rounded field would push ~3.5e-6 of relative error into every
        // adjustment factor computed downstream.
        var actions = _normaliser.ParseSplits(Envelope("splits", SplitsBody));

        var split = actions.Single(a => a.ExDate == new DateOnly(2014, 6, 9));

        split.Ratio.ShouldBe(1m / 7m);
        split.Ratio.ShouldNotBe(0.14286m);
    }

    [Fact]
    public void Falls_back_to_the_ratio_field_when_factors_are_absent()
    {
        const string body = """{"splits":[{"date":"2020-08-31","ratio":0.25}]}""";

        var actions = _normaliser.ParseSplits(Envelope("splits", body));

        actions.Single().Ratio.ShouldBe(0.25m);
    }

    [Fact]
    public void Parses_dividends_with_amount_and_no_ratio()
    {
        var actions = _normaliser.ParseDividends(Envelope("dividends", DividendsBody));

        var dividend = actions.Single(a => a.ExDate == new DateOnly(2026, 8, 10));
        dividend.ActionType.ShouldBe(CorporateActionType.Dividend);
        dividend.Amount.ShouldBe(0.27m);
        dividend.Ratio.ShouldBeNull();
    }

    [Fact]
    public void Returns_actions_in_ascending_ex_date_order()
    {
        var actions = _normaliser.ParseSplits(Envelope("splits", SplitsBody));

        actions.Select(a => a.ExDate).ShouldBe(actions.Select(a => a.ExDate).Order());
    }

    [Fact]
    public void Returns_nothing_for_a_symbol_with_no_actions()
    {
        _normaliser.ParseSplits(Envelope("splits", """{"splits":[]}""")).ShouldBeEmpty();
        _normaliser.ParseDividends(Envelope("dividends", """{"dividends":[]}""")).ShouldBeEmpty();
    }
}
