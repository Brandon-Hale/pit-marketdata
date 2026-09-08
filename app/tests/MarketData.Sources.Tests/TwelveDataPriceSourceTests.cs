using MarketData.Sources.TwelveData;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class TwelveDataPriceSourceTests
{
    private const string Body =
        """{"meta":{"symbol":"AAPL"},"values":[{"datetime":"2020-08-31","open":"127.58","high":"131.00","low":"126.00","close":"129.04","volume":"225702700"}]}""";

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 22, 15, 3, TimeSpan.Zero);

    private static (TwelveDataPriceSource Source, StubHandler Handler) Build(string body = Body)
    {
        var handler = new StubHandler(body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var time = new FakeTimeProvider(Now);

        return (new TwelveDataPriceSource(client, "SECRET", time), handler);
    }

    [Fact]
    public async Task Stamps_observed_at_from_the_injected_clock()
    {
        var (source, _) = Build();

        var envelope = await source.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), TestContext.Current.CancellationToken);

        envelope.ObservedAt.ShouldBe(Now);
        envelope.ObservedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Stores_the_payload_verbatim_and_hashes_it()
    {
        var (source, _) = Build();

        var envelope = await source.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), TestContext.Current.CancellationToken);

        envelope.Payload.ShouldBe(Body);
        envelope.ContentHash.ShouldBe(Storage.ContentHash.Sha256(Body));
        envelope.Dataset.ShouldBe("prices_daily");
        envelope.SourceId.ShouldBe("twelvedata");
    }

    [Fact]
    public async Task Never_records_the_api_key()
    {
        var (source, handler) = Build();

        var envelope = await source.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), TestContext.Current.CancellationToken);

        handler.LastRequestUri!.Query.ShouldContain("SECRET");   // the real call carries it
        envelope.RequestUrl.ShouldNotContain("SECRET");          // the stored envelope does not
        envelope.RequestUrl.ShouldContain("apikey=REDACTED");
    }

    [Fact]
    public async Task Requests_only_the_window_asked_for()
    {
        var (source, handler) = Build();

        await source.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);

        handler.LastRequestUri!.Query.ShouldContain("start_date=2026-09-01");
        handler.LastRequestUri.Query.ShouldContain("end_date=2026-09-08");
    }

    [Fact]
    public async Task Splits_and_dividends_use_their_own_raw_datasets()
    {
        var (source, _) = Build("""{"splits":[]}""");

        var splits = await source.FetchSplitsAsync("AAPL", TestContext.Current.CancellationToken);
        var dividends = await source.FetchDividendsAsync("AAPL", TestContext.Current.CancellationToken);

        splits.Dataset.ShouldBe("splits");
        dividends.Dataset.ShouldBe("dividends");
    }

    [Fact]
    public async Task Throws_on_a_vendor_error_status()
    {
        var handler = new StubHandler("""{"code":429,"message":"limit"}""", System.Net.HttpStatusCode.TooManyRequests);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var source = new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now));

        await Should.ThrowAsync<HttpRequestException>(() => source.FetchSplitsAsync("AAPL", TestContext.Current.CancellationToken));
    }
}
