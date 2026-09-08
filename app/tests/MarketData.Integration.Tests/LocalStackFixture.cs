using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Testcontainers.LocalStack;

namespace MarketData.Integration.Tests;

/// <summary>Starts LocalStack once per test collection and exposes configured AWS clients.</summary>
public sealed class LocalStackFixture : IAsyncLifetime
{
    // LocalStack ignores regions, and us-east-1 is the one region S3 accepts a
    // CreateBucket for without an explicit LocationConstraint. Using the real
    // project region here buys nothing and fails the create.
    private const string TestRegion = "us-east-1";

    private LocalStackContainer? _container;

    public IAmazonS3 S3 { get; private set; } = null!;

    public IAmazonDynamoDB Dynamo { get; private set; } = null!;

    public string Bucket => "pit-marketdata-test";

    public string TableName => "pit-marketdata-test";

    /// <summary>
    /// Host:port of the LocalStack S3 endpoint, without a scheme, in the form DuckDB's
    /// httpfs secret expects.
    /// </summary>
    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>
    /// False only when no Docker daemon could be reached, so tests skip instead of
    /// failing on machines without Docker. Everything after the container starts is
    /// deliberately outside the catch: a broken bucket or table setup is a real bug
    /// and must fail loudly rather than masquerade as a missing daemon.
    /// </summary>
    public bool Available { get; private set; }

    public string SkipReason { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            // Build() probes Docker eagerly and throws, so it lives inside the guard.
            _container = new LocalStackBuilder("localstack/localstack:3").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            SkipReason = $"Docker is unavailable, so LocalStack could not start: {ex.Message}";
            return;
        }

        S3 = new AmazonS3Client(
            "test",
            "test",
            new AmazonS3Config
            {
                ServiceURL = _container.GetConnectionString(),
                ForcePathStyle = true,
                AuthenticationRegion = TestRegion
            });

        await S3.PutBucketAsync(Bucket);

        Dynamo = new AmazonDynamoDBClient(
            "test",
            "test",
            new AmazonDynamoDBConfig
            {
                ServiceURL = _container.GetConnectionString(),
                AuthenticationRegion = TestRegion
            });

        await Dynamo.CreateTableAsync(new CreateTableRequest
        {
            TableName = TableName,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema =
            [
                new KeySchemaElement("pk", KeyType.HASH),
                new KeySchemaElement("sk", KeyType.RANGE)
            ],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S)
            ]
        });

        Available = true;
    }

    public async ValueTask DisposeAsync()
    {
        S3?.Dispose();
        Dynamo?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(nameof(LocalStackCollection))]
public sealed class LocalStackCollection : ICollectionFixture<LocalStackFixture>;
