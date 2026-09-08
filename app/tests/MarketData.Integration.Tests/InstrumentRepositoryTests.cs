using MarketData.Domain;
using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class InstrumentRepositoryTests(LocalStackFixture fixture)
{
    private InstrumentRepository Repo() => new(fixture.Dynamo, fixture.TableName);

    private static Instrument Apple() => new(
        Symbol: "AAPL",
        Exchange: "NASDAQ",
        MicCode: "XNGS",
        Name: "Apple Inc.",
        Currency: "USD",
        Cik: "320193",
        Sector: "Technology",
        ListingDate: new DateOnly(1980, 12, 12),
        FirstSeenAt: new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
        IsActive: true);

    [Fact]
    public async Task Round_trips_every_field_including_cik()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var repo = Repo();
        await repo.UpsertAsync(Apple(), TestContext.Current.CancellationToken);

        var read = await repo.GetAsync("AAPL", TestContext.Current.CancellationToken);

        read.ShouldBe(Apple());
        read!.Cik.ShouldBe("320193");
    }

    [Fact]
    public async Task Returns_null_for_an_unknown_symbol()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        (await Repo().GetAsync("NOPE", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Tolerates_a_missing_cik_and_sector()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var repo = Repo();
        var etf = Apple() with { Symbol = "SPY", Cik = null, Sector = null, ListingDate = null };
        await repo.UpsertAsync(etf, TestContext.Current.CancellationToken);

        var read = await repo.GetAsync("SPY", TestContext.Current.CancellationToken);

        read!.Cik.ShouldBeNull();
        read.Sector.ShouldBeNull();
        read.ListingDate.ShouldBeNull();
    }
}
