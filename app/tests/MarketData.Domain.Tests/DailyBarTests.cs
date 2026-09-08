using MarketData.Domain;
using Shouldly;

namespace MarketData.Domain.Tests;

public sealed class DailyBarTests
{
    private static DailyBar Bar(DateTimeOffset observedAt) => new(
        Symbol: "AAPL",
        EffectiveDate: new DateOnly(2020, 8, 31),
        Open: 127.58m, High: 131.00m, Low: 126.00m, Close: 129.04m,
        Volume: 225_702_700L,
        Currency: "USD",
        Source: "twelvedata",
        ObservedAt: observedAt,
        ObservedAtKind: ObservationKind.Inferred,
        IngestId: "run-1",
        RawKey: "raw/x.json");

    [Fact]
    public void Accepts_a_utc_observed_at()
    {
        var bar = Bar(new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero));

        bar.ObservedAt.Offset.ShouldBe(TimeSpan.Zero);
        bar.Close.ShouldBe(129.04m);
    }

    [Fact]
    public void Rejects_a_non_utc_observed_at()
    {
        var sydney = new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.FromHours(10));

        Should.Throw<ArgumentException>(() => Bar(sydney));
    }

    [Fact]
    public void Preserves_decimal_precision_exactly()
    {
        var bar = Bar(DateTimeOffset.UnixEpoch) with { Close = 328.31000m };

        bar.Close.ToString().ShouldBe("328.31000");
    }
}
