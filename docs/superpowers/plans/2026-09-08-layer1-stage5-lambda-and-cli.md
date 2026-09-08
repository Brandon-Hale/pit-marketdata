# Layer 1 Stage 5 Implementation Plan — The scheduled Lambda and the CLI

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the libraries into something that runs — a Lambda that keeps the warehouse current on a schedule, and a CLI that lets a human add symbols, backfill history, and ask the as-of question.

**Architecture:** Two entry points over one shared composition project. The CLI carries DuckDB and the full query layer; the Lambda carries neither, answering "did I already know this close?" from a rolling ten-day map on the DynamoDB cursor instead. `IngestService` takes prior knowledge as a delegate and cannot tell the difference.

**Tech Stack:** C# / .NET 10, System.CommandLine 2.0.11, Amazon.Lambda.RuntimeSupport 2.2.0, Microsoft.Extensions.Hosting, AWS SDK v4, Terraform 1.14.

**Spec:** `docs/superpowers/specs/2026-09-08-layer1-stage5-lambda-and-cli-design.md`

**Scope:** Stage 5 only. `reprocess`, `compact`, run records mirrored to S3, and GitHub OIDC are Stage 6 or hardening.

## Global Constraints

Carried over from Stages 0–4. Breaking one is silent.

- **Target framework `net10.0`.** Nullable enabled, warnings are errors.
- **`MarketData.Domain` has zero package references.** An architecture test enforces it.
- **`MarketData.Lambda` must have no DuckDB reference,** direct or transitive. A new architecture test enforces it; the whole point of the cursor design is that the package stays small.
- **Central package management.** Versions in `app/Directory.Packages.props`. After any `dotnet add package`, strip the `Version=` attribute.
- **No `DateTime.UtcNow` / `DateTimeOffset.UtcNow`** outside a composition root. The Lambda handler and the CLI entry point *are* composition roots and may create `TimeProvider.System`; nothing else may.
- **Prices are `decimal`, never `double`.**
- **Append-only.** No code path updates or deletes a curated row, and the Lambda's IAM policy grants no `s3:DeleteObject`.
- **`asOf` is never optional.** `query` without `--as-of` is a usage error, never a default to now.
- **Tests run on Microsoft.Testing.Platform.** Do not add `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio`.
- **Work from `app/`** unless a step says otherwise.

## File Structure

```
app/src/MarketData.Hosting/          NEW
  MarketDataOptions.cs               bucket, table, region, ssm parameter name
  ServiceCollectionExtensions.cs     AddMarketData — everything both entry points need

app/src/MarketData.Lambda/           NEW  (no DuckDB)
  Function.cs                        the handler
  IngestRunner.cs                    watchlist loop, per-symbol error isolation

app/src/MarketData.Cli/              NEW  (with DuckDB)
  Program.cs                         System.CommandLine wiring
  Commands/WatchlistCommand.cs
  Commands/BackfillCommand.cs
  Commands/QueryCommand.cs

app/src/MarketData.Storage/
  Dynamo/Cursor.cs                   MOD  + RecentCloses
  Dynamo/CursorRepository.cs         MOD  map the new attribute, trim to 10
  Dynamo/RunRecord.cs                NEW
  Dynamo/RunRecordRepository.cs      NEW

app/src/MarketData.Sources/
  IngestService.cs                   MOD  populate RecentCloses on save
  CursorPriorKnowledge.cs            NEW  the Lambda's delegate

app/tests/MarketData.Lambda.Tests/   NEW
app/tests/MarketData.Cli.Tests/      NEW

scripts/build-lambda.sh              NEW
infra/terraform/modules/ingest/      NEW  function, role, schedule, log group
```

---

# Stage A — Prior knowledge without DuckDB

### Task 1: The cursor carries recent closes

**Files:**
- Modify: `app/src/MarketData.Storage/Dynamo/Cursor.cs`, `app/src/MarketData.Storage/Dynamo/CursorRepository.cs`
- Test: `app/tests/MarketData.Integration.Tests/CursorRepositoryTests.cs` (new file)

**Interfaces:**
- Consumes: `Cursor` as it exists today, with four positional parameters.
- Produces: `Cursor` with a fifth, `IReadOnlyDictionary<DateOnly, decimal>? RecentCloses = null`, round-tripping through DynamoDB and trimmed to the ten newest dates on save. Tasks 2 and 3 read it.

Ten is enough for a scheduled run, which fetches one or two trading days from the cursor forward. It is deliberately not enough for a deep restatement — see §9 of the spec.

- [ ] **Step 1: Write the failing test**

```csharp
using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class CursorRepositoryTests(LocalStackFixture fixture)
{
    private CursorRepository Repo() => new(fixture.Dynamo, fixture.TableName);

    [Fact]
    public async Task Round_trips_recent_closes()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var closes = new Dictionary<DateOnly, decimal>
        {
            [new DateOnly(2026, 9, 1)] = 231.40m,
            [new DateOnly(2026, 9, 2)] = 229.85m
        };

        await Repo().SaveAsync(
            new Cursor("prices_daily", "RC1", new DateOnly(2026, 9, 2), "sha256:x", closes),
            TestContext.Current.CancellationToken);

        var read = await Repo().GetAsync("prices_daily", "RC1", TestContext.Current.CancellationToken);

        read!.RecentCloses.ShouldNotBeNull();
        read.RecentCloses![new DateOnly(2026, 9, 1)].ShouldBe(231.40m);
        read.RecentCloses[new DateOnly(2026, 9, 2)].ShouldBe(229.85m);
    }

    [Fact]
    public async Task Keeps_only_the_ten_newest_closes()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var closes = Enumerable.Range(1, 25)
            .ToDictionary(d => new DateOnly(2026, 1, d), d => 100m + d);

        await Repo().SaveAsync(
            new Cursor("prices_daily", "RC2", new DateOnly(2026, 1, 25), "sha256:x", closes),
            TestContext.Current.CancellationToken);

        var read = await Repo().GetAsync("prices_daily", "RC2", TestContext.Current.CancellationToken);

        read!.RecentCloses!.Count.ShouldBe(10);
        read.RecentCloses.ShouldContainKey(new DateOnly(2026, 1, 25));
        read.RecentCloses.ShouldNotContainKey(new DateOnly(2026, 1, 15));
    }

    [Fact]
    public async Task A_cursor_without_closes_reads_back_empty_not_null_bearing()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await Repo().SaveAsync(
            new Cursor("prices_daily", "RC3", new DateOnly(2026, 1, 1), "sha256:x"),
            TestContext.Current.CancellationToken);

        var read = await Repo().GetAsync("prices_daily", "RC3", TestContext.Current.CancellationToken);

        (read!.RecentCloses is null || read.RecentCloses.Count == 0).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Integration.Tests --filter CursorRepositoryTests`
Expected: FAIL to compile — `Cursor` has four parameters. Without Docker the tests skip instead; in that case verify the compile error with `dotnet build`.

- [ ] **Step 3: Add the property**

```csharp
namespace MarketData.Storage.Dynamo;

/// <summary>Watermark for one (dataset, symbol) fetch stream.</summary>
/// <param name="RecentCloses">
/// The newest closes by effective date, so a scheduled run can answer "did I already know
/// this?" without a query layer. Trimmed to the ten newest on save: a daily run only ever
/// looks a day or two back, and an unbounded map would grow without limit.
/// </param>
public sealed record Cursor(
    string Dataset,
    string Symbol,
    DateOnly? LastEffectiveDate,
    string? LastContentHash,
    IReadOnlyDictionary<DateOnly, decimal>? RecentCloses = null);
```

- [ ] **Step 4: Map it in `CursorRepository`**

Add the constant and the two mappings. In `SaveAsync`, before the `PutItemAsync`:

```csharp
        if (cursor.RecentCloses is { Count: > 0 })
        {
            item["recent_closes"] = new AttributeValue
            {
                M = cursor.RecentCloses
                    .OrderByDescending(kv => kv.Key)
                    .Take(MaxRecentCloses)
                    .ToDictionary(
                        kv => kv.Key.ToString("yyyy-MM-dd"),
                        kv => new AttributeValue { N = kv.Value.ToString(CultureInfo.InvariantCulture) })
            };
        }
```

In `GetAsync`, after reading `hash`:

```csharp
        var recent = response.Item.TryGetValue("recent_closes", out var m) && m.M is { Count: > 0 }
            ? m.M.ToDictionary(
                kv => DateOnly.ParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                kv => decimal.Parse(kv.Value.N, CultureInfo.InvariantCulture))
            : null;

        return new Cursor(dataset, symbol, last, hash, recent);
```

and at the top of the class:

```csharp
    /// <summary>A daily run never looks further back than a few trading days.</summary>
    private const int MaxRecentCloses = 10;
```

Add `using System.Globalization;` to the file. DynamoDB numbers are stored as strings, so
`InvariantCulture` is required in both directions — a machine with a comma decimal separator
would otherwise write `231,40` and fail to read it back.

- [ ] **Step 5: Run the tests**

Run: `dotnet test` — with Docker, or rely on CI.
Expected: PASS, or SKIP where Docker is unavailable.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Carry recent closes on the cursor

A scheduled run needs to know whether it already had a close, which
until now meant a query layer and a 60MB native library in the Lambda.
Ten days covers what a daily run touches; anything deeper is Stage 6's
reprocess."
```

---

### Task 2: `IngestService` populates the recent closes

**Files:**
- Modify: `app/src/MarketData.Sources/IngestService.cs`
- Test: `app/tests/MarketData.Sources.Tests/IngestServiceTests.cs`

**Interfaces:**
- Consumes: `Cursor.RecentCloses` from Task 1.
- Produces: `IngestService` writing the newest closes onto the cursor it saves. Task 3 reads them back.

- [ ] **Step 1: Write the failing test**

Append to `IngestServiceTests.cs`:

```csharp
    [Fact]
    public async Task Saves_the_closes_it_wrote_onto_the_cursor()
    {
        var cursors = new InMemoryCursorRepository();
        var handler = new RoutingStubHandler(new Dictionary<string, string>
        {
            ["time_series"] = AdjustedPricesBody,
            ["splits"] = SplitsBody
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };

        await new IngestService(
            new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now)),
            new TwelveDataPriceNormaliser(),
            new TwelveDataActionsNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            new CapturingCuratedStore(),
            cursors).IngestSymbolAsync(
                "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1",
                TestContext.Current.CancellationToken);

        var cursor = await cursors.GetAsync("prices_daily", "AAPL", TestContext.Current.CancellationToken);

        // The unadjusted close, so a later comparison is like for like.
        cursor!.RecentCloses.ShouldNotBeNull();
        cursor.RecentCloses![new DateOnly(2020, 6, 15)].ShouldBe(342.99m);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Sources.Tests --filter IngestServiceTests`
Expected: FAIL — `RecentCloses` is null.

- [ ] **Step 3: Populate it**

In `IngestService.IngestSymbolAsync`, replace the `cursors.SaveAsync` call:

```csharp
        // Carry forward what was already known, then overlay this run's bars. The
        // repository trims to the newest ten on save. Unadjusted closes, matching what
        // a later comparison will be given.
        var recentCloses = new Dictionary<DateOnly, decimal>();

        foreach (var kv in cursor?.RecentCloses ?? new Dictionary<DateOnly, decimal>())
        {
            recentCloses[kv.Key] = kv.Value;
        }

        foreach (var bar in bars)
        {
            recentCloses[bar.EffectiveDate] = bar.Close;
        }

        await cursors.SaveAsync(
            new Cursor(
                Dataset,
                symbol,
                parsed.Count > 0 ? parsed[^1].EffectiveDate : cursor?.LastEffectiveDate,
                envelope.ContentHash,
                recentCloses),
            ct);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MarketData.Sources.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Record the closes each run wrote onto its cursor"
```

---

### Task 3: The cursor-backed prior knowledge delegate

**Files:**
- Create: `app/src/MarketData.Sources/CursorPriorKnowledge.cs`
- Test: `app/tests/MarketData.Sources.Tests/CursorPriorKnowledgeTests.cs`

**Interfaces:**
- Consumes: `ICursorRepository` and `Cursor.RecentCloses`.
- Produces: `CursorPriorKnowledge.From(ICursorRepository, string dataset) -> Func<string, DateOnly, CancellationToken, Task<decimal?>>`, the same shape `PriorKnowledge.From` produces in `MarketData.Query`. Task 6 wires it into the Lambda.

- [ ] **Step 1: Write the failing test**

```csharp
using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class CursorPriorKnowledgeTests
{
    private static async Task<ICursorRepository> SeededAsync(
        IReadOnlyDictionary<DateOnly, decimal>? closes)
    {
        var repo = new InMemoryCursorRepository();
        await repo.SaveAsync(
            new Cursor("prices_daily", "AAPL", new DateOnly(2026, 9, 5), "sha256:x", closes),
            CancellationToken.None);

        return repo;
    }

    [Fact]
    public async Task Returns_a_close_that_is_in_the_map()
    {
        var repo = await SeededAsync(new Dictionary<DateOnly, decimal>
        {
            [new DateOnly(2026, 9, 4)] = 231.40m
        });

        var lookup = CursorPriorKnowledge.From(repo, "prices_daily");

        (await lookup("AAPL", new DateOnly(2026, 9, 4), TestContext.Current.CancellationToken))
            .ShouldBe(231.40m);
    }

    [Fact]
    public async Task Returns_null_for_a_date_outside_the_map()
    {
        var repo = await SeededAsync(new Dictionary<DateOnly, decimal>
        {
            [new DateOnly(2026, 9, 4)] = 231.40m
        });

        var lookup = CursorPriorKnowledge.From(repo, "prices_daily");

        (await lookup("AAPL", new DateOnly(2020, 1, 2), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_cursor_at_all()
    {
        var lookup = CursorPriorKnowledge.From(new InMemoryCursorRepository(), "prices_daily");

        (await lookup("NOPE", new DateOnly(2026, 9, 4), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Reads_the_cursor_once_per_symbol_not_once_per_date()
    {
        // A backfill asks for thousands of dates; one DynamoDB read each would be absurd.
        var repo = new CountingCursorRepository();
        await repo.SaveAsync(
            new Cursor("prices_daily", "AAPL", new DateOnly(2026, 9, 5), "sha256:x",
                new Dictionary<DateOnly, decimal> { [new DateOnly(2026, 9, 4)] = 231.40m }),
            CancellationToken.None);

        var lookup = CursorPriorKnowledge.From(repo, "prices_daily");

        for (var i = 0; i < 5; i++)
        {
            await lookup("AAPL", new DateOnly(2026, 9, 4), TestContext.Current.CancellationToken);
        }

        repo.Gets.ShouldBe(1);
    }
}

/// <summary>Counts reads, so caching can be asserted rather than assumed.</summary>
public sealed class CountingCursorRepository : ICursorRepository
{
    private readonly InMemoryCursorRepository _inner = new();

    public int Gets { get; private set; }

    public Task<Cursor?> GetAsync(string dataset, string symbol, CancellationToken ct)
    {
        Gets++;
        return _inner.GetAsync(dataset, symbol, ct);
    }

    public Task SaveAsync(Cursor cursor, CancellationToken ct) => _inner.SaveAsync(cursor, ct);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Sources.Tests --filter CursorPriorKnowledge`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Write `CursorPriorKnowledge.cs`**

```csharp
using MarketData.Storage.Dynamo;

namespace MarketData.Sources;

/// <summary>
/// Prior knowledge from the cursor's recent-closes map, for callers that cannot carry a
/// query layer. Same delegate shape as <c>PriorKnowledge.From</c> in MarketData.Query, so
/// <see cref="IngestService"/> cannot tell which it was given.
/// </summary>
/// <remarks>
/// Bounded by what the cursor holds — about ten trading days. A restatement of an older bar
/// is not detected here and is left to Stage 6's reprocess, which runs with the full query
/// layer. Use <c>PriorKnowledge.From</c> wherever DuckDB is available.
/// </remarks>
public static class CursorPriorKnowledge
{
    public static Func<string, DateOnly, CancellationToken, Task<decimal?>> From(
        ICursorRepository cursors,
        string dataset)
    {
        // One read per symbol, held for the life of the delegate: a backfill asks about
        // thousands of dates and the answer for a symbol does not change mid-run.
        var cache = new Dictionary<string, IReadOnlyDictionary<DateOnly, decimal>?>();

        return async (symbol, date, ct) =>
        {
            if (!cache.TryGetValue(symbol, out var closes))
            {
                var cursor = await cursors.GetAsync(dataset, symbol, ct);
                closes = cursor?.RecentCloses;
                cache[symbol] = closes;
            }

            return closes is not null && closes.TryGetValue(date, out var close)
                ? close
                : null;
        };
    }
}
```

The delegate is created per ingest run, so the cache never outlives one invocation.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MarketData.Sources.Tests`
Expected: PASS — 4 new tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add cursor-backed prior knowledge for callers without DuckDB

Same delegate shape as the query-layer version, so IngestService cannot
tell which it was given. One cursor read per symbol, not per date."
```

---

# Stage B — Shared composition

### Task 4: `MarketData.Hosting`

**Files:**
- Create: `app/src/MarketData.Hosting/MarketData.Hosting.csproj`, `MarketDataOptions.cs`, `ServiceCollectionExtensions.cs`
- Create: `app/tests/MarketData.Hosting.Tests/MarketData.Hosting.Tests.csproj`
- Test: `app/tests/MarketData.Hosting.Tests/AddMarketDataTests.cs`

**Interfaces:**
- Consumes: everything in Storage and Sources.
- Produces: `MarketDataOptions` (record: `DataBucket`, `TableName`, `Region`, `ApiKeyParameterName`) and `IServiceCollection.AddMarketData(MarketDataOptions)`, registering `IRawStore`, `ICuratedStore`, `IWatchlistRepository`, `ICursorRepository`, `IInstrumentRepository`, `IPriceSource`, `IPriceNormaliser`, `TwelveDataActionsNormaliser`, `ObservationPolicy` and `IngestService`. Tasks 6 and 8 both call it.

Both entry points must resolve the same bucket and table. A divergence there would look like missing data rather than a bug, which is why this is one method rather than two wirings.

- [ ] **Step 1: Create the projects**

```bash
cd app
dotnet new classlib -o src/MarketData.Hosting -f net10.0
dotnet new xunit3 -o tests/MarketData.Hosting.Tests -f net10.0
rm -f src/MarketData.Hosting/Class1.cs tests/MarketData.Hosting.Tests/UnitTest1.cs
dotnet sln add src/MarketData.Hosting tests/MarketData.Hosting.Tests
git -C .. checkout global.json
```

Add to `Directory.Packages.props` under the `Sources` group:

```xml
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
```

If that version does not resolve, run
`curl -s https://api.nuget.org/v3-flatcontainer/microsoft.extensions.dependencyinjection.abstractions/index.json`
and pin the highest stable release.

`src/MarketData.Hosting/MarketData.Hosting.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="AWSSDK.SimpleSystemsManagement" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\MarketData.Sources\MarketData.Sources.csproj" />
    <ProjectReference Include="..\MarketData.Storage\MarketData.Storage.csproj" />
  </ItemGroup>

</Project>
```

`tests/MarketData.Hosting.Tests/MarketData.Hosting.Tests.csproj` mirrors the other test projects: `OutputType` `Exe`, `IsPackable` false, `xunit.v3` and `Shouldly` package references, the `xunit.runner.json` content item, the `Xunit` using, plus a project reference to `MarketData.Hosting`.

- [ ] **Step 2: Write `MarketDataOptions.cs`**

```csharp
namespace MarketData.Hosting;

/// <summary>
/// Everything both entry points need in order to reach the same data. Resolved from
/// environment variables, so a local run and a deployed run differ only in values.
/// </summary>
public sealed record MarketDataOptions(
    string DataBucket,
    string TableName,
    string Region,
    string ApiKeyParameterName = "/pit-marketdata/twelvedata/apikey")
{
    public const string DataBucketVariable = "MARKETDATA_DATA_BUCKET";
    public const string TableNameVariable = "MARKETDATA_TABLE_NAME";
    public const string RegionVariable = "MARKETDATA_REGION";

    /// <summary>Reads the options from the environment, failing loudly if any is missing.</summary>
    public static MarketDataOptions FromEnvironment() => new(
        Required(DataBucketVariable),
        Required(TableNameVariable),
        Environment.GetEnvironmentVariable(RegionVariable) ?? "ap-southeast-2");

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Environment variable '{name}' is not set. Both the Lambda and the CLI need " +
                "it to reach the right bucket and table; guessing a default would silently " +
                "read or write the wrong store.");
}
```

- [ ] **Step 3: Write the failing test**

```csharp
using MarketData.Hosting;
using MarketData.Sources;
using MarketData.Storage;
using MarketData.Storage.Dynamo;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace MarketData.Hosting.Tests;

public sealed class AddMarketDataTests
{
    private static readonly MarketDataOptions Options =
        new("test-bucket", "test-table", "ap-southeast-2");

    private static ServiceProvider Build() =>
        new ServiceCollection().AddMarketData(Options).BuildServiceProvider();

    [Theory]
    [InlineData(typeof(IRawStore))]
    [InlineData(typeof(ICuratedStore))]
    [InlineData(typeof(ICursorRepository))]
    [InlineData(typeof(IWatchlistRepository))]
    [InlineData(typeof(IInstrumentRepository))]
    [InlineData(typeof(IPriceSource))]
    [InlineData(typeof(IPriceNormaliser))]
    [InlineData(typeof(IngestService))]
    public void Resolves_everything_both_entry_points_need(Type service)
    {
        using var provider = Build();

        provider.GetService(service).ShouldNotBeNull();
    }

    [Fact]
    public void Missing_environment_variables_fail_loudly()
    {
        Environment.SetEnvironmentVariable(MarketDataOptions.DataBucketVariable, null);

        Should.Throw<InvalidOperationException>(MarketDataOptions.FromEnvironment)
            .Message.ShouldContain(MarketDataOptions.DataBucketVariable);
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test tests/MarketData.Hosting.Tests`
Expected: FAIL — `AddMarketData` does not exist.

- [ ] **Step 5: Write `ServiceCollectionExtensions.cs`**

```csharp
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using MarketData.Domain;
using MarketData.Sources;
using MarketData.Sources.TwelveData;
using MarketData.Storage;
using MarketData.Storage.Dynamo;
using MarketData.Storage.S3;
using Microsoft.Extensions.DependencyInjection;

namespace MarketData.Hosting;

/// <summary>
/// The wiring both entry points share. Anything specific to one of them — the query layer
/// for the CLI, the cursor-backed prior knowledge for the Lambda — is registered by that
/// entry point, not here.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMarketData(
        this IServiceCollection services, MarketDataOptions options)
    {
        var region = RegionEndpoint.GetBySystemName(options.Region);

        services.AddSingleton(options);
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(region));
        services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient(region));

        services.AddSingleton<IRawStore>(sp =>
            new S3RawStore(sp.GetRequiredService<IAmazonS3>(), options.DataBucket));
        services.AddSingleton<ICuratedStore>(sp =>
            new S3CuratedStore(sp.GetRequiredService<IAmazonS3>(), options.DataBucket));

        services.AddSingleton<ICursorRepository>(sp =>
            new CursorRepository(sp.GetRequiredService<IAmazonDynamoDB>(), options.TableName));
        services.AddSingleton<IWatchlistRepository>(sp =>
            new WatchlistRepository(sp.GetRequiredService<IAmazonDynamoDB>(), options.TableName));
        services.AddSingleton<IInstrumentRepository>(sp =>
            new InstrumentRepository(sp.GetRequiredService<IAmazonDynamoDB>(), options.TableName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPublicationClock, UsEquityPublicationClock>();
        services.AddSingleton(sp => new ObservationPolicy(
            sp.GetRequiredService<IPublicationClock>(), TimeSpan.FromHours(24)));

        services.AddSingleton<IPriceNormaliser, TwelveDataPriceNormaliser>();
        services.AddSingleton<TwelveDataActionsNormaliser>();

        // Resilience covers the vendor's rate limit and transient 5xx. The key is read from
        // SSM rather than an environment variable so it never appears in the function
        // configuration, where the console and GetFunctionConfiguration would show it.
        services.AddHttpClient<IPriceSource, TwelveDataPriceSource>((sp, client) =>
            {
                client.BaseAddress = new Uri("https://api.twelvedata.com/");
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddStandardResilienceHandler();

        services.AddSingleton(sp => new TwelveDataPriceSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IPriceSource)),
            ReadApiKey(options),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IngestService>();

        return services;
    }

    private static string ReadApiKey(MarketDataOptions options)
    {
        using var ssm = new AmazonSimpleSystemsManagementClient(
            RegionEndpoint.GetBySystemName(options.Region));

        var response = ssm.GetParameterAsync(new GetParameterRequest
        {
            Name = options.ApiKeyParameterName,
            WithDecryption = true
        }).GetAwaiter().GetResult();

        return response.Parameter.Value;
    }
}
```

`ReadApiKey` blocks, which is acceptable in a composition root running once per process. If
the resilience handler registration and the explicit `TwelveDataPriceSource` registration
conflict, prefer the explicit one and drop `AddHttpClient`'s typed registration, keeping a
named client — the goal is one construction path, not two.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/MarketData.Hosting.Tests`
Expected: PASS — 9 tests.

The tests construct AWS clients but make no calls, so no credentials are needed. If
`ReadApiKey` runs during resolution and fails without credentials, make `IPriceSource`
resolution lazy: register a `Func<string>` for the key and resolve it on first use.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add shared composition for both entry points

A Lambda writing to a different bucket than the CLI reads would look
like missing data rather than a bug, so the common wiring is one method."
```

---

# Stage C — The Lambda

### Task 5: Run records

**Files:**
- Create: `app/src/MarketData.Storage/Dynamo/RunRecord.cs`, `app/src/MarketData.Storage/Dynamo/RunRecordRepository.cs`
- Test: `app/tests/MarketData.Integration.Tests/RunRecordRepositoryTests.cs`

**Interfaces:**
- Consumes: the DynamoDB table.
- Produces: `RunRecord` and `IRunRecordRepository.SaveAsync(RunRecord, CancellationToken)`. Task 7 writes one per run.

`expires_at` uses the TTL attribute the table was provisioned with in Stage 1, so old records
expire with no cleanup job.

- [ ] **Step 1: Write the failing test**

```csharp
using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class RunRecordRepositoryTests(LocalStackFixture fixture)
{
    [Fact]
    public async Task Writes_a_run_record_with_a_ninety_day_ttl()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var startedAt = new DateTimeOffset(2026, 9, 8, 22, 15, 3, TimeSpan.Zero);
        var record = new RunRecord(
            RunId: "2026-09-08T22:15:03Z",
            StartedAt: startedAt,
            FinishedAt: startedAt.AddMinutes(2),
            SymbolsAttempted: 3,
            SymbolsSucceeded: 2,
            RowsWritten: 5,
            ActionsWritten: 1,
            SkippedUnchanged: 1,
            Errors: ["MSFT: vendor returned 429"]);

        var repo = new RunRecordRepository(fixture.Dynamo, fixture.TableName);
        await repo.SaveAsync(record, TestContext.Current.CancellationToken);

        var read = await repo.GetAsync(record.RunId, TestContext.Current.CancellationToken);

        read.ShouldNotBeNull();
        read!.SymbolsSucceeded.ShouldBe(2);
        read.Errors.ShouldContain("MSFT: vendor returned 429");
        read.ExpiresAt.ShouldBe(startedAt.AddDays(90).ToUnixTimeSeconds(), tolerance: 60);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build`
Expected: FAIL — `RunRecord` does not exist.

- [ ] **Step 3: Write `RunRecord.cs`**

```csharp
namespace MarketData.Storage.Dynamo;

/// <summary>
/// What one scheduled run did. The only visibility into a job nobody watches, and the
/// reason the table carries a TTL attribute.
/// </summary>
public sealed record RunRecord(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int SymbolsAttempted,
    int SymbolsSucceeded,
    int RowsWritten,
    int ActionsWritten,
    int SkippedUnchanged,
    IReadOnlyList<string> Errors)
{
    /// <summary>Unix seconds at which DynamoDB may expire this record.</summary>
    public long ExpiresAt => StartedAt.AddDays(90).ToUnixTimeSeconds();
}
```

- [ ] **Step 4: Write `RunRecordRepository.cs`**

```csharp
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
            DateTimeOffset.Parse(item["started_at"].S).ToUniversalTime(),
            DateTimeOffset.Parse(item["finished_at"].S).ToUniversalTime(),
            Int(item, "symbols_attempted"),
            Int(item, "symbols_succeeded"),
            Int(item, "rows_written"),
            Int(item, "actions_written"),
            Int(item, "skipped_unchanged"),
            item.TryGetValue("errors", out var e) ? e.SS : []);
    }

    private static AttributeValue Number(long value) =>
        new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static int Int(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var v) ? int.Parse(v.N, CultureInfo.InvariantCulture) : 0;

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

    private static Dictionary<string, AttributeValue> Key(string runId) => new()
    {
        ["pk"] = new($"RUN#{runId}"),
        ["sk"] = new("META")
    };
}
```

- [ ] **Step 5: Run and commit**

Run: `dotnet test` — with Docker, or rely on CI.
Expected: PASS or SKIP.

```bash
git add -A
git commit -m "Add run records with a 90 day TTL

A scheduled process with no record of what it did cannot be trusted,
and the table already carries the TTL attribute for exactly this."
```

---

### Task 6: The Lambda project and its no-DuckDB guard

**Files:**
- Create: `app/src/MarketData.Lambda/MarketData.Lambda.csproj`, `Function.cs`
- Create: `app/tests/MarketData.Lambda.Tests/MarketData.Lambda.Tests.csproj`
- Test: `app/tests/MarketData.Lambda.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `AddMarketData` from Task 4, `CursorPriorKnowledge` from Task 3.
- Produces: an executable Lambda project whose entry point builds the service provider and delegates to `IngestRunner` (Task 7).

The architecture test is the point of this task. §3 of the spec exists to keep DuckDB out of
this package; without a test, a transitive reference would undo it silently.

- [ ] **Step 1: Create the projects**

```bash
cd app
dotnet new classlib -o src/MarketData.Lambda -f net10.0
dotnet new xunit3 -o tests/MarketData.Lambda.Tests -f net10.0
rm -f src/MarketData.Lambda/Class1.cs tests/MarketData.Lambda.Tests/UnitTest1.cs
dotnet sln add src/MarketData.Lambda tests/MarketData.Lambda.Tests
git -C .. checkout global.json
```

Add to `Directory.Packages.props`:

```xml
    <PackageVersion Include="Amazon.Lambda.Core" Version="3.3.0" />
    <PackageVersion Include="Amazon.Lambda.RuntimeSupport" Version="2.2.0" />
    <PackageVersion Include="Amazon.Lambda.Serialization.SystemTextJson" Version="2.4.4" />
```

If `Amazon.Lambda.Serialization.SystemTextJson` 2.4.4 does not resolve, pin the highest stable
from `curl -s https://api.nuget.org/v3-flatcontainer/amazon.lambda.serialization.systemtextjson/index.json`.

`src/MarketData.Lambda/MarketData.Lambda.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>MarketData.Lambda</AssemblyName>
    <PublishReadyToRun>false</PublishReadyToRun>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Amazon.Lambda.Core" />
    <PackageReference Include="Amazon.Lambda.RuntimeSupport" />
    <PackageReference Include="Amazon.Lambda.Serialization.SystemTextJson" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\MarketData.Hosting\MarketData.Hosting.csproj" />
  </ItemGroup>

</Project>
```

`OutputType` is `Exe` because this uses the managed `dotnet10` runtime's **executable assembly**
handler model: `Amazon.Lambda.RuntimeSupport` runs the invocation loop from `Main`, and the
Terraform `handler` is then simply the assembly name. `AssemblyName` is stated explicitly, even
though it already matches the project name, because that Terraform string depends on it.

This is *not* the custom runtime. `provided.al2023` is a separate runtime, needed only for Native
AOT, and it alone requires the executable to be named `bootstrap`. `RuntimeSupport` in the csproj
is not evidence of one — the executable-assembly model needs it on the managed runtime too.

Add `Microsoft.Extensions.DependencyInjection` to `Directory.Packages.props` if it is not already
pinned, matching the Abstractions version.

- [ ] **Step 2: Write the architecture test**

```csharp
using System.Xml.Linq;

namespace MarketData.Lambda.Tests;

public sealed class ArchitectureTests
{
    private static DirectoryInfo SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "MarketData.slnx")) &&
               !File.Exists(Path.Combine(dir.FullName, "MarketData.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>
    /// The Lambda must not carry DuckDB, directly or transitively. Keeping it out is the
    /// entire reason the cursor holds recent closes; a transitive reference would add
    /// ~60MB and an httpfs download at cold start without anyone noticing.
    /// </summary>
    [Fact]
    public void Lambda_does_not_reference_duckdb_transitively()
    {
        var root = SolutionRoot();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offenders = new List<string>();

        Walk(Path.Combine(root.FullName, "src", "MarketData.Lambda", "MarketData.Lambda.csproj"));

        Assert.True(offenders.Count == 0,
            "DuckDB reached MarketData.Lambda via: " + string.Join(", ", offenders));

        void Walk(string csproj)
        {
            var full = Path.GetFullPath(csproj);
            if (!visited.Add(full) || !File.Exists(full))
            {
                return;
            }

            var document = XDocument.Load(full);
            var name = Path.GetFileNameWithoutExtension(full);

            if (document.Descendants("PackageReference")
                .Any(p => (p.Attribute("Include")?.Value ?? string.Empty)
                    .Contains("DuckDB", StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add(name);
            }

            var directory = Path.GetDirectoryName(full)!;
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (include is not null)
                {
                    Walk(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
                }
            }
        }
    }
}
```

- [ ] **Step 3: Run to verify it passes**

Run: `dotnet test tests/MarketData.Lambda.Tests`
Expected: PASS. To confirm the test can fail, temporarily add
`<PackageReference Include="DuckDB.NET.Data.Full" />` to `MarketData.Lambda.csproj`, re-run,
see it fail, then remove it. A guard that has never failed is not known to work.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Add the Lambda project with a no-DuckDB architecture test

Keeping DuckDB out is the whole reason the cursor carries recent
closes, so a transitive reference must fail the build rather than
quietly add 60MB and a cold-start download."
```

---

### Task 7: The ingest runner and the handler

**Files:**
- Create: `app/src/MarketData.Lambda/IngestRunner.cs`, `app/src/MarketData.Lambda/Function.cs`
- Test: `app/tests/MarketData.Lambda.Tests/IngestRunnerTests.cs`

**Interfaces:**
- Consumes: `IWatchlistRepository`, `IngestService`, `IRunRecordRepository`.
- Produces: `IngestRunner.RunAsync(string runId, CancellationToken) -> Task<RunRecord>`, and a `Function` entry point that builds the provider and calls it.

A failure on one symbol must not abort the rest. A single bad symbol taking down the whole
schedule is worse than a gap in one series.

- [ ] **Step 1: Write the failing test**

```csharp
using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Lambda.Tests;

public sealed class IngestRunnerTests
{
    [Fact]
    public async Task A_failing_symbol_does_not_stop_the_others()
    {
        var runner = new IngestRunner(
            watchlist: new StubWatchlist(["BAD", "GOOD"]),
            ingest: (symbol, ct) => symbol == "BAD"
                ? throw new HttpRequestException("vendor returned 429")
                : Task.FromResult(new IngestOutcome(RowsWritten: 3, ActionsWritten: 1, Skipped: false)),
            runRecords: new StubRunRecords(),
            timeProvider: TimeProvider.System);

        var record = await runner.RunAsync("run-1", TestContext.Current.CancellationToken);

        record.SymbolsAttempted.ShouldBe(2);
        record.SymbolsSucceeded.ShouldBe(1);
        record.RowsWritten.ShouldBe(3);
        record.Errors.ShouldHaveSingleItem().ShouldContain("BAD");
    }

    [Fact]
    public async Task Skipped_symbols_are_counted_separately_from_failures()
    {
        var runner = new IngestRunner(
            watchlist: new StubWatchlist(["AAPL"]),
            ingest: (_, _) => Task.FromResult(new IngestOutcome(0, 0, Skipped: true)),
            runRecords: new StubRunRecords(),
            timeProvider: TimeProvider.System);

        var record = await runner.RunAsync("run-1", TestContext.Current.CancellationToken);

        record.SkippedUnchanged.ShouldBe(1);
        record.SymbolsSucceeded.ShouldBe(1);
        record.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_run_record_is_saved()
    {
        var records = new StubRunRecords();
        var runner = new IngestRunner(
            new StubWatchlist(["AAPL"]),
            (_, _) => Task.FromResult(new IngestOutcome(1, 0, false)),
            records,
            TimeProvider.System);

        await runner.RunAsync("run-1", TestContext.Current.CancellationToken);

        records.Saved.ShouldHaveSingleItem().RunId.ShouldBe("run-1");
    }
}

file sealed class StubWatchlist(IReadOnlyList<string> symbols) : IWatchlistRepository
{
    public Task AddAsync(string symbol, DateTimeOffset addedAt, CancellationToken ct) =>
        Task.CompletedTask;

    public Task RemoveAsync(string symbol, DateTimeOffset removedAt, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> ActiveAsync(DateTimeOffset asOf, CancellationToken ct) =>
        Task.FromResult(symbols);
}

file sealed class StubRunRecords : IRunRecordRepository
{
    public List<RunRecord> Saved { get; } = [];

    public Task SaveAsync(RunRecord record, CancellationToken ct)
    {
        Saved.Add(record);
        return Task.CompletedTask;
    }

    public Task<RunRecord?> GetAsync(string runId, CancellationToken ct) =>
        Task.FromResult(Saved.FirstOrDefault(r => r.RunId == runId));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Lambda.Tests`
Expected: FAIL — `IngestRunner` does not exist.

- [ ] **Step 3: Write `IngestRunner.cs`**

```csharp
using MarketData.Storage.Dynamo;

namespace MarketData.Lambda;

/// <summary>What one symbol's ingest did, flattened so the runner needs no Sources reference.</summary>
public sealed record IngestOutcome(int RowsWritten, int ActionsWritten, bool Skipped);

/// <summary>
/// Walks the watchlist, ingests each symbol, and records the result. Symbols run
/// sequentially: at twenty symbols against an eight-per-minute vendor limit, concurrency
/// buys nothing and risks a 429 that would look like missing data.
/// </summary>
public sealed class IngestRunner(
    IWatchlistRepository watchlist,
    Func<string, CancellationToken, Task<IngestOutcome>> ingest,
    IRunRecordRepository runRecords,
    TimeProvider timeProvider)
{
    public async Task<RunRecord> RunAsync(string runId, CancellationToken ct)
    {
        var startedAt = timeProvider.GetUtcNow();
        var symbols = await watchlist.ActiveAsync(startedAt, ct);

        var succeeded = 0;
        var rows = 0;
        var actions = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var symbol in symbols)
        {
            try
            {
                var outcome = await ingest(symbol, ct);

                succeeded++;
                rows += outcome.RowsWritten;
                actions += outcome.ActionsWritten;

                if (outcome.Skipped)
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad symbol must not take down the schedule. The error is recorded
                // against the run and the remaining symbols still ingest.
                errors.Add($"{symbol}: {ex.Message}");
            }
        }

        var record = new RunRecord(
            runId, startedAt, timeProvider.GetUtcNow(),
            symbols.Count, succeeded, rows, actions, skipped, errors);

        await runRecords.SaveAsync(record, ct);

        return record;
    }
}
```

`OperationCanceledException` is deliberately not swallowed: a cancellation means the Lambda is
being shut down, and continuing would waste the remaining symbols' vendor credits.

- [ ] **Step 4: Write `Function.cs`**

```csharp
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using MarketData.Hosting;
using MarketData.Sources;
using MarketData.Storage.Dynamo;
using Microsoft.Extensions.DependencyInjection;

namespace MarketData.Lambda;

/// <summary>
/// The scheduled entry point. This is a composition root, so it is the one place allowed to
/// read the real clock and the environment.
/// </summary>
public static class Function
{
    public static async Task Main()
    {
        var options = MarketDataOptions.FromEnvironment();

        var services = new ServiceCollection()
            .AddMarketData(options)
            .AddSingleton<IRunRecordRepository>(sp => new RunRecordRepository(
                sp.GetRequiredService<Amazon.DynamoDBv2.IAmazonDynamoDB>(), options.TableName))
            .BuildServiceProvider();

        using var handlerWrapper = HandlerWrapper.GetHandlerWrapper(
            (input, context) => Handle(services, context),
            new DefaultLambdaJsonSerializer());

        using var bootstrap = new LambdaBootstrap(handlerWrapper);
        await bootstrap.RunAsync();
    }

    private static async Task<Stream> Handle(IServiceProvider services, ILambdaContext context)
    {
        var time = services.GetRequiredService<TimeProvider>();
        var now = time.GetUtcNow();
        var runId = now.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var ingestService = services.GetRequiredService<IngestService>();
        var cursors = services.GetRequiredService<ICursorRepository>();

        // No DuckDB here: prior knowledge comes from the cursor's recent closes.
        var priorKnowledge = CursorPriorKnowledge.From(cursors, "prices_daily");

        var runner = new IngestRunner(
            services.GetRequiredService<IWatchlistRepository>(),
            async (symbol, ct) =>
            {
                var cursor = await cursors.GetAsync("prices_daily", symbol, ct);
                var from = cursor?.LastEffectiveDate?.AddDays(1) ?? new DateOnly(2015, 1, 1);
                var to = DateOnly.FromDateTime(now.UtcDateTime);

                var result = await ingestService.IngestSymbolAsync(symbol, from, to, runId, ct);

                return new IngestOutcome(result.RowsWritten, result.ActionsWritten, result.SkippedUnchanged);
            },
            services.GetRequiredService<IRunRecordRepository>(),
            time);

        var record = await runner.RunAsync(runId, context.RemainingTime > TimeSpan.Zero
            ? new CancellationTokenSource(context.RemainingTime - TimeSpan.FromSeconds(10)).Token
            : CancellationToken.None);

        context.Logger.LogInformation(
            $"run {record.RunId}: {record.SymbolsSucceeded}/{record.SymbolsAttempted} symbols, " +
            $"{record.RowsWritten} rows, {record.Errors.Count} errors");

        return Stream.Null;
    }
}
```

`IngestService` is not constructed with the prior-knowledge delegate by `AddMarketData`, so
this handler must pass it. If `IngestService` is registered as a singleton without it, change
the registration in Task 4 to accept the delegate from DI and register the cursor-backed one
here — one construction path, chosen by the entry point.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MarketData.Lambda.Tests`
Expected: PASS — 4 tests including the architecture guard.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add the ingest runner and the Lambda handler

A failing symbol is recorded and the run continues: one bad symbol
taking down the schedule is worse than a gap in one series.
Cancellation is not swallowed, since it means shutdown."
```

---

# Stage D — The CLI

### Task 8: CLI skeleton and the watchlist commands

**Files:**
- Create: `app/src/MarketData.Cli/MarketData.Cli.csproj`, `Program.cs`, `Commands/WatchlistCommand.cs`
- Create: `app/tests/MarketData.Cli.Tests/MarketData.Cli.Tests.csproj`
- Test: `app/tests/MarketData.Cli.Tests/WatchlistCommandTests.cs`

**Interfaces:**
- Consumes: `AddMarketData` from Task 4, `IWatchlistRepository`.
- Produces: a runnable CLI with `watchlist add|remove|list`. Tasks 9 and 10 add `backfill` and `query`.

- [ ] **Step 1: Create the projects and pin the parser**

```bash
cd app
dotnet new console -o src/MarketData.Cli -f net10.0
dotnet new xunit3 -o tests/MarketData.Cli.Tests -f net10.0
rm -f tests/MarketData.Cli.Tests/UnitTest1.cs
dotnet sln add src/MarketData.Cli tests/MarketData.Cli.Tests
git -C .. checkout global.json
```

Add to `Directory.Packages.props`:

```xml
    <PackageVersion Include="System.CommandLine" Version="2.0.11" />
```

`src/MarketData.Cli/MarketData.Cli.csproj` references `System.CommandLine`,
`Microsoft.Extensions.DependencyInjection`, `MarketData.Hosting` and `MarketData.Query` — the
CLI is the entry point that *does* carry DuckDB.

- [ ] **Step 2: Write the failing test**

The commands are tested through a seam rather than by launching a process, so assertions are
on behaviour rather than on stdout scraping.

```csharp
using MarketData.Cli.Commands;
using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Cli.Tests;

public sealed class WatchlistCommandTests
{
    [Fact]
    public async Task Add_records_the_symbol_with_the_supplied_instant()
    {
        var repo = new RecordingWatchlist();
        var at = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

        await WatchlistCommand.AddAsync(repo, "aapl", at, TestContext.Current.CancellationToken);

        // Symbols are upper-cased: the store must not end up with both AAPL and aapl.
        repo.Added.ShouldHaveSingleItem().ShouldBe(("AAPL", at));
    }

    [Fact]
    public async Task List_passes_the_as_of_through()
    {
        var repo = new RecordingWatchlist();
        var asOf = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await WatchlistCommand.ListAsync(repo, asOf, TestContext.Current.CancellationToken);

        repo.AsOfQueried.ShouldHaveSingleItem().ShouldBe(asOf);
    }
}

file sealed class RecordingWatchlist : IWatchlistRepository
{
    public List<(string Symbol, DateTimeOffset At)> Added { get; } = [];

    public List<DateTimeOffset> AsOfQueried { get; } = [];

    public Task AddAsync(string symbol, DateTimeOffset addedAt, CancellationToken ct)
    {
        Added.Add((symbol, addedAt));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string symbol, DateTimeOffset removedAt, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> ActiveAsync(DateTimeOffset asOf, CancellationToken ct)
    {
        AsOfQueried.Add(asOf);
        return Task.FromResult<IReadOnlyList<string>>(["AAPL"]);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/MarketData.Cli.Tests`
Expected: FAIL — `WatchlistCommand` does not exist.

- [ ] **Step 4: Write `Commands/WatchlistCommand.cs`**

```csharp
using MarketData.Storage.Dynamo;

namespace MarketData.Cli.Commands;

/// <summary>
/// Watchlist membership. Deliberately bitemporal: <c>add</c> stamps when, and <c>list</c>
/// takes an as-of, so the tracked universe is itself point-in-time. Without that, every
/// historical query would know about symbols that were not being tracked at the time.
/// </summary>
public static class WatchlistCommand
{
    public static async Task AddAsync(
        IWatchlistRepository watchlist, string symbol, DateTimeOffset at, CancellationToken ct)
    {
        var normalised = Normalise(symbol);
        await watchlist.AddAsync(normalised, at, ct);

        Console.WriteLine($"added {normalised} as at {at:yyyy-MM-dd HH:mm:ss}Z");
    }

    public static async Task RemoveAsync(
        IWatchlistRepository watchlist, string symbol, DateTimeOffset at, CancellationToken ct)
    {
        var normalised = Normalise(symbol);
        await watchlist.RemoveAsync(normalised, at, ct);

        Console.WriteLine($"removed {normalised} as at {at:yyyy-MM-dd HH:mm:ss}Z");
    }

    public static async Task ListAsync(
        IWatchlistRepository watchlist, DateTimeOffset asOf, CancellationToken ct)
    {
        var symbols = await watchlist.ActiveAsync(asOf, ct);

        Console.WriteLine($"tracked as at {asOf:yyyy-MM-dd}: {symbols.Count} symbol(s)");
        foreach (var symbol in symbols)
        {
            Console.WriteLine($"  {symbol}");
        }
    }

    // Vendors are case sensitive in places and the store must not end up holding both
    // AAPL and aapl as separate symbols.
    private static string Normalise(string symbol) => symbol.Trim().ToUpperInvariant();
}
```

- [ ] **Step 5: Write `Program.cs`**

```csharp
using System.CommandLine;
using MarketData.Cli.Commands;
using MarketData.Hosting;
using MarketData.Storage.Dynamo;
using Microsoft.Extensions.DependencyInjection;

var options = MarketDataOptions.FromEnvironment();
var services = new ServiceCollection().AddMarketData(options).BuildServiceProvider();
var time = services.GetRequiredService<TimeProvider>();
var watchlist = services.GetRequiredService<IWatchlistRepository>();

var symbolArgument = new Argument<string>("symbol") { Description = "Ticker, e.g. AAPL." };

var addCommand = new Command("add", "Start tracking a symbol.") { symbolArgument };
addCommand.SetAction((parse, ct) =>
    WatchlistCommand.AddAsync(watchlist, parse.GetValue(symbolArgument)!, time.GetUtcNow(), ct));

var removeCommand = new Command("remove", "Stop tracking a symbol.") { symbolArgument };
removeCommand.SetAction((parse, ct) =>
    WatchlistCommand.RemoveAsync(watchlist, parse.GetValue(symbolArgument)!, time.GetUtcNow(), ct));

var asOfOption = new Option<DateTimeOffset?>("--as-of")
{
    Description = "Show membership as it was at this instant. Defaults to now."
};

var listCommand = new Command("list", "Show tracked symbols.") { asOfOption };
listCommand.SetAction((parse, ct) =>
    WatchlistCommand.ListAsync(watchlist, parse.GetValue(asOfOption) ?? time.GetUtcNow(), ct));

var watchlistCommand = new Command("watchlist", "Manage the tracked universe.")
{
    addCommand, removeCommand, listCommand
};

var root = new RootCommand("Point-in-time market data warehouse.") { watchlistCommand };

return await root.Parse(args).InvokeAsync();
```

`--as-of` defaults to now on `watchlist list` but **not** on `query`. Membership defaulting to
now is a convenience; a price defaulting to now is lookahead bias.

If the System.CommandLine 2.0.11 API differs from the shape above — `SetAction`, `Parse`,
`GetValue` — consult `dotnet-suggest`-free samples in the package's README rather than guessing,
and keep the structure: a root command, one sub-command per noun.

- [ ] **Step 6: Run and commit**

Run: `dotnet test tests/MarketData.Cli.Tests`
Expected: PASS — 2 tests.

Then a manual smoke check:
```bash
export MARKETDATA_DATA_BUCKET=pit-marketdata-data-bzun6w
export MARKETDATA_TABLE_NAME=pit-marketdata-marketdata
dotnet run --project src/MarketData.Cli -- watchlist list
```
Expected: `tracked as at <today>: 0 symbol(s)`.

```bash
git add -A
git commit -m "Add the CLI with watchlist commands"
```

---

### Task 9: `backfill`, paced against the vendor limit

**Files:**
- Create: `app/src/MarketData.Cli/Commands/BackfillCommand.cs`
- Modify: `app/src/MarketData.Cli/Program.cs`
- Test: `app/tests/MarketData.Cli.Tests/BackfillCommandTests.cs`

**Interfaces:**
- Consumes: `IngestService`, `IPriceSource.Limits`.
- Produces: `BackfillCommand.RunAsync(...)`. This is where `RateLimit`, declared in Stage 3 and unused since, earns its place.

- [ ] **Step 1: Write the failing test**

```csharp
using MarketData.Cli.Commands;
using MarketData.Sources;
using Shouldly;

namespace MarketData.Cli.Tests;

public sealed class BackfillCommandTests
{
    [Fact]
    public async Task Waits_between_symbols_to_respect_the_per_minute_limit()
    {
        var delays = new List<TimeSpan>();

        await BackfillCommand.RunAsync(
            symbols: ["AAPL", "MSFT", "NVDA"],
            from: new DateOnly(2015, 1, 1),
            to: new DateOnly(2026, 9, 8),
            limits: new RateLimit(RequestsPerDay: 800, RequestsPerMinute: 8),
            ingest: (_, _, _, ct) => Task.FromResult(0),
            delay: (span, _) => { delays.Add(span); return Task.CompletedTask; },
            ct: TestContext.Current.CancellationToken);

        // Three credits per symbol against eight per minute: a pause is required.
        delays.Count.ShouldBe(2);
        delays.ShouldAllBe(d => d > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_single_symbol_needs_no_pause()
    {
        var delays = new List<TimeSpan>();

        await BackfillCommand.RunAsync(
            ["AAPL"], new DateOnly(2015, 1, 1), new DateOnly(2026, 9, 8),
            new RateLimit(800, 8),
            (_, _, _, _) => Task.FromResult(5),
            (span, _) => { delays.Add(span); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        delays.ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Cli.Tests --filter BackfillCommandTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Write `Commands/BackfillCommand.cs`**

```csharp
using MarketData.Sources;

namespace MarketData.Cli.Commands;

/// <summary>
/// Fetches full history for one or more symbols. Paces itself against the vendor's
/// per-minute limit rather than leaving that in the operator's head — exceeding it returns
/// 429s that would surface as missing data rather than an obvious failure.
/// </summary>
public static class BackfillCommand
{
    /// <summary>Prices, splits and dividends: three requests per symbol.</summary>
    private const int CreditsPerSymbol = 3;

    public static async Task RunAsync(
        IReadOnlyList<string> symbols,
        DateOnly from,
        DateOnly to,
        RateLimit limits,
        Func<string, DateOnly, DateOnly, CancellationToken, Task<int>> ingest,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(
            60.0 * CreditsPerSymbol / Math.Max(1, limits.RequestsPerMinute));

        for (var i = 0; i < symbols.Count; i++)
        {
            if (i > 0)
            {
                await delay(pause, ct);
            }

            var symbol = symbols[i].Trim().ToUpperInvariant();
            var rows = await ingest(symbol, from, to, ct);

            Console.WriteLine($"{symbol}: {rows} row(s) written");
        }
    }
}
```

Injecting `delay` keeps the test instant; production passes `Task.Delay`.

- [ ] **Step 4: Wire it into `Program.cs`**

Add a `backfill` command taking the symbol argument plus `--from` (required) and `--to`
(defaulting to today), resolving `IngestService` and `IPriceSource` from the provider and
passing `Task.Delay` as the delay.

- [ ] **Step 5: Run and commit**

Run: `dotnet test tests/MarketData.Cli.Tests`
Expected: PASS.

```bash
git add -A
git commit -m "Add backfill, paced against the vendor per-minute limit

RateLimit has been declared and unused since Stage 3; this is where it
earns its place. Exceeding the limit returns 429s that would surface as
missing data rather than an obvious failure."
```

---

### Task 10: `query`, with a mandatory `--as-of`

**Files:**
- Create: `app/src/MarketData.Cli/Commands/QueryCommand.cs`
- Modify: `app/src/MarketData.Cli/Program.cs`
- Test: `app/tests/MarketData.Cli.Tests/QueryCommandTests.cs`

**Interfaces:**
- Consumes: `IMarketDataQuery` from Stage 4.
- Produces: `QueryCommand.RunAsync(...)` and a `query` sub-command whose `--as-of` is required.

This is the most likely place for lookahead bias to re-enter the system, so the requirement is
enforced by the parser and asserted by a test.

- [ ] **Step 1: Write the failing test**

```csharp
using System.CommandLine;
using MarketData.Domain;
using Shouldly;

namespace MarketData.Cli.Tests;

public sealed class QueryCommandTests
{
    [Fact]
    public void Query_without_as_of_is_a_usage_error()
    {
        var command = MarketData.Cli.Commands.QueryCommand.Build(
            (_, _, _, _, _, _, _) => Task.CompletedTask);

        var result = command.Parse(["AAPL", "--on", "2020-06-15"]);

        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldContain(e => e.Message.Contains("as-of", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Query_with_as_of_parses_cleanly()
    {
        var command = MarketData.Cli.Commands.QueryCommand.Build(
            (_, _, _, _, _, _, _) => Task.CompletedTask);

        var result = command.Parse(
            ["AAPL", "--on", "2020-06-15", "--as-of", "2020-07-01"]);

        result.Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("none", PriceAdjustment.None)]
    [InlineData("splits", PriceAdjustment.SplitsOnly)]
    [InlineData("all", PriceAdjustment.SplitsAndDividends)]
    public void Adjust_maps_to_the_right_enum(string text, PriceAdjustment expected)
    {
        MarketData.Cli.Commands.QueryCommand.ParseAdjustment(text).ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Cli.Tests --filter QueryCommandTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Write `Commands/QueryCommand.cs`**

```csharp
using System.CommandLine;
using MarketData.Domain;

namespace MarketData.Cli.Commands;

/// <summary>
/// The as-of read. <c>--as-of</c> is required, deliberately: defaulting it to now is exactly
/// how lookahead bias would re-enter a system that spent four stages designing it out, and a
/// CLI is where such a convenience would most plausibly be added later.
/// </summary>
public static class QueryCommand
{
    public static Command Build(
        Func<string, DateOnly, DateOnly, DateTimeOffset, PriceAdjustment, ObservationMode,
            CancellationToken, Task> run)
    {
        var symbol = new Argument<string>("symbol") { Description = "Ticker, e.g. AAPL." };

        var on = new Option<DateOnly?>("--on")
        {
            Description = "A single trading date. Omit to use --from and --to."
        };
        var from = new Option<DateOnly?>("--from") { Description = "Start of the date range." };
        var to = new Option<DateOnly?>("--to") { Description = "End of the date range." };

        var asOf = new Option<DateTimeOffset>("--as-of")
        {
            Description = "What was knowable at this instant. Required.",
            Required = true
        };

        var adjust = new Option<string>("--adjust")
        {
            Description = "none | splits | all. Default: splits.",
            DefaultValueFactory = _ => "splits"
        };

        var observedOnly = new Option<bool>("--observed-only")
        {
            Description = "Exclude bars whose observed_at was reconstructed."
        };

        var command = new Command("query", "Ask what was knowable about a symbol.")
        {
            symbol, on, from, to, asOf, adjust, observedOnly
        };

        command.SetAction((parse, ct) =>
        {
            var single = parse.GetValue(on);
            var start = single ?? parse.GetValue(from)
                ?? throw new InvalidOperationException("Supply --on, or --from and --to.");
            var end = single ?? parse.GetValue(to) ?? start;

            return run(
                parse.GetValue(symbol)!.Trim().ToUpperInvariant(),
                start, end,
                parse.GetValue(asOf),
                ParseAdjustment(parse.GetValue(adjust)!),
                parse.GetValue(observedOnly) ? ObservationMode.ObservedOnly : ObservationMode.All,
                ct);
        });

        return command;
    }

    public static PriceAdjustment ParseAdjustment(string text) => text.ToLowerInvariant() switch
    {
        "none" => PriceAdjustment.None,
        "splits" => PriceAdjustment.SplitsOnly,
        "all" => PriceAdjustment.SplitsAndDividends,
        _ => throw new ArgumentException(
            $"Unknown --adjust value '{text}'. Use none, splits or all.", nameof(text))
    };
}
```

- [ ] **Step 4: Wire it into `Program.cs`**

Register `IMarketDataQuery` as `new DuckDbMarketDataQuery(CuratedSource.S3(options.DataBucket))`,
add `QueryCommand.Build(...)` to the root, and print each returned bar as
`{EffectiveDate} O {Open} H {High} L {Low} C {Close} V {Volume} [{ObservedAtKind}]`.

- [ ] **Step 5: Run the tests, then a real end-to-end check**

Run: `dotnet test`
Expected: PASS.

Then, against the live warehouse:
```bash
export MARKETDATA_DATA_BUCKET=pit-marketdata-data-bzun6w
export MARKETDATA_TABLE_NAME=pit-marketdata-marketdata
dotnet run --project src/MarketData.Cli -- watchlist add AAPL
dotnet run --project src/MarketData.Cli -- backfill AAPL --from 2015-01-01
dotnet run --project src/MarketData.Cli -- query AAPL --on 2020-06-15 --as-of 2020-07-01
dotnet run --project src/MarketData.Cli -- query AAPL --on 2020-06-15 --as-of 2026-09-08
```
Expected: the first query prints a close near **342.99**, the second near **85.75**. This is
the first end-to-end proof against real data and real infrastructure; if the two numbers are
equal, the adjustment is not being applied and something is wrong.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add query with a mandatory --as-of

Defaulting it to now is how lookahead bias would re-enter the system,
and a CLI is where that convenience would most plausibly be added."
```

---

# Stage E — Deploy

### Task 11: Build script and the ingest Terraform module

**Files:**
- Create: `scripts/build-lambda.sh`
- Create: `infra/terraform/modules/ingest/main.tf`, `variables.tf`, `outputs.tf`

**Interfaces:**
- Consumes: the Lambda project; the storage module's bucket and table.
- Produces: a deployable zip and the Terraform to place it. Task 12 wires it into the root.

- [ ] **Step 1: Write `scripts/build-lambda.sh`**

```bash
#!/usr/bin/env bash
# Publishes the ingest Lambda for linux-arm64 and zips it for Terraform.
# Usage: ./scripts/build-lambda.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/dist"
staging="$out/lambda"

rm -rf "$staging" "$out/lambda.zip"
mkdir -p "$staging"

dotnet publish "$root/app/src/MarketData.Lambda/MarketData.Lambda.csproj" \
  --configuration Release \
  --runtime linux-arm64 \
  --self-contained false \
  --output "$staging"

( cd "$staging" && zip -qr "$out/lambda.zip" . )

echo "built $out/lambda.zip ($(du -h "$out/lambda.zip" | cut -f1))"
```

`dist/` is already gitignored. If `zip` is unavailable on Windows, use
`Compress-Archive -Path "$staging/*" -DestinationPath "$out/lambda.zip"` in PowerShell instead.

- [ ] **Step 2: Write `modules/ingest/variables.tf`**

```hcl
variable "project" {
  description = "Project name prefix."
  type        = string
}

variable "data_bucket" {
  description = "Name of the S3 bucket holding raw/ and curated/."
  type        = string
}

variable "data_bucket_arn" {
  description = "ARN of the data bucket."
  type        = string
}

variable "table_name" {
  description = "Name of the DynamoDB state table."
  type        = string
}

variable "table_arn" {
  description = "ARN of the DynamoDB table."
  type        = string
}

variable "region" {
  description = "AWS region for the function."
  type        = string
}

variable "lambda_zip_path" {
  description = "Path to the built deployment package."
  type        = string
}

variable "api_key_parameter" {
  description = "SSM parameter holding the vendor API key."
  type        = string
  default     = "/pit-marketdata/twelvedata/apikey"
}

variable "schedule_expression" {
  description = "When to run. Weekdays after the US close."
  type        = string
  default     = "cron(15 22 ? * MON-FRI *)"
}

variable "log_retention_days" {
  description = "CloudWatch log retention. The default is never expire, which costs money quietly."
  type        = number
  default     = 14
}
```

- [ ] **Step 3: Write `modules/ingest/main.tf`**

```hcl
data "aws_caller_identity" "current" {}

resource "aws_cloudwatch_log_group" "ingest" {
  name              = "/aws/lambda/${var.project}-ingest"
  retention_in_days = var.log_retention_days
}

data "aws_iam_policy_document" "assume" {
  statement {
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "ingest" {
  name               = "${var.project}-ingest"
  assume_role_policy = data.aws_iam_policy_document.assume.json
}

# Least privilege, and deliberately no s3:DeleteObject: the store is append-only, so the
# function should be structurally incapable of removing anything.
data "aws_iam_policy_document" "ingest" {
  statement {
    actions   = ["s3:PutObject", "s3:GetObject", "s3:ListBucket"]
    resources = [var.data_bucket_arn, "${var.data_bucket_arn}/*"]
  }

  statement {
    actions = [
      "dynamodb:GetItem",
      "dynamodb:PutItem",
      "dynamodb:UpdateItem",
      "dynamodb:Query"
    ]
    resources = [var.table_arn]
  }

  statement {
    actions   = ["ssm:GetParameter"]
    resources = ["arn:aws:ssm:${var.region}:${data.aws_caller_identity.current.account_id}:parameter${var.api_key_parameter}"]
  }

  statement {
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["${aws_cloudwatch_log_group.ingest.arn}:*"]
  }
}

resource "aws_iam_role_policy" "ingest" {
  role   = aws_iam_role.ingest.id
  policy = data.aws_iam_policy_document.ingest.json
}

resource "aws_lambda_function" "ingest" {
  function_name = "${var.project}-ingest"
  role          = aws_iam_role.ingest.arn
  handler       = "MarketData.Lambda"
  runtime       = "dotnet10"
  architectures = ["arm64"]
  memory_size   = 512
  timeout       = 300

  filename         = var.lambda_zip_path
  source_code_hash = filebase64sha256(var.lambda_zip_path)

  # Two concurrent runs would both write raw objects and both advance the same cursor.
  reserved_concurrent_executions = 1

  environment {
    variables = {
      MARKETDATA_DATA_BUCKET = var.data_bucket
      MARKETDATA_TABLE_NAME  = var.table_name
      MARKETDATA_REGION      = var.region
    }
  }

  depends_on = [aws_cloudwatch_log_group.ingest]
}

resource "aws_cloudwatch_event_rule" "schedule" {
  name                = "${var.project}-ingest-schedule"
  description         = "Weekdays after the US close."
  schedule_expression = var.schedule_expression
}

resource "aws_cloudwatch_event_target" "ingest" {
  rule = aws_cloudwatch_event_rule.schedule.name
  arn  = aws_lambda_function.ingest.arn
}

resource "aws_lambda_permission" "events" {
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.ingest.function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.schedule.arn
}
```

The vendor key is **not** in `environment.variables`: it would then be visible in the console
and in `GetFunctionConfiguration`. It is read from SSM at runtime instead.

If `runtime = "dotnet10"` is rejected by the provider, check the supported identifiers with
`aws lambda list-runtimes` or the provider docs and use the managed .NET 10 identifier; do not
silently fall back to a custom `provided.al2023` runtime without noting why.

- [ ] **Step 4: Write `modules/ingest/outputs.tf`**

```hcl
output "function_name" {
  description = "Name of the ingest function."
  value       = aws_lambda_function.ingest.function_name
}

output "log_group" {
  description = "CloudWatch log group receiving function logs."
  value       = aws_cloudwatch_log_group.ingest.name
}
```

- [ ] **Step 5: Validate and commit**

```bash
cd infra/terraform && terraform fmt -check -recursive && terraform init -backend=false && terraform validate
```
Expected: valid.

```bash
git add -A
git commit -m "Add the ingest Lambda module and its build script

Least privilege with no s3:DeleteObject: the store is append-only, so
the function should be incapable of removing anything. Log retention is
explicit because the default is never expire."
```

---

### Task 12: Wire the module in and deploy

**Files:**
- Modify: `infra/terraform/main.tf`, `variables.tf`, `outputs.tf`

**Interfaces:**
- Consumes: `modules/ingest` from Task 11 and the storage module's outputs.
- Produces: a deployed, scheduled function.

- [ ] **Step 1: Add the module to the root configuration**

In `variables.tf`:

```hcl
variable "lambda_zip_path" {
  description = "Path to the built Lambda deployment package."
  type        = string
  default     = "../../dist/lambda.zip"
}
```

In `main.tf`:

```hcl
module "ingest" {
  source = "./modules/ingest"

  project         = var.project
  region          = var.region
  data_bucket     = module.storage.data_bucket
  data_bucket_arn = module.storage.data_bucket_arn
  table_name      = module.storage.table_name
  table_arn       = module.storage.table_arn
  lambda_zip_path = var.lambda_zip_path
}
```

In `outputs.tf`:

```hcl
output "ingest_function_name" {
  description = "Name of the scheduled ingest function."
  value       = module.ingest.function_name
}
```

- [ ] **Step 2: Build and plan**

```bash
./scripts/build-lambda.sh
cd infra/terraform && terraform init && terraform plan
```
Expected: the plan adds a log group, a role, a role policy, a function, an event rule, an event
target and a permission — seven resources, nothing destroyed.

- [ ] **Step 3: Apply**

Run: `terraform apply`
Expected: seven added.

- [ ] **Step 4: Invoke it once by hand before trusting the schedule**

```bash
aws lambda invoke --function-name pit-marketdata-ingest --region ap-southeast-2 /tmp/out.json
aws logs tail /aws/lambda/pit-marketdata-ingest --region ap-southeast-2 --since 5m
```
Expected: the log line `run <id>: n/n symbols, ...`. If the watchlist is empty the run is a
no-op, which still proves the wiring, permissions and SSM read all work.

- [ ] **Step 5: Confirm the run record**

```bash
aws dynamodb query --region ap-southeast-2 \
  --table-name pit-marketdata-marketdata \
  --key-condition-expression 'pk = :p' \
  --expression-attribute-values '{":p":{"S":"RUN#<id from the log>"}}'
```
Expected: one item with `expires_at` about 90 days out.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Deploy the scheduled ingest function"
```

---

### Task 13: Update the documentation

**Files:**
- Modify: `README.md`, `CLAUDE.md`

- [ ] **Step 1: `README.md`**

- Move Stage 5 to ✅ Done and mark Stage 6 as next.
- Replace "**There is deliberately no runnable application yet**" with a short **Using it**
  section showing the four real commands and the two-answer AAPL example as actual CLI output.
- Note in **Stack** that the Lambda deliberately carries no DuckDB and why.

- [ ] **Step 2: `CLAUDE.md`**

- **Current state**: Stage 5 complete, Stage 6 next, updated test count.
- Add to the non-negotiables: the Lambda must never reference DuckDB, and an architecture
  test enforces it.
- Add to **Environment**: the two environment variables both entry points require, and the
  `dotnet run --project app/src/MarketData.Cli --` invocation.
- Record the ten-day recent-closes limitation from §9 of the spec.

- [ ] **Step 3: Run everything, commit, push**

```bash
cd app && dotnet test && cd ..
git add -A
git commit -m "Update README and CLAUDE.md for Stage 5"
git push
```

Expected: whole suite green, CI green.

---

## Definition of done for this plan

- `dotnet test` passes without Docker (integration tests self-skip) and in CI with it.
- `MarketData.Lambda` has no DuckDB reference, direct or transitive, proven by a test that has
  been seen to fail.
- The function is deployed, has been invoked by hand, and wrote a run record with a TTL.
- `marketdata watchlist add AAPL` then `backfill` then two `query` calls return **342.99** as at
  2020-07-01 and **85.75** as at today, against real infrastructure.
- `query` without `--as-of` exits non-zero with a usage error.
- No `s3:DeleteObject` appears anywhere in the function's IAM policy.
- The vendor API key appears in no environment variable, and no raw object contains it.

## Deliberately deferred

- `reprocess` and `compact` — Stage 6, and `reprocess` is what catches restatements older than
  the cursor's ten-day window.
- Run records mirrored to S3.
- Alerting when a run records errors. Nothing currently reads the run record.
- GitHub OIDC for deployment.
- Any hosted API or UI. If one is built: `asOf` is required and a request without it is a 400;
  the web tier gets `s3:GetObject` on `curated/*` and nothing else.
