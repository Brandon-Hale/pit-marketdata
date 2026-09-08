using MarketData.Sources.TwelveData;
using MarketData.Storage;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class TwelveDataPriceNormaliserTests
{
    private const string Body =
        """
        {"meta":{"symbol":"AAPL","currency":"USD"},
         "values":[
           {"datetime":"2020-09-01","open":"132.76","high":"134.80","low":"130.53","close":"134.18","volume":"151948100"},
           {"datetime":"2020-08-31","open":"127.58","high":"131.00","low":"126.00","close":"129.04","volume":"225702700"}
         ]}
        """;

    private static RawEnvelope Envelope(string payload = Body) => new(
        "twelvedata", "AAPL", "prices_daily",
        new DateTimeOffset(2026, 9, 8, 22, 15, 3, TimeSpan.Zero),
        "https://api.twelvedata.com/time_series?symbol=AAPL&apikey=REDACTED",
        ContentHash.Sha256(payload), payload);

    private readonly TwelveDataPriceNormaliser _normaliser = new();

    [Fact]
    public void Parses_every_bar_with_exact_decimals()
    {
        var bars = _normaliser.Parse(Envelope());

        bars.Count.ShouldBe(2);
        bars.ShouldContain(b => b.EffectiveDate == new DateOnly(2020, 8, 31) && b.Close == 129.04m);
        bars.ShouldContain(b => b.EffectiveDate == new DateOnly(2020, 9, 1) && b.Volume == 151_948_100L);
    }

    [Fact]
    public void Returns_bars_in_ascending_date_order_regardless_of_payload_order()
    {
        var bars = _normaliser.Parse(Envelope());

        bars.Select(b => b.EffectiveDate).ShouldBe(bars.Select(b => b.EffectiveDate).Order());
    }

    [Fact]
    public void Is_a_pure_function_of_the_envelope()
    {
        var envelope = Envelope();

        _normaliser.Parse(envelope).ShouldBe(_normaliser.Parse(envelope));
    }

    [Fact]
    public void Returns_nothing_for_a_payload_with_no_values()
    {
        _normaliser.Parse(Envelope("""{"status":"error","message":"no data"}""")).ShouldBeEmpty();
    }
}
