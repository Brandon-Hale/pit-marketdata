using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using MarketData.Domain;

namespace MarketData.Storage.Dynamo;

public interface IInstrumentRepository
{
    Task<Instrument?> GetAsync(string symbol, CancellationToken ct);

    Task UpsertAsync(Instrument instrument, CancellationToken ct);
}

/// <inheritdoc />
public sealed class InstrumentRepository(IAmazonDynamoDB dynamo, string tableName) : IInstrumentRepository
{
    public async Task<Instrument?> GetAsync(string symbol, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = tableName, Key = Key(symbol) }, ct);

        if (response.Item is null || response.Item.Count == 0)
        {
            return null;
        }

        var item = response.Item;

        return new Instrument(
            Symbol: item["symbol"].S,
            Exchange: item["exchange"].S,
            MicCode: item["mic_code"].S,
            Name: item["name"].S,
            Currency: item["currency"].S,
            Cik: Optional(item, "cik"),
            Sector: Optional(item, "sector"),
            ListingDate: Optional(item, "listing_date") is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd") : null,
            FirstSeenAt: DateTimeOffset.Parse(item["first_seen_at"].S).ToUniversalTime(),
            IsActive: item["is_active"].BOOL ?? true);
    }

    public Task UpsertAsync(Instrument instrument, CancellationToken ct)
    {
        var item = Key(instrument.Symbol);
        item["symbol"] = new AttributeValue(instrument.Symbol);
        item["exchange"] = new AttributeValue(instrument.Exchange);
        item["mic_code"] = new AttributeValue(instrument.MicCode);
        item["name"] = new AttributeValue(instrument.Name);
        item["currency"] = new AttributeValue(instrument.Currency);
        item["first_seen_at"] = new AttributeValue(
            instrument.FirstSeenAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        item["is_active"] = new AttributeValue { BOOL = instrument.IsActive };

        Set(item, "cik", instrument.Cik);
        Set(item, "sector", instrument.Sector);
        Set(item, "listing_date", instrument.ListingDate?.ToString("yyyy-MM-dd"));

        return dynamo.PutItemAsync(new PutItemRequest { TableName = tableName, Item = item }, ct);
    }

    // DynamoDB rejects empty strings in some contexts, so absent values are simply
    // not written rather than written as "".
    private static void Set(Dictionary<string, AttributeValue> item, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            item[name] = new AttributeValue(value);
        }
    }

    private static string? Optional(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var v) && v.S is { Length: > 0 } ? v.S : null;

    private static Dictionary<string, AttributeValue> Key(string symbol) => new()
    {
        ["pk"] = new($"INSTRUMENT#{symbol}"),
        ["sk"] = new("META")
    };
}
