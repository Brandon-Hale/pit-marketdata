using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;

namespace MarketData.Storage.S3;

/// <summary>S3-backed <see cref="IRawStore"/>. Objects are written once and never modified.</summary>
public sealed class S3RawStore(IAmazonS3 s3, string bucket) : IRawStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<string> WriteAsync(RawEnvelope envelope, DateOnly runDate, CancellationToken ct)
    {
        var key = RawKey.For(envelope.SourceId, envelope.Dataset, runDate, envelope.Symbol);

        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = JsonSerializer.Serialize(envelope, Json),
                ContentType = "application/json"
            },
            ct);

        return key;
    }

    public async Task<RawEnvelope> ReadAsync(string key, CancellationToken ct)
    {
        using var response = await s3.GetObjectAsync(bucket, key, ct);
        using var reader = new StreamReader(response.ResponseStream);

        var text = await reader.ReadToEndAsync(ct);

        return JsonSerializer.Deserialize<RawEnvelope>(text, Json)
               ?? throw new InvalidDataException($"Raw object '{key}' deserialized to null.");
    }

    public async IAsyncEnumerable<string> ListAsync(
        string keyPrefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;

        do
        {
            var page = await s3.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket, Prefix = keyPrefix, ContinuationToken = token },
                ct);

            foreach (var o in page.S3Objects)
            {
                yield return o.Key;
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
    }
}
