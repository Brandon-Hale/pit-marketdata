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

    private CuratedSource(string prefix, bool isS3)
    {
        _prefix = prefix;
        _isS3 = isS3;
    }

    /// <summary>A directory on disk. Used by tests and offline work.</summary>
    public static CuratedSource Local(string root) =>
        new(root.Replace('\\', '/').TrimEnd('/'), isS3: false);

    /// <summary>
    /// An S3 bucket, read in place through DuckDB's httpfs extension using the ambient
    /// AWS credential chain.
    /// </summary>
    /// <remarks>
    /// There is no endpoint override. DuckDB's httpfs ignores <c>ENDPOINT</c> on an S3
    /// secret and contacts real AWS regardless, so pointing it at LocalStack is not
    /// possible — an attempt fails with AWS's own <c>InvalidAccessKeyId</c>. Read-path
    /// equivalence is checked against a scratch bucket in real S3 instead; see
    /// <c>S3ReadPathTests</c>.
    /// </remarks>
    public static CuratedSource S3(string bucket) => new($"s3://{bucket}", isS3: true);

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
        command.CommandText =
            "INSTALL httpfs; LOAD httpfs; " +
            $"CREATE OR REPLACE SECRET s3 (TYPE s3, PROVIDER credential_chain, SCOPE '{_prefix}');";

        command.ExecuteNonQuery();
    }
}
