using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Testcontainers.LocalStack;

namespace MarketData.Integration.Tests;

/// <summary>Starts LocalStack once per test collection and exposes a configured S3 client.</summary>
public sealed class LocalStackFixture : IAsyncLifetime
{
    private LocalStackContainer? _container;

    public IAmazonS3 S3 { get; private set; } = null!;

    public IAmazonDynamoDB Dynamo { get; private set; } = null!;

    public string Bucket => "pit-marketdata-test";

    public string TableName => "pit-marketdata-test";

    /// <summary>
    /// False when no Docker daemon could be reached. Tests skip rather than fail, so the
    /// suite stays green on machines without Docker while still running for real wherever
    /// a daemon exists. The container is built inside the guard because
    /// <c>LocalStackBuilder.Build()</c> probes Docker eagerly and throws from the
    /// constructor otherwise.
    /// </summary>
    public bool Available { get; private set; }

    public string SkipReason { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new LocalStackBuilder("localstack/localstack:3").Build();
            await _container.StartAsync();

            S3 = new AmazonS3Client(
                "test",
                "test",
                new AmazonS3Config
                {
                    ServiceURL = _container.GetConnectionString(),
                    ForcePathStyle = true,
                    AuthenticationRegion = "ap-southeast-2"
                });

            await S3.PutBucketAsync(Bucket);

            Dynamo = new AmazonDynamoDBClient(
                "test",
                "test",
                new AmazonDynamoDBConfig
                {
                    ServiceURL = _container.GetConnectionString(),
                    AuthenticationRegion = "ap-southeast-2"
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
        catch (Exception ex)
        {
            SkipReason = $"LocalStack unavailable, Docker is probably not running: {ex.Message}";
        }
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
