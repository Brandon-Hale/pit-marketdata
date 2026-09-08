using MarketData.Storage;

namespace MarketData.Sources.TwelveData;

/// <inheritdoc />
public sealed class TwelveDataPriceSource(
    HttpClient http,
    string apiKey,
    TimeProvider time) : IPriceSource
{
    public string SourceId => "twelvedata";

    /// <summary>Free tier: 800 credits/day, 8 credits/minute (verified 2026-09-08).</summary>
    public RateLimit Limits => new(RequestsPerDay: 800, RequestsPerMinute: 8);

    public Task<RawEnvelope> FetchDailyBarsAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct) =>
        FetchAsync(
            symbol,
            dataset: "prices_daily",
            path: $"time_series?symbol={Uri.EscapeDataString(symbol)}&interval=1day&outputsize=5000" +
                  $"&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}",
            ct);

    public Task<RawEnvelope> FetchSplitsAsync(string symbol, CancellationToken ct) =>
        FetchAsync(symbol, "splits", $"splits?symbol={Uri.EscapeDataString(symbol)}&range=full", ct);

    public Task<RawEnvelope> FetchDividendsAsync(string symbol, CancellationToken ct) =>
        FetchAsync(symbol, "dividends", $"dividends?symbol={Uri.EscapeDataString(symbol)}&range=full", ct);

    private async Task<RawEnvelope> FetchAsync(
        string symbol, string dataset, string path, CancellationToken ct)
    {
        var url = $"{path}&apikey={Uri.EscapeDataString(apiKey)}";

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(ct);

        // Stamped once, here. Nothing downstream reads a clock.
        var observedAt = time.GetUtcNow().ToUniversalTime();

        return new RawEnvelope(
            SourceId: SourceId,
            Symbol: symbol,
            Dataset: dataset,
            ObservedAt: observedAt,
            RequestUrl: UrlRedactor.Redact(new Uri(http.BaseAddress!, url).ToString()),
            ContentHash: ContentHash.Sha256(payload),
            Payload: payload);
    }
}
