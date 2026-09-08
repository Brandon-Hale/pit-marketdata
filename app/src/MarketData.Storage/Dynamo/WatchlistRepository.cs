using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace MarketData.Storage.Dynamo;

/// <summary>Universe membership over time. Removal is a soft delete.</summary>
public interface IWatchlistRepository
{
    Task AddAsync(string symbol, DateTimeOffset addedAt, CancellationToken ct);

    Task RemoveAsync(string symbol, DateTimeOffset removedAt, CancellationToken ct);

    /// <summary>Symbols being tracked as at <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<string>> ActiveAsync(DateTimeOffset asOf, CancellationToken ct);
}

/// <inheritdoc />
public sealed class WatchlistRepository(IAmazonDynamoDB dynamo, string tableName) : IWatchlistRepository
{
    private const string PartitionKey = "WATCHLIST";

    public Task AddAsync(string symbol, DateTimeOffset addedAt, CancellationToken ct) =>
        dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new(PartitionKey),
                    ["sk"] = new($"SYMBOL#{symbol}"),
                    ["symbol"] = new(symbol),
                    ["added_at"] = new(Iso(addedAt))
                }
            },
            ct);

    public Task RemoveAsync(string symbol, DateTimeOffset removedAt, CancellationToken ct) =>
        dynamo.UpdateItemAsync(
            new UpdateItemRequest
            {
                TableName = tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new(PartitionKey),
                    ["sk"] = new($"SYMBOL#{symbol}")
                },
                UpdateExpression = "SET removed_at = :r",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":r"] = new(Iso(removedAt))
                }
            },
            ct);

    public async Task<IReadOnlyList<string>> ActiveAsync(DateTimeOffset asOf, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = tableName,
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new(PartitionKey)
                }
            },
            ct);

        var stamp = Iso(asOf);

        return response.Items
            .Where(item => string.CompareOrdinal(item["added_at"].S, stamp) <= 0)
            .Where(item => !item.TryGetValue("removed_at", out var removed)
                           || string.CompareOrdinal(removed.S, stamp) > 0)
            .Select(item => item["symbol"].S)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    // Round-trip ISO-8601 in UTC sorts lexicographically in the same order as
    // chronologically, which is what makes the string comparisons above correct.
    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
}
