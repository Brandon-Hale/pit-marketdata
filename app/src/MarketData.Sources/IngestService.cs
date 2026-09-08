using MarketData.Domain;
using MarketData.Storage;
using MarketData.Storage.Dynamo;

namespace MarketData.Sources;

/// <summary>
/// Fetches one symbol, short-circuits if the vendor payload is unchanged, applies
/// the observation policy, and appends only what was actually learned.
/// </summary>
public sealed class IngestService(
    IPriceSource source,
    IPriceNormaliser normaliser,
    ObservationPolicy policy,
    IRawStore rawStore,
    ICuratedStore curatedStore,
    ICursorRepository cursors)
{
    private const string Dataset = "prices_daily";

    public async Task<IngestResult> IngestSymbolAsync(
        string symbol, DateOnly from, DateOnly to, string ingestId, CancellationToken ct)
    {
        var envelope = await source.FetchDailyBarsAsync(symbol, from, to, ct);
        var cursor = await cursors.GetAsync(Dataset, symbol, ct);

        // Nothing changed at the vendor, so nothing was learned. No raw object is
        // written: an identical payload is not a new observation.
        if (cursor?.LastContentHash == envelope.ContentHash)
        {
            return new IngestResult(symbol, SkippedUnchanged: true, 0, null, []);
        }

        var runDate = DateOnly.FromDateTime(envelope.ObservedAt.UtcDateTime);
        var rawKey = await rawStore.WriteAsync(envelope, runDate, ct);

        var parsed = normaliser.Parse(envelope);
        var bars = new List<DailyBar>(parsed.Count);

        foreach (var bar in parsed)
        {
            // Prior knowledge is currently the cursor's watermark only; a bar at or
            // before it is treated as already known and unchanged. Task 20 of the
            // next plan replaces this with a per-date lookup through the query layer.
            var known = cursor?.LastEffectiveDate is { } last && bar.EffectiveDate <= last
                ? bar.Close
                : (decimal?)null;

            if (policy.Decide(bar.EffectiveDate, bar.Close, known, envelope.ObservedAt) is not { } decision)
            {
                continue;
            }

            bars.Add(new DailyBar(
                symbol, bar.EffectiveDate,
                bar.Open, bar.High, bar.Low, bar.Close, bar.Volume,
                bar.Currency, source.SourceId,
                decision.ObservedAt, decision.Kind,
                ingestId, rawKey));
        }

        var curatedKeys = bars.Count > 0
            ? await curatedStore.AppendPricesAsync(bars, ingestId, ct)
            : [];

        await cursors.SaveAsync(
            new Cursor(
                Dataset,
                symbol,
                parsed.Count > 0 ? parsed[^1].EffectiveDate : cursor?.LastEffectiveDate,
                envelope.ContentHash),
            ct);

        return new IngestResult(symbol, SkippedUnchanged: false, bars.Count, rawKey, curatedKeys);
    }
}
