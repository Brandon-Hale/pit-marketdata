using System.Globalization;
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
    /// <summary>A daily run never looks further back than a few trading days.</summary>
    private const int MaxRecentCloses = 10;

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

        // DynamoDB numbers are strings on the wire, so the culture must be pinned in both
        // directions: a comma decimal separator would write 231,40 and fail to read back.
        var recent = response.Item.TryGetValue("recent_closes", out var m) && m.M is { Count: > 0 }
            ? m.M.ToDictionary(
                kv => DateOnly.ParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                kv => decimal.Parse(kv.Value.N, CultureInfo.InvariantCulture))
            : null;

        return new Cursor(dataset, symbol, last, hash, recent);
    }

    public Task SaveAsync(Cursor cursor, CancellationToken ct)
    {
        var item = Key(cursor.Dataset, cursor.Symbol);
        item["last_content_hash"] = new AttributeValue(cursor.LastContentHash ?? string.Empty);
        item["last_effective_date"] = new AttributeValue(
            cursor.LastEffectiveDate?.ToString("yyyy-MM-dd") ?? string.Empty);

        if (cursor.RecentCloses is { Count: > 0 })
        {
            item["recent_closes"] = new AttributeValue
            {
                M = cursor.RecentCloses
                    .OrderByDescending(kv => kv.Key)
                    .Take(MaxRecentCloses)
                    .ToDictionary(
                        kv => kv.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        kv => new AttributeValue { N = kv.Value.ToString(CultureInfo.InvariantCulture) })
            };
        }

        return dynamo.PutItemAsync(new PutItemRequest { TableName = tableName, Item = item }, ct);
    }

    private static Dictionary<string, AttributeValue> Key(string dataset, string symbol) => new()
    {
        ["pk"] = new($"CURSOR#{dataset}"),
        ["sk"] = new($"SYMBOL#{symbol}")
    };
}
