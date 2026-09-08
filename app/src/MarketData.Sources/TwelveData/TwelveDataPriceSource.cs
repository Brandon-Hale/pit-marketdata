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

    /// <summary>
    /// Twelve Data answers a window containing no bars with HTTP 400 and this message. It
    /// is a normal outcome on a weekend or before the day's data is published, so it must
    /// not be treated as a failure.
    /// </summary>
    private static bool IsNoDataAvailable(string payload) =>
        payload.Contains("No data is available", StringComparison.OrdinalIgnoreCase);

    private async Task<RawEnvelope> FetchAsync(
        string symbol, string dataset, string path, CancellationToken ct)
    {
        var url = $"{path}&apikey={Uri.EscapeDataString(apiKey)}";

        using var response = await http.GetAsync(url, ct);

        // The body is read before the status is checked, because the vendor signals an
        // empty window with 400 rather than an empty 200 and only the body distinguishes
        // that from a real failure.
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            if (IsNoDataAvailable(payload))
            {
                throw new VendorNoDataException(symbol, dataset, "no rows in the requested window");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new VendorNotEntitledException(
                    symbol, dataset, "the vendor plan does not cover this endpoint for this symbol");
            }

            response.EnsureSuccessStatusCode();
        }

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
