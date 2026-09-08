using System.Globalization;
using System.Text.Json;
using MarketData.Storage;

namespace MarketData.Sources.TwelveData;

/// <inheritdoc />
public sealed class TwelveDataPriceNormaliser : IPriceNormaliser
{
    public string SourceId => "twelvedata";

    public IReadOnlyList<ParsedBar> Parse(RawEnvelope raw)
    {
        using var document = JsonDocument.Parse(raw.Payload);

        if (!document.RootElement.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var currency = document.RootElement.TryGetProperty("meta", out var meta)
                       && meta.TryGetProperty("currency", out var c)
            ? c.GetString() ?? "USD"
            : "USD";

        return values.EnumerateArray()
            .Select(v => new ParsedBar(
                DateOnly.ParseExact(Text(v, "datetime"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Dec(v, "open"), Dec(v, "high"), Dec(v, "low"), Dec(v, "close"),
                long.Parse(Text(v, "volume"), CultureInfo.InvariantCulture),
                currency))
            .OrderBy(b => b.EffectiveDate)
            .ToList();
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"Property '{name}' was null.");

    // Vendor values arrive as exact decimal strings such as "328.31000".
    // Parsing to decimal preserves them; double would not.
    private static decimal Dec(JsonElement element, string name) =>
        decimal.Parse(Text(element, name), NumberStyles.Number, CultureInfo.InvariantCulture);
}
