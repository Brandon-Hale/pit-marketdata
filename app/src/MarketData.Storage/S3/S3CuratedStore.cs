using Amazon.S3;
using Amazon.S3.Model;
using MarketData.Domain;
using MarketData.Storage.Local;

namespace MarketData.Storage.S3;

/// <summary>
/// S3-backed <see cref="ICuratedStore"/>. Parts are built locally in a temporary
/// directory by <see cref="LocalCuratedStore"/>, then uploaded under the same keys,
/// so both implementations produce byte-equivalent layouts.
/// </summary>
public sealed class S3CuratedStore(IAmazonS3 s3, string bucket) : ICuratedStore
{
    public Task<IReadOnlyList<string>> AppendPricesAsync(
        IReadOnlyList<DailyBar> bars, string ingestId, CancellationToken ct) =>
        StageAndUploadAsync((local, c) => local.AppendPricesAsync(bars, ingestId, c), ct);

    public Task<IReadOnlyList<string>> AppendActionsAsync(
        IReadOnlyList<CorporateAction> actions, string ingestId, CancellationToken ct) =>
        StageAndUploadAsync((local, c) => local.AppendActionsAsync(actions, ingestId, c), ct);

    private async Task<IReadOnlyList<string>> StageAndUploadAsync(
        Func<LocalCuratedStore, CancellationToken, Task<IReadOnlyList<string>>> write,
        CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetTempPath(), "pitmd-stage-" + Guid.NewGuid().ToString("N"));

        try
        {
            var keys = await write(new LocalCuratedStore(staging), ct);

            foreach (var key in keys)
            {
                var path = Path.Combine(staging, key.Replace('/', Path.DirectorySeparatorChar));

                await s3.PutObjectAsync(
                    new PutObjectRequest { BucketName = bucket, Key = key, FilePath = path },
                    ct);
            }

            return keys;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }
}
