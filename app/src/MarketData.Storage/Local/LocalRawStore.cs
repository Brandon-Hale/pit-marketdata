using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MarketData.Storage.Local;

/// <summary>Filesystem-backed <see cref="IRawStore"/>, used for local runs and tests.</summary>
public sealed class LocalRawStore(string rootDirectory) : IRawStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<string> WriteAsync(RawEnvelope envelope, DateOnly runDate, CancellationToken ct)
    {
        var key = RawKey.For(envelope.SourceId, envelope.Dataset, runDate, envelope.Symbol);
        var path = ToPath(key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(envelope, Json), ct);

        return key;
    }

    public async Task<RawEnvelope> ReadAsync(string key, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(ToPath(key), ct);

        return JsonSerializer.Deserialize<RawEnvelope>(text, Json)
               ?? throw new InvalidDataException($"Raw object '{key}' deserialized to null.");
    }

    public async IAsyncEnumerable<string> ListAsync(
        string keyPrefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var root = Path.Combine(rootDirectory, "raw");
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var key = Path.GetRelativePath(rootDirectory, path).Replace('\\', '/');
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                yield return key;
            }
        }

        await Task.CompletedTask;
    }

    private string ToPath(string key) =>
        Path.Combine(rootDirectory, key.Replace('/', Path.DirectorySeparatorChar));
}
