using DuckDB.NET.Data;

namespace MarketData.Query;

/// <summary>
/// Where curated Parquet lives, and whatever DuckDB needs in order to read it. The two
/// forms must return identical rows for identical data; an integration test asserts it.
/// </summary>
public sealed class CuratedSource
{
    private readonly string _prefix;
    private readonly bool _isS3;
    private readonly string? _endpoint;

    private CuratedSource(string prefix, bool isS3, string? endpoint = null)
    {
        _prefix = prefix;
        _isS3 = isS3;
        _endpoint = endpoint;
    }

    /// <summary>A directory on disk. Used by tests and offline work.</summary>
    public static CuratedSource Local(string root) =>
        new(root.Replace('\\', '/').TrimEnd('/'), isS3: false);

    /// <summary>
    /// An S3 bucket, read in place through DuckDB's httpfs extension.
    /// </summary>
    /// <param name="endpoint">
    /// Intended to point httpfs at LocalStack, but DuckDB ignores it and reaches real AWS
    /// regardless — a LocalStack run fails with AWS's own <c>InvalidAccessKeyId: "test"</c>.
    /// Kept because the emitted secret is still correct if a future DuckDB honours it, but
    /// do not rely on it: read-path equivalence is checked against a scratch bucket in real
    /// S3 instead. Production leaves this null.
    /// </param>
    public static CuratedSource S3(string bucket, string? endpoint = null) =>
        new($"s3://{bucket}", isS3: true, endpoint);

    /// <summary>Glob matching every Parquet part of one dataset.</summary>
    public string Glob(string dataset) =>
        $"{_prefix}/curated/dataset={dataset}/**/*.parquet";

    /// <summary>Loads and configures whatever the connection needs before querying.</summary>
    public void Setup(DuckDBConnection connection)
    {
        if (!_isS3)
        {
            return;
        }

        using var command = connection.CreateCommand();

        // INSTALL reaches extensions.duckdb.org on first use and caches under ~/.duckdb.
        // Acceptable locally and in CI; a Lambda must ship the extension instead. See the
        // risks section of the Stage 4 design.
        // SCOPE binds the secret to this prefix explicitly; without it DuckDB can fall
        // back to no credentials and the read fails 403. REGION is stated rather than
        // inferred because a custom endpoint has no region to infer from.
        command.CommandText = _endpoint is null
            ? "INSTALL httpfs; LOAD httpfs; " +
              $"CREATE OR REPLACE SECRET s3 (TYPE s3, PROVIDER credential_chain, " +
              $"SCOPE '{_prefix}');"
            : "INSTALL httpfs; LOAD httpfs; " +
              $"CREATE OR REPLACE SECRET s3 (TYPE s3, KEY_ID 'test', SECRET 'test', " +
              $"REGION 'us-east-1', ENDPOINT '{_endpoint}', URL_STYLE 'path', " +
              $"USE_SSL false, SCOPE '{_prefix}');";

        command.ExecuteNonQuery();
    }
}
