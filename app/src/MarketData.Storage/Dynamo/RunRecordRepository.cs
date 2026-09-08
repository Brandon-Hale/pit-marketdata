using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace MarketData.Storage.Dynamo;

public interface IRunRecordRepository
{
    Task SaveAsync(RunRecord record, CancellationToken ct);

    Task<RunRecord?> GetAsync(string runId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class RunRecordRepository(IAmazonDynamoDB dynamo, string tableName) : IRunRecordRepository
{
    public Task SaveAsync(RunRecord record, CancellationToken ct)
    {
        var item = Key(record.RunId);
        item["started_at"] = new AttributeValue(Iso(record.StartedAt));
        item["finished_at"] = new AttributeValue(Iso(record.FinishedAt));
        item["symbols_attempted"] = Number(record.SymbolsAttempted);
        item["symbols_succeeded"] = Number(record.SymbolsSucceeded);
        item["rows_written"] = Number(record.RowsWritten);
        item["actions_written"] = Number(record.ActionsWritten);
        item["skipped_unchanged"] = Number(record.SkippedUnchanged);
        item["expires_at"] = Number(record.ExpiresAt);

        if (record.Errors.Count > 0)
        {
            item["errors"] = new AttributeValue { SS = record.Errors.ToList() };
        }

        return dynamo.PutItemAsync(new PutItemRequest { TableName = tableName, Item = item }, ct);
    }

    public async Task<RunRecord?> GetAsync(string runId, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = tableName, Key = Key(runId) }, ct);

        if (response.Item is null || response.Item.Count == 0)
        {
            return null;
        }

        var item = response.Item;

        return new RunRecord(
            runId,
            DateTimeOffset.Parse(item["started_at"].S, CultureInfo.InvariantCulture).ToUniversalTime(),
            DateTimeOffset.Parse(item["finished_at"].S, CultureInfo.InvariantCulture).ToUniversalTime(),
            Int(item, "symbols_attempted"),
            Int(item, "symbols_succeeded"),
            Int(item, "rows_written"),
            Int(item, "actions_written"),
            Int(item, "skipped_unchanged"),
            item.TryGetValue("errors", out var e) && e.SS is { Count: > 0 } ? e.SS : []);
    }

    private static AttributeValue Number(long value) =>
        new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static int Int(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var v) ? int.Parse(v.N, CultureInfo.InvariantCulture) : 0;

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static Dictionary<string, AttributeValue> Key(string runId) => new()
    {
        ["pk"] = new($"RUN#{runId}"),
        ["sk"] = new("META")
    };
}
