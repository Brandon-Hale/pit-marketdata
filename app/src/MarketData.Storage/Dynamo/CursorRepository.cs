using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace MarketData.Storage.Dynamo;

public interface ICursorRepository
{
    Task<Cursor?> GetAsync(string dataset, string symbol, CancellationToken ct);

    Task SaveAsync(Cursor cursor, CancellationToken ct);
}

/// <inheritdoc />
public sealed class CursorRepository(IAmazonDynamoDB dynamo, string tableName) : ICursorRepository
{
    public async Task<Cursor?> GetAsync(string dataset, string symbol, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = tableName, Key = Key(dataset, symbol) }, ct);

        if (response.Item is null || response.Item.Count == 0)
        {
            return null;
        }

        var last = response.Item.TryGetValue("last_effective_date", out var d) && d.S is { Length: > 0 }
            ? DateOnly.ParseExact(d.S, "yyyy-MM-dd")
            : (DateOnly?)null;

        var hash = response.Item.TryGetValue("last_content_hash", out var h) ? h.S : null;

        return new Cursor(dataset, symbol, last, hash);
    }

    public Task SaveAsync(Cursor cursor, CancellationToken ct)
    {
        var item = Key(cursor.Dataset, cursor.Symbol);
        item["last_content_hash"] = new AttributeValue(cursor.LastContentHash ?? string.Empty);
        item["last_effective_date"] = new AttributeValue(
            cursor.LastEffectiveDate?.ToString("yyyy-MM-dd") ?? string.Empty);

        return dynamo.PutItemAsync(new PutItemRequest { TableName = tableName, Item = item }, ct);
    }

    private static Dictionary<string, AttributeValue> Key(string dataset, string symbol) => new()
    {
        ["pk"] = new($"CURSOR#{dataset}"),
        ["sk"] = new($"SYMBOL#{symbol}")
    };
}
