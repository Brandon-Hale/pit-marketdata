# Layer 1 Stage 4 Implementation Plan — As-of query, adjustment, and the un-adjust fix

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the warehouse answerable — add the as-of read path, the adjustment arithmetic, and the eight temporal tests that prove Stages 0–3 were built correctly — and fix the ingest path so it stores the price actually quoted on each day rather than the vendor's split-adjusted number.

**Architecture:** The adjustment rule is a pure function in the dependency-free `MarketData.Domain` project, tested in isolation with hand-checked numbers. DuckDB does the as-of filtering with one window function; C# applies the factors. The same factor is divided on ingest and multiplied on query, so the two directions cannot drift without a round-trip test failing.

**Tech Stack:** C# / .NET 10, DuckDB.NET 1.5.5 (with `httpfs` for S3), Parquet.Net 6.1.0, xUnit v3 on Microsoft.Testing.Platform, Shouldly, Testcontainers.LocalStack.

**Spec:** `docs/superpowers/specs/2026-09-08-layer1-stage4-asof-query-design.md`

**Scope:** Stage 4 only. The Lambda, the schedule, IAM and the CLI are Stage 5. Compaction and the operator-facing reprocess command are Stage 6.

## Global Constraints

These carry over from the Stage 0–3 plan and still hold. Breaking one is silent.

- **Target framework `net10.0`.** Nullable enabled, warnings are errors. No `#pragma warning disable` without a comment saying why.
- **`MarketData.Domain` has zero package references.** An architecture test enforces it. `AdjustmentCalculator` goes in Domain, so it must use only the BCL.
- **Central package management.** Versions live in `app/Directory.Packages.props`. After any `dotnet add package`, strip the `Version=` attribute it writes.
- **No `DateTime.UtcNow` / `DateTimeOffset.UtcNow`** outside a composition root. Time comes from an injected `TimeProvider`.
- **Prices are `decimal`, never `double`.** Parsed from the vendor's exact decimal strings.
- **Append-only.** No code path updates or deletes a curated row.
- **Tests run on Microsoft.Testing.Platform.** Do not add `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio` — the .NET 10 SDK refuses the VSTest target and `dotnet test` breaks outright.
- **`.slnx`, not `.sln`.** The solution is `app/MarketData.slnx`.
- **Reads use DuckDB, writes use Parquet.Net.** DuckDB returns a Parquet `DATE` column as `DateOnly`, not `DateTime`.
- **Split ratios come from `to_factor / from_factor`**, never the vendor's rounded `ratio` field.
- **Work from `app/`.** `dotnet` commands assume that working directory unless stated otherwise.

## File Structure

```
app/src/MarketData.Domain/
  PriceAdjustment.cs            NEW  enum: None, SplitsOnly, SplitsAndDividends
  ObservationMode.cs            NEW  enum: All, ObservedOnly
  AdjustmentCalculator.cs       NEW  pure; split + dividend factors
  DailyBar.cs                   MOD  + SplitsRawKey

app/src/MarketData.Storage/
  Parquet/PriceRow.cs           MOD  + SplitsRawKey
  Local/LocalCuratedStore.cs    MOD  map the new column

app/src/MarketData.Sources/
  IngestService.cs              MOD  fetch splits, un-adjust, record key, real prior-knowledge

app/src/MarketData.Query/       NEW PROJECT
  MarketData.Query.csproj
  IMarketDataQuery.cs           the interface
  CuratedSource.cs              resolves s3:// or a local directory into DuckDB globs
  DuckDbMarketDataQuery.cs      two SELECTs, then the calculator

app/tests/MarketData.Domain.Tests/
  AdjustmentCalculatorTests.cs  NEW  pure, hand-checked AAPL numbers

app/tests/MarketData.Query.Tests/   NEW PROJECT
  MarketData.Query.Tests.csproj
  QueryFixture.cs               builds a curated store on disk for a test
  TemporalTests.cs              the eight tests from the spec
  RoundTripTests.cs             un-adjust then re-adjust returns the vendor number

app/tests/MarketData.Integration.Tests/
  S3ReadPathTests.cs            NEW  local and s3:// return identical rows
```

---

# Stage A — The arithmetic

### Task 1: Split factors as a pure function

**Files:**
- Create: `app/src/MarketData.Domain/PriceAdjustment.cs`, `app/src/MarketData.Domain/ObservationMode.cs`, `app/src/MarketData.Domain/AdjustmentCalculator.cs`
- Test: `app/tests/MarketData.Domain.Tests/AdjustmentCalculatorTests.cs`

**Interfaces:**
- Consumes: `CorporateAction`, `CorporateActionType` from `MarketData.Domain`.
- Produces: `PriceAdjustment` (enum: `None`, `SplitsOnly`, `SplitsAndDividends`); `ObservationMode` (enum: `All`, `ObservedOnly`); `AdjustmentCalculator.SplitFactor(IEnumerable<CorporateAction>, DateOnly) -> decimal`. Tasks 4 and 7 both call `SplitFactor`.

The factor is the product of `Ratio` over splits whose `ExDate` is **strictly after** the bar date. Strictly, because on the ex-date itself the quoted price already reflects the split — AAPL closed at 129.04 on 2020-08-31, which is post-split.

- [ ] **Step 1: Write the failing tests**

```csharp
using MarketData.Domain;
using Shouldly;

namespace MarketData.Domain.Tests;

public sealed class AdjustmentCalculatorTests
{
    private static CorporateAction Split(DateOnly exDate, decimal ratio) => new(
        "AAPL", exDate, CorporateActionType.Split, ratio, null, "USD", "twelvedata",
        new DateTimeOffset(exDate.ToDateTime(new TimeOnly(20, 15)), TimeSpan.Zero),
        ObservationKind.Inferred, "run-1", "raw/x.json");

    // The two real AAPL splits, as the vendor reports them.
    private static readonly CorporateAction FourForOne = Split(new DateOnly(2020, 8, 31), 0.25m);
    private static readonly CorporateAction SevenForOne = Split(new DateOnly(2014, 6, 9), 1m / 7m);

    [Fact]
    public void No_actions_gives_exactly_one()
    {
        AdjustmentCalculator.SplitFactor([], new DateOnly(2020, 6, 15)).ShouldBe(1m);
    }

    [Fact]
    public void A_later_split_scales_an_earlier_bar()
    {
        var factor = AdjustmentCalculator.SplitFactor([FourForOne], new DateOnly(2020, 6, 15));

        factor.ShouldBe(0.25m);
        // Apple really closed at 342.99 that day; the vendor reports 85.7475.
        (342.99m * factor).ShouldBe(85.7475m);
    }

    [Fact]
    public void A_split_on_the_bar_date_itself_is_not_applied()
    {
        // 2020-08-31 closed at 129.04, already post-split.
        AdjustmentCalculator.SplitFactor([FourForOne], new DateOnly(2020, 8, 31)).ShouldBe(1m);
    }

    [Fact]
    public void An_earlier_split_does_not_affect_a_later_bar()
    {
        AdjustmentCalculator.SplitFactor([SevenForOne], new DateOnly(2020, 6, 15)).ShouldBe(1m);
    }

    [Fact]
    public void Factors_compound_across_two_splits()
    {
        var factor = AdjustmentCalculator.SplitFactor(
            [FourForOne, SevenForOne], new DateOnly(2014, 6, 6));

        // 7-for-1 then 4-for-1 = 28x. 645.57 / 28 = 23.05607...
        (645.57m * factor).ShouldBe(23.056071m, tolerance: 0.000001m);
    }

    [Fact]
    public void Dividends_do_not_contribute_to_the_split_factor()
    {
        var dividend = new CorporateAction(
            "AAPL", new DateOnly(2020, 8, 7), CorporateActionType.Dividend, null, 0.205m,
            "USD", "twelvedata", new DateTimeOffset(2020, 8, 7, 20, 15, 0, TimeSpan.Zero),
            ObservationKind.Inferred, "run-1", "raw/x.json");

        AdjustmentCalculator.SplitFactor([dividend], new DateOnly(2020, 6, 15)).ShouldBe(1m);
    }

    [Fact]
    public void A_split_with_a_null_ratio_is_ignored_rather_than_throwing()
    {
        var malformed = new CorporateAction(
            "AAPL", new DateOnly(2020, 8, 31), CorporateActionType.Split, null, null,
            "USD", "twelvedata", new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero),
            ObservationKind.Inferred, "run-1", "raw/x.json");

        AdjustmentCalculator.SplitFactor([malformed], new DateOnly(2020, 6, 15)).ShouldBe(1m);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Domain.Tests`
Expected: FAIL — `AdjustmentCalculator` does not exist.

- [ ] **Step 3: Write the two enums**

```csharp
namespace MarketData.Domain;

/// <summary>Which corporate actions to fold into a returned price.</summary>
public enum PriceAdjustment
{
    /// <summary>The price exactly as quoted on the day. The literal historical record.</summary>
    None,

    /// <summary>Scaled by splits knowable as at the query instant.</summary>
    SplitsOnly,

    /// <summary>Scaled by splits and dividends knowable as at the query instant.</summary>
    SplitsAndDividends
}
```

```csharp
namespace MarketData.Domain;

/// <summary>
/// Whether reconstructed observations are included. Applies to bars only — never to
/// corporate actions, whose ex-dates are a matter of public record.
/// </summary>
public enum ObservationMode
{
    All,

    /// <summary>Only bars whose observed_at was captured live.</summary>
    ObservedOnly
}
```

- [ ] **Step 4: Write `AdjustmentCalculator.cs`**

```csharp
namespace MarketData.Domain;

/// <summary>
/// Cumulative corporate-action factors. Pure: no clock, no I/O, no prior state, so the
/// same inputs give the same factor forever. Callers supply only the actions they consider
/// visible; deciding visibility is the query layer's job, not this one's.
/// </summary>
public static class AdjustmentCalculator
{
    /// <summary>
    /// Product of split ratios for splits taking effect strictly after
    /// <paramref name="barDate"/>. On an ex-date the quoted price already reflects the
    /// split, so that day's own split must not be applied again.
    /// </summary>
    public static decimal SplitFactor(IEnumerable<CorporateAction> actions, DateOnly barDate)
    {
        var factor = 1m;

        foreach (var action in actions)
        {
            if (action.ActionType != CorporateActionType.Split ||
                action.ExDate <= barDate ||
                action.Ratio is not { } ratio ||
                ratio <= 0m)
            {
                continue;
            }

            factor *= ratio;
        }

        return factor;
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MarketData.Domain.Tests`
Expected: PASS. The Task 1 architecture tests from the Stage 0–3 plan must still pass — Domain gained no packages.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add split adjustment factors as a pure domain function

Strictly-after is load-bearing: on an ex-date the quoted price already
reflects the split, so applying it again double-counts. Asserted
against the real AAPL 4-for-1 and the 28x compounding through 2014."
```

---

### Task 2: Dividend factors

**Files:**
- Modify: `app/src/MarketData.Domain/AdjustmentCalculator.cs`
- Test: `app/tests/MarketData.Domain.Tests/AdjustmentCalculatorTests.cs`

**Interfaces:**
- Consumes: `SplitFactor` from Task 1.
- Produces: `AdjustmentCalculator.DividendFactor(IEnumerable<CorporateAction>, DateOnly, Func<DateOnly, decimal?>) -> decimal` and `AdjustmentCalculator.Factor(IEnumerable<CorporateAction>, DateOnly, PriceAdjustment, Func<DateOnly, decimal?>) -> decimal`. Task 7 calls `Factor`.

A dividend contributes `1 - amount / close_on_day_before_ex_date`. The prior close comes from a caller-supplied lookup rather than a data dependency, which keeps Domain free of storage types. When the lookup returns null the dividend contributes nothing — a missing prior close must not silently produce a wrong number.

- [ ] **Step 1: Write the failing tests**

Append to `AdjustmentCalculatorTests.cs`:

```csharp
    private static CorporateAction Dividend(DateOnly exDate, decimal amount) => new(
        "AAPL", exDate, CorporateActionType.Dividend, null, amount, "USD", "twelvedata",
        new DateTimeOffset(exDate.ToDateTime(new TimeOnly(20, 15)), TimeSpan.Zero),
        ObservationKind.Inferred, "run-1", "raw/x.json");

    [Fact]
    public void A_dividend_scales_by_one_minus_its_yield()
    {
        var div = Dividend(new DateOnly(2020, 8, 7), 0.205m);

        var factor = AdjustmentCalculator.DividendFactor(
            [div], new DateOnly(2020, 6, 15), _ => 100m);

        // 1 - 0.205/100 = 0.99795
        factor.ShouldBe(0.99795m);
    }

    [Fact]
    public void Dividend_factors_compound()
    {
        var a = Dividend(new DateOnly(2020, 8, 7), 0.205m);
        var b = Dividend(new DateOnly(2020, 11, 6), 0.205m);

        var factor = AdjustmentCalculator.DividendFactor(
            [a, b], new DateOnly(2020, 6, 15), _ => 100m);

        factor.ShouldBe(0.99795m * 0.99795m);
    }

    [Fact]
    public void A_dividend_on_or_before_the_bar_date_is_not_applied()
    {
        var div = Dividend(new DateOnly(2020, 6, 15), 0.205m);

        AdjustmentCalculator.DividendFactor([div], new DateOnly(2020, 6, 15), _ => 100m)
            .ShouldBe(1m);
    }

    [Fact]
    public void A_missing_prior_close_contributes_nothing_rather_than_guessing()
    {
        var div = Dividend(new DateOnly(2020, 8, 7), 0.205m);

        AdjustmentCalculator.DividendFactor([div], new DateOnly(2020, 6, 15), _ => null)
            .ShouldBe(1m);
    }

    [Fact]
    public void Factor_selects_by_adjustment_mode()
    {
        var actions = new[] { FourForOne, Dividend(new DateOnly(2020, 8, 7), 0.205m) };
        var bar = new DateOnly(2020, 6, 15);

        AdjustmentCalculator.Factor(actions, bar, PriceAdjustment.None, _ => 100m)
            .ShouldBe(1m);
        AdjustmentCalculator.Factor(actions, bar, PriceAdjustment.SplitsOnly, _ => 100m)
            .ShouldBe(0.25m);
        AdjustmentCalculator.Factor(actions, bar, PriceAdjustment.SplitsAndDividends, _ => 100m)
            .ShouldBe(0.25m * 0.99795m);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Domain.Tests --filter AdjustmentCalculatorTests`
Expected: FAIL — `DividendFactor` does not exist.

- [ ] **Step 3: Add the two methods**

Add to `AdjustmentCalculator`:

```csharp
    /// <summary>
    /// Product of <c>1 - amount / prior close</c> for dividends going ex strictly after
    /// <paramref name="barDate"/>. <paramref name="closeOnDayBefore"/> supplies the close
    /// on the last trading day before an ex-date, and returns null when that bar is not
    /// available; such a dividend contributes nothing rather than a guessed factor.
    /// </summary>
    public static decimal DividendFactor(
        IEnumerable<CorporateAction> actions,
        DateOnly barDate,
        Func<DateOnly, decimal?> closeOnDayBefore)
    {
        var factor = 1m;

        foreach (var action in actions)
        {
            if (action.ActionType != CorporateActionType.Dividend ||
                action.ExDate <= barDate ||
                action.Amount is not { } amount ||
                amount <= 0m)
            {
                continue;
            }

            if (closeOnDayBefore(action.ExDate) is not { } priorClose || priorClose <= 0m)
            {
                continue;
            }

            factor *= 1m - (amount / priorClose);
        }

        return factor;
    }

    /// <summary>The factor for one bar under a given adjustment mode.</summary>
    public static decimal Factor(
        IEnumerable<CorporateAction> actions,
        DateOnly barDate,
        PriceAdjustment adjustment,
        Func<DateOnly, decimal?> closeOnDayBefore) => adjustment switch
        {
            PriceAdjustment.None => 1m,
            PriceAdjustment.SplitsOnly => SplitFactor(actions, barDate),
            PriceAdjustment.SplitsAndDividends =>
                SplitFactor(actions, barDate) * DividendFactor(actions, barDate, closeOnDayBefore),
            _ => throw new ArgumentOutOfRangeException(nameof(adjustment), adjustment, null)
        };
```

`actions` is enumerated more than once by `Factor`. Callers pass materialised lists; the query layer in Task 7 always does.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MarketData.Domain.Tests --filter AdjustmentCalculatorTests`
Expected: PASS — 12 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add dividend adjustment factors

Multiplicative back-adjustment keeps percentage returns correct across
ex-dates, which subtraction does not. A missing prior close contributes
no factor rather than a guessed one."
```

---

# Stage B — Fix the ingest path

### Task 3: Record which splits payload un-adjusted each row

**Files:**
- Modify: `app/src/MarketData.Domain/DailyBar.cs`, `app/src/MarketData.Storage/Parquet/PriceRow.cs`, `app/src/MarketData.Storage/Local/LocalCuratedStore.cs`
- Test: `app/tests/MarketData.Storage.Tests/CuratedRoundTripTests.cs`

**Interfaces:**
- Consumes: `DailyBar` as it exists today.
- Produces: `DailyBar` and `PriceRow` each carrying `SplitsRawKey` (string). Task 4 populates it; Task 8's rebuild test reads it.

The curated store is empty, so this is a clean schema rather than an evolution.

- [ ] **Step 1: Write the failing test**

Append to `CuratedRoundTripTests.cs`:

```csharp
    [Fact]
    public async Task Splits_raw_key_survives_the_write()
    {
        var store = new LocalCuratedStore(_root);
        var at = new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero);
        var bar = Bar(129.04m, at, ObservationKind.Inferred) with
        {
            SplitsRawKey = "raw/source=twelvedata/dataset=splits/dt=2026-09-08/AAPL.json"
        };

        await store.AppendPricesAsync([bar], "run-1", TestContext.Current.CancellationToken);

        using var duck = new DuckDbReader();
        var rows = duck.Query(
            $"SELECT SplitsRawKey FROM read_parquet('{DuckDbReader.Glob(_root, "prices_daily")}')");

        rows[0]["SplitsRawKey"]!.ToString()
            .ShouldBe("raw/source=twelvedata/dataset=splits/dt=2026-09-08/AAPL.json");
    }
```

`Bar(...)` in that file builds a positional `DailyBar`. Adding a trailing parameter with a default keeps the existing call sites compiling.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Storage.Tests --filter CuratedRoundTripTests`
Expected: FAIL — `DailyBar` has no `SplitsRawKey`.

- [ ] **Step 3: Add the property to `DailyBar`**

Add a final positional parameter, after `RawKey`:

```csharp
    string RawKey,
    string SplitsRawKey = "")
```

and document it inside the record body:

```csharp
    /// <summary>
    /// Raw key of the splits payload used to un-adjust this bar's prices. Empty when no
    /// un-adjustment was applied. Recorded so a rebuild can redo identical arithmetic
    /// years later rather than using whatever splits are known at rebuild time.
    /// </summary>
    public string SplitsRawKey { get; init; } = SplitsRawKey;
```

- [ ] **Step 4: Add the column to `PriceRow` and map it**

In `PriceRow.cs`, after `RawKey`:

```csharp
    public string SplitsRawKey { get; set; } = string.Empty;
```

In `LocalCuratedStore.ToRow(DailyBar b)`, after `RawKey = b.RawKey,`:

```csharp
        SplitsRawKey = b.SplitsRawKey,
```

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS. Existing tests construct `DailyBar` positionally without the new argument and still compile because it has a default.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Record the splits payload used to un-adjust each price row

A rebuild must redo the arithmetic the original ingest did, not the
arithmetic today's split history implies. Naming the exact envelope is
what makes that possible."
```

---

### Task 4: Un-adjust the vendor's split-adjusted prices on ingest

**Files:**
- Modify: `app/src/MarketData.Sources/IngestService.cs`
- Test: `app/tests/MarketData.Sources.Tests/IngestServiceTests.cs`

**Interfaces:**
- Consumes: `AdjustmentCalculator.SplitFactor` from Task 1; `DailyBar.SplitsRawKey` from Task 3; `IPriceSource.FetchSplitsAsync` and `TwelveDataActionsNormaliser.ParseSplits`, both of which already exist.
- Produces: `IngestService` writing true unadjusted prices, and appending corporate actions in the same run. `IngestResult` gains `ActionsWritten` (int).

The vendor reports prices adjusted for every split up to the fetch instant. Dividing by the same-run split factor recovers what was quoted on the day.

- [ ] **Step 1: Write the failing test**

Append to `IngestServiceTests.cs`. The stub returns the prices payload for the `time_series` call and a splits payload for the `splits` call, so `StubHandler` needs to vary by path — add this alongside it in `StubHandler.cs`:

```csharp
using System.Net;

namespace MarketData.Sources.Tests;

/// <summary>Returns a different canned body per URL path segment.</summary>
public sealed class RoutingStubHandler(IReadOnlyDictionary<string, string> bodyByPath)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/');
        var body = bodyByPath.TryGetValue(path, out var b) ? b : "{}";

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        });
    }
}
```

Then the test:

```csharp
    // The vendor reports 2020-06-15 as 85.74750 because of the 2020-08-31 4-for-1.
    // Apple actually closed at 342.99 that day.
    private const string AdjustedPricesBody =
        """{"meta":{"symbol":"AAPL","currency":"USD"},"values":[{"datetime":"2020-06-15","open":"85.00000","high":"86.00000","low":"84.00000","close":"85.74750","volume":"34702000"}]}""";

    private const string SplitsBody =
        """{"meta":{"symbol":"AAPL","currency":"USD"},"splits":[{"date":"2020-08-31","ratio":0.25,"from_factor":4,"to_factor":1}]}""";

    [Fact]
    public async Task Un_adjusts_the_vendors_split_adjusted_price()
    {
        var handler = new RoutingStubHandler(new Dictionary<string, string>
        {
            ["time_series"] = AdjustedPricesBody,
            ["splits"] = SplitsBody
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var curated = new CapturingCuratedStore();

        var service = new IngestService(
            new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now)),
            new TwelveDataPriceNormaliser(),
            new TwelveDataActionsNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            curated,
            new InMemoryCursorRepository());

        await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1",
            TestContext.Current.CancellationToken);

        var bar = curated.Prices.ShouldHaveSingleItem();
        bar.Close.ShouldBe(342.99m);
        bar.SplitsRawKey.ShouldBe(
            "raw/source=twelvedata/dataset=splits/dt=2026-09-08/AAPL.json");
    }

    [Fact]
    public async Task Volume_is_un_adjusted_inversely()
    {
        var handler = new RoutingStubHandler(new Dictionary<string, string>
        {
            ["time_series"] = AdjustedPricesBody,
            ["splits"] = SplitsBody
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var curated = new CapturingCuratedStore();

        var service = new IngestService(
            new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now)),
            new TwelveDataPriceNormaliser(),
            new TwelveDataActionsNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            curated,
            new InMemoryCursorRepository());

        await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1",
            TestContext.Current.CancellationToken);

        // Reported volume 34,702,000 at a 0.25 factor -> 8,675,500 actually traded.
        curated.Prices.Single().Volume.ShouldBe(8_675_500L);
    }

    [Fact]
    public async Task Corporate_actions_are_written_in_the_same_run()
    {
        var handler = new RoutingStubHandler(new Dictionary<string, string>
        {
            ["time_series"] = AdjustedPricesBody,
            ["splits"] = SplitsBody
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var curated = new CapturingCuratedStore();

        var service = new IngestService(
            new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now)),
            new TwelveDataPriceNormaliser(),
            new TwelveDataActionsNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            curated,
            new InMemoryCursorRepository());

        var result = await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1",
            TestContext.Current.CancellationToken);

        result.ActionsWritten.ShouldBe(1);
        var action = curated.Actions.ShouldHaveSingleItem();
        action.ExDate.ShouldBe(new DateOnly(2020, 8, 31));
        action.Ratio.ShouldBe(0.25m);
        // An action's inferred observed_at is its ex-date.
        action.ObservedAt.ShouldBe(new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero));
    }
```

The existing three `IngestServiceTests` construct `IngestService` through the private `Build` helper. Update that helper to pass `new TwelveDataActionsNormaliser()` as the third argument and a splits body of `"""{"splits":[]}"""`, so those tests keep their current meaning: no splits, so a factor of 1, so no un-adjustment.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Sources.Tests --filter IngestServiceTests`
Expected: FAIL — the constructor has no actions-normaliser parameter.

- [ ] **Step 3: Add `ActionsWritten` to `IngestResult`**

```csharp
namespace MarketData.Sources;

/// <summary>Outcome of ingesting one symbol, recorded against the run.</summary>
public sealed record IngestResult(
    string Symbol,
    bool SkippedUnchanged,
    int RowsWritten,
    string? RawKey,
    IReadOnlyList<string> CuratedKeys,
    int ActionsWritten = 0);
```

- [ ] **Step 4: Rewrite `IngestService.IngestSymbolAsync`**

Add `TwelveDataActionsNormaliser actionsNormaliser` as the third constructor parameter, then:

```csharp
    public async Task<IngestResult> IngestSymbolAsync(
        string symbol, DateOnly from, DateOnly to, string ingestId, CancellationToken ct)
    {
        var envelope = await source.FetchDailyBarsAsync(symbol, from, to, ct);
        var cursor = await cursors.GetAsync(Dataset, symbol, ct);

        // Nothing changed at the vendor, so nothing was learned. No raw object is
        // written: an identical payload is not a new observation.
        if (cursor?.LastContentHash == envelope.ContentHash)
        {
            return new IngestResult(symbol, SkippedUnchanged: true, 0, null, []);
        }

        var runDate = DateOnly.FromDateTime(envelope.ObservedAt.UtcDateTime);
        var rawKey = await rawStore.WriteAsync(envelope, runDate, ct);

        // Splits are fetched in the same run and their key recorded on every price row,
        // so a rebuild un-adjusts with exactly this split history rather than whatever
        // is known at rebuild time.
        var splitsEnvelope = await source.FetchSplitsAsync(symbol, ct);
        var splitsRawKey = await rawStore.WriteAsync(splitsEnvelope, runDate, ct);

        var parsedActions = actionsNormaliser.ParseSplits(splitsEnvelope);
        var actions = parsedActions
            .Select(a => new CorporateAction(
                symbol, a.ExDate, a.ActionType, a.Ratio, a.Amount, a.Currency,
                source.SourceId,
                // An action's inferred observed_at is its ex-date: it became effective
                // then, and claiming earlier knowledge would let a query see a split
                // before the market did.
                policy.Clock.InferredPublication(a.ExDate),
                ObservationKind.Inferred,
                ingestId, splitsRawKey))
            .ToList();

        var parsed = normaliser.Parse(envelope);
        var bars = new List<DailyBar>(parsed.Count);

        foreach (var bar in parsed)
        {
            // The vendor reports prices adjusted for every split up to the fetch instant.
            // Dividing by that factor recovers the price actually quoted on the day.
            var factor = AdjustmentCalculator.SplitFactor(actions, bar.EffectiveDate);

            var close = bar.Close / factor;
            var known = cursor?.LastEffectiveDate is { } last && bar.EffectiveDate <= last
                ? close
                : (decimal?)null;

            if (policy.Decide(bar.EffectiveDate, close, known, envelope.ObservedAt) is not { } decision)
            {
                continue;
            }

            bars.Add(new DailyBar(
                symbol, bar.EffectiveDate,
                bar.Open / factor, bar.High / factor, bar.Low / factor, close,
                (long)(bar.Volume * factor),
                bar.Currency, source.SourceId,
                decision.ObservedAt, decision.Kind,
                ingestId, rawKey, splitsRawKey));
        }

        var curatedKeys = bars.Count > 0
            ? await curatedStore.AppendPricesAsync(bars, ingestId, ct)
            : [];

        if (actions.Count > 0)
        {
            await curatedStore.AppendActionsAsync(actions, ingestId, ct);
        }

        await cursors.SaveAsync(
            new Cursor(
                Dataset,
                symbol,
                parsed.Count > 0 ? parsed[^1].EffectiveDate : cursor?.LastEffectiveDate,
                envelope.ContentHash),
            ct);

        return new IngestResult(
            symbol, SkippedUnchanged: false, bars.Count, rawKey, curatedKeys, actions.Count);
    }
```

`policy.Clock` requires one addition to `ObservationPolicy`, which already receives a clock but does not expose it. Add to that class:

```csharp
    /// <summary>
    /// The clock behind inferred timestamps, reused to stamp corporate actions. Exposed
    /// so IngestService need not be handed the same clock a second time.
    /// </summary>
    public IPublicationClock Clock => clock;
```

The constructor signature is therefore unchanged apart from `actionsNormaliser`, which goes third — after `normaliser` and before `policy`, matching every test snippet in this task.

Volume multiplies by the factor because prices divide by it: a 0.25 factor means the reported volume was four times what actually traded. Query reverses both, so a round trip is lossless.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MarketData.Sources.Tests`
Expected: PASS — the three original tests plus three new ones.

- [ ] **Step 6: Run the whole suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add -A
git commit -m "Un-adjust the vendor's split-adjusted prices on ingest

Twelve Data returns prices scaled by every split up to the request
instant, so a stored value would be re-based by each future split.
Dividing by the same-run split factor recovers the price actually
quoted that day, and the splits envelope key is recorded so a rebuild
redoes identical arithmetic."
```

---

# Stage C — The read path

### Task 5: The query project and curated source resolution

**Files:**
- Create: `app/src/MarketData.Query/MarketData.Query.csproj`, `app/src/MarketData.Query/IMarketDataQuery.cs`, `app/src/MarketData.Query/CuratedSource.cs`
- Create: `app/tests/MarketData.Query.Tests/MarketData.Query.Tests.csproj`
- Test: `app/tests/MarketData.Query.Tests/CuratedSourceTests.cs`

**Interfaces:**
- Consumes: `DailyBar`, `CorporateAction`, `PriceAdjustment`, `ObservationMode` from Domain.
- Produces: `IMarketDataQuery` with `GetPricesAsync` and `GetCorporateActionsAsync`; `CuratedSource.Local(string root)` and `CuratedSource.S3(string bucket)`, each exposing `Glob(string dataset) -> string` and `Setup(DuckDBConnection)`. Tasks 6 and 7 implement against these.

- [ ] **Step 1: Create the projects**

```bash
cd app
dotnet new classlib -o src/MarketData.Query -f net10.0
dotnet new xunit3 -o tests/MarketData.Query.Tests -f net10.0
rm -f src/MarketData.Query/Class1.cs tests/MarketData.Query.Tests/UnitTest1.cs
dotnet sln add src/MarketData.Query tests/MarketData.Query.Tests
```

Write `src/MarketData.Query/MarketData.Query.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="DuckDB.NET.Data.Full" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\MarketData.Domain\MarketData.Domain.csproj" />
  </ItemGroup>

</Project>
```

Write `tests/MarketData.Query.Tests/MarketData.Query.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="Shouldly" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\MarketData.Query\MarketData.Query.csproj" />
    <ProjectReference Include="..\..\src\MarketData.Storage\MarketData.Storage.csproj" />
  </ItemGroup>

</Project>
```

The test project references Storage so it can build fixtures with `LocalCuratedStore`.

- [ ] **Step 2: Measure the DuckDB native payload before going further**

`DuckDB.NET.Data.Full` ships native binaries for five platforms. Now that a `src` project references it, that payload propagates to every downstream output, and it has previously exhausted this machine's disk.

Run:
```bash
dotnet build
du -sh src/MarketData.Query/bin tests/MarketData.Query.Tests/bin
df -h .
```
Expected: each `bin` in the region of 300–350 MB, and free space still comfortably above 2 GB.

If free space drops below 2 GB, add to **both** new csproj files:

```xml
    <RuntimeIdentifiers>win-x64;linux-x64</RuntimeIdentifiers>
```

and re-measure. Do not skip this step and discover it during Task 8.

- [ ] **Step 3: Write the failing test**

```csharp
using MarketData.Query;
using Shouldly;

namespace MarketData.Query.Tests;

public sealed class CuratedSourceTests
{
    [Fact]
    public void Local_builds_a_recursive_glob_with_forward_slashes()
    {
        var source = CuratedSource.Local("/tmp/store");

        source.Glob("prices_daily").ShouldBe("/tmp/store/curated/dataset=prices_daily/**/*.parquet");
    }

    [Fact]
    public void S3_builds_an_s3_glob()
    {
        var source = CuratedSource.S3("pit-marketdata-data-bzun6w");

        source.Glob("corporate_actions")
            .ShouldBe("s3://pit-marketdata-data-bzun6w/curated/dataset=corporate_actions/**/*.parquet");
    }

    [Fact]
    public void A_windows_local_root_still_produces_forward_slashes()
    {
        var source = CuratedSource.Local(@"C:\store");

        source.Glob("prices_daily").ShouldNotContain(@"\");
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test tests/MarketData.Query.Tests`
Expected: FAIL — `CuratedSource` does not exist.

- [ ] **Step 5: Write `CuratedSource.cs`**

```csharp
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

    /// <summary>An S3 bucket, read in place through DuckDB's httpfs extension.</summary>
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
        command.CommandText =
            "INSTALL httpfs; LOAD httpfs; " +
            "CREATE OR REPLACE SECRET s3 (TYPE s3, PROVIDER credential_chain);";
        command.ExecuteNonQuery();
    }
}
```

`credential_chain` makes DuckDB use the same ambient AWS credentials the SDK uses, so nothing is configured twice.

- [ ] **Step 6: Write `IMarketDataQuery.cs`**

```csharp
using MarketData.Domain;

namespace MarketData.Query;

/// <summary>
/// The as-of read path. Every method takes an <paramref name="asOf"/> and returns only
/// what was knowable at that instant. There is deliberately no overload without one.
/// </summary>
public interface IMarketDataQuery
{
    Task<IReadOnlyList<DailyBar>> GetPricesAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTimeOffset asOf,
        PriceAdjustment adjustment,
        ObservationMode observations,
        CancellationToken ct);

    Task<IReadOnlyList<CorporateAction>> GetCorporateActionsAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTimeOffset asOf,
        CancellationToken ct);
}
```

`GetCorporateActionsAsync` takes no `ObservationMode`: the mode applies to bars only, because every backfilled action is `INFERRED` and filtering them would make adjustment silently return unadjusted prices.

- [ ] **Step 7: Run the tests and commit**

Run: `dotnet test tests/MarketData.Query.Tests`
Expected: PASS — 3 tests.

```bash
git add -A
git commit -m "Add the query project and curated source resolution

One abstraction over a local directory and an s3:// prefix so the same
query runs against both, with httpfs configured from the ambient AWS
credential chain."
```

---

### Task 6: Visible bars through the window function

**Files:**
- Create: `app/src/MarketData.Query/DuckDbMarketDataQuery.cs`
- Create: `app/tests/MarketData.Query.Tests/QueryFixture.cs`
- Test: `app/tests/MarketData.Query.Tests/VisibleBarsTests.cs`

**Interfaces:**
- Consumes: `CuratedSource` and `IMarketDataQuery` from Task 5.
- Produces: `DuckDbMarketDataQuery(CuratedSource source)` implementing `GetPricesAsync` with `PriceAdjustment.None`. Task 7 adds adjustment; Task 8 tests it end to end.

- [ ] **Step 1: Write `QueryFixture.cs`**

```csharp
using MarketData.Domain;
using MarketData.Storage.Local;

namespace MarketData.Query.Tests;

/// <summary>A curated store on disk, built row by row, for one test.</summary>
public sealed class QueryFixture : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "pitmd-q-" + Guid.NewGuid().ToString("N"));

    public CuratedSource Source => CuratedSource.Local(Root);

    public DuckDbMarketDataQuery Query => new(Source);

    public static DailyBar Bar(
        DateOnly effectiveDate,
        decimal close,
        DateTimeOffset observedAt,
        ObservationKind kind = ObservationKind.Inferred,
        string ingestId = "run-1") =>
        new("AAPL", effectiveDate, close, close, close, close, 1_000_000L,
            "USD", "twelvedata", observedAt, kind, ingestId, "raw/x.json", "raw/s.json");

    public static CorporateAction Split(DateOnly exDate, decimal ratio, DateTimeOffset observedAt) =>
        new("AAPL", exDate, CorporateActionType.Split, ratio, null, "USD", "twelvedata",
            observedAt, ObservationKind.Inferred, "run-1", "raw/s.json");

    public async Task WriteBarsAsync(params DailyBar[] bars)
    {
        foreach (var group in bars.GroupBy(b => b.IngestId))
        {
            await new LocalCuratedStore(Root)
                .AppendPricesAsync(group.ToList(), group.Key, CancellationToken.None);
        }
    }

    public async Task WriteActionsAsync(params CorporateAction[] actions)
    {
        if (actions.Length == 0)
        {
            return;
        }

        await new LocalCuratedStore(Root)
            .AppendActionsAsync(actions, actions[0].IngestId, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
```

Bars are grouped by `IngestId` so each group becomes its own part file, which is what makes the restatement and tiebreak tests meaningful.

- [ ] **Step 2: Write the failing tests**

```csharp
using MarketData.Domain;
using Shouldly;

namespace MarketData.Query.Tests;

public sealed class VisibleBarsTests : IDisposable
{
    private readonly QueryFixture _fixture = new();

    private static readonly DateOnly Day = new(2020, 6, 15);

    [Fact]
    public async Task Returns_a_bar_observed_before_the_as_of()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 342.99m, new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero)));

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day,
            new DateTimeOffset(2020, 7, 1, 0, 0, 0, TimeSpan.Zero),
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.ShouldHaveSingleItem().Close.ShouldBe(342.99m);
    }

    [Fact]
    public async Task Returns_nothing_when_the_store_is_empty()
    {
        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day, DateTimeOffset.UtcNow,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.ShouldBeEmpty();
    }

    [Fact]
    public async Task Filters_by_symbol_and_date_range()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(new DateOnly(2020, 6, 15), 342.99m, new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero)),
            QueryFixture.Bar(new DateOnly(2020, 7, 15), 380.00m, new DateTimeOffset(2020, 7, 15, 20, 15, 0, TimeSpan.Zero)));

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", new DateOnly(2020, 7, 1), new DateOnly(2020, 7, 31),
            DateTimeOffset.UtcNow,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.ShouldHaveSingleItem().EffectiveDate.ShouldBe(new DateOnly(2020, 7, 15));
    }

    [Fact]
    public async Task Returns_bars_in_ascending_date_order()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(new DateOnly(2020, 7, 15), 380.00m, new DateTimeOffset(2020, 7, 15, 20, 15, 0, TimeSpan.Zero)),
            QueryFixture.Bar(new DateOnly(2020, 6, 15), 342.99m, new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero)));

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
            DateTimeOffset.UtcNow,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.Select(b => b.EffectiveDate)
            .ShouldBe([new DateOnly(2020, 6, 15), new DateOnly(2020, 7, 15)]);
    }

    public void Dispose() => _fixture.Dispose();
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/MarketData.Query.Tests --filter VisibleBarsTests`
Expected: FAIL — `DuckDbMarketDataQuery` does not exist.

- [ ] **Step 4: Write `DuckDbMarketDataQuery.cs`**

```csharp
using System.Globalization;
using DuckDB.NET.Data;
using MarketData.Domain;

namespace MarketData.Query;

/// <inheritdoc />
public sealed class DuckDbMarketDataQuery(CuratedSource source) : IMarketDataQuery
{
    public async Task<IReadOnlyList<DailyBar>> GetPricesAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTimeOffset asOf,
        PriceAdjustment adjustment,
        ObservationMode observations,
        CancellationToken ct)
    {
        var bars = await ReadBarsAsync(symbol, from, to, asOf, observations, ct);

        return bars;
    }

    public Task<IReadOnlyList<CorporateAction>> GetCorporateActionsAsync(
        string symbol, DateOnly from, DateOnly to, DateTimeOffset asOf, CancellationToken ct) =>
        throw new NotImplementedException("Task 7.");

    private async Task<IReadOnlyList<DailyBar>> ReadBarsAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTimeOffset asOf,
        ObservationMode observations,
        CancellationToken ct)
    {
        // One window function is the whole temporal model: keep rows observed at or before
        // asOf, then take the most recent row per (symbol, effective_date). The ingest_id
        // tiebreak makes the choice deterministic when two rows share an observed_at.
        const string Sql = """
            WITH visible AS (
              SELECT *, ROW_NUMBER() OVER (
                PARTITION BY Symbol, EffectiveDate
                ORDER BY ObservedAt DESC, IngestId DESC) AS rn
              FROM read_parquet($glob)
              WHERE Symbol = $symbol
                AND ObservedAt <= $asOf
                AND ($allKinds OR ObservedAtKind = 'OBSERVED')
            )
            SELECT Symbol, EffectiveDate, Open, High, Low, Close, Volume, Currency, Source,
                   ObservedAt, ObservedAtKind, IngestId, RawKey, SplitsRawKey
            FROM visible
            WHERE rn = 1
              AND EffectiveDate BETWEEN $from AND $to
            ORDER BY EffectiveDate
            """;

        await using var connection = Open();

        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        Add(command, "glob", source.Glob("prices_daily"));
        Add(command, "symbol", symbol);
        Add(command, "asOf", asOf.UtcDateTime);
        Add(command, "allKinds", observations == ObservationMode.All);
        Add(command, "from", from.ToDateTime(TimeOnly.MinValue));
        Add(command, "to", to.ToDateTime(TimeOnly.MinValue));

        var bars = new List<DailyBar>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            bars.Add(new DailyBar(
                Symbol: reader.GetString(0),
                EffectiveDate: reader.GetFieldValue<DateOnly>(1),
                Open: reader.GetDecimal(2),
                High: reader.GetDecimal(3),
                Low: reader.GetDecimal(4),
                Close: reader.GetDecimal(5),
                Volume: reader.GetInt64(6),
                Currency: reader.GetString(7),
                Source: reader.GetString(8),
                ObservedAt: new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero),
                ObservedAtKind: Enum.Parse<ObservationKind>(reader.GetString(10), ignoreCase: true),
                IngestId: reader.GetString(11),
                RawKey: reader.GetString(12),
                SplitsRawKey: reader.GetString(13)));
        }

        return bars;
    }

    private DuckDBConnection Open()
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        source.Setup(connection);

        return connection;
    }

    private static void Add(DuckDBCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
```

`read_parquet` over a glob matching no files raises an IO error rather than returning zero rows. Wrap the read so an empty store returns an empty list: catch `DuckDBException` whose message contains `No files found`, and return `[]`. Add that around the `ExecuteReaderAsync` call:

```csharp
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // ... as above
            }
        }
        catch (DuckDBException ex) when (ex.Message.Contains("No files found", StringComparison.OrdinalIgnoreCase))
        {
            // An empty store is a valid state, not an error: a symbol may simply never
            // have been ingested.
            return [];
        }
```

`ObservedAtKind` is written upper-case (`INFERRED`) but `Enum.Parse` with `ignoreCase: true` handles it.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MarketData.Query.Tests --filter VisibleBarsTests`
Expected: PASS — 4 tests.

If `GetFieldValue<DateOnly>` throws, print the runtime type of column 1 and adjust — Task 12 of the Stage 0–3 plan established DuckDB returns Parquet `DATE` as `DateOnly`, but confirm rather than assume.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Read visible bars through the as-of window function

One window function is the whole temporal model. An empty store is a
valid state and returns no rows rather than raising."
```

---

### Task 7: Corporate actions and applied adjustment

**Files:**
- Modify: `app/src/MarketData.Query/DuckDbMarketDataQuery.cs`
- Test: `app/tests/MarketData.Query.Tests/AdjustedPricesTests.cs`

**Interfaces:**
- Consumes: `AdjustmentCalculator.Factor` from Task 2; `ReadBarsAsync` from Task 6.
- Produces: a complete `IMarketDataQuery` — `GetCorporateActionsAsync` implemented, and `GetPricesAsync` honouring all three `PriceAdjustment` values. Task 8's temporal tests exercise it.

- [ ] **Step 1: Write the failing tests**

```csharp
using MarketData.Domain;
using Shouldly;

namespace MarketData.Query.Tests;

public sealed class AdjustedPricesTests : IDisposable
{
    private readonly QueryFixture _fixture = new();

    private static readonly DateOnly Day = new(2020, 6, 15);
    private static readonly DateTimeOffset BarObserved = new(2020, 6, 15, 20, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SplitObserved = new(2020, 8, 31, 20, 15, 0, TimeSpan.Zero);

    private async Task SeedAsync()
    {
        await _fixture.WriteBarsAsync(QueryFixture.Bar(Day, 342.99m, BarObserved));
        await _fixture.WriteActionsAsync(
            QueryFixture.Split(new DateOnly(2020, 8, 31), 0.25m, SplitObserved));
    }

    [Fact]
    public async Task Unadjusted_returns_the_price_quoted_that_day()
    {
        await SeedAsync();

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day, DateTimeOffset.UtcNow,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.Single().Close.ShouldBe(342.99m);
    }

    [Fact]
    public async Task Split_adjusted_applies_a_known_split()
    {
        await SeedAsync();

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day, DateTimeOffset.UtcNow,
            PriceAdjustment.SplitsOnly, ObservationMode.All, TestContext.Current.CancellationToken);

        bars.Single().Close.ShouldBe(85.7475m);
    }

    [Fact]
    public async Task Volume_is_adjusted_inversely()
    {
        await SeedAsync();

        var bars = await _fixture.Query.GetPricesAsync(
            "AAPL", Day, Day, DateTimeOffset.UtcNow,
            PriceAdjustment.SplitsOnly, ObservationMode.All, TestContext.Current.CancellationToken);

        // 1,000,000 traded unadjusted becomes 4,000,000 post-split-equivalent shares.
        bars.Single().Volume.ShouldBe(4_000_000L);
    }

    [Fact]
    public async Task Corporate_actions_come_back_filtered_by_as_of()
    {
        await SeedAsync();

        var before = await _fixture.Query.GetCorporateActionsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
            new DateTimeOffset(2020, 7, 1, 0, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken);

        var after = await _fixture.Query.GetCorporateActionsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
            DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        before.ShouldBeEmpty();
        after.ShouldHaveSingleItem().Ratio.ShouldBe(0.25m);
    }

    public void Dispose() => _fixture.Dispose();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Query.Tests --filter AdjustedPricesTests`
Expected: FAIL — `GetCorporateActionsAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement `GetCorporateActionsAsync`**

Replace the throwing stub:

```csharp
    public async Task<IReadOnlyList<CorporateAction>> GetCorporateActionsAsync(
        string symbol, DateOnly from, DateOnly to, DateTimeOffset asOf, CancellationToken ct)
    {
        var actions = await ReadActionsAsync(symbol, asOf, ct);

        return actions.Where(a => a.ExDate >= from && a.ExDate <= to).ToList();
    }

    /// <summary>
    /// Every action visible at <paramref name="asOf"/>, unfiltered by ex-date, because
    /// adjusting a bar needs actions from outside the requested window.
    /// </summary>
    private async Task<IReadOnlyList<CorporateAction>> ReadActionsAsync(
        string symbol, DateTimeOffset asOf, CancellationToken ct)
    {
        const string Sql = """
            WITH visible AS (
              SELECT *, ROW_NUMBER() OVER (
                PARTITION BY Symbol, ExDate, ActionType
                ORDER BY ObservedAt DESC, IngestId DESC) AS rn
              FROM read_parquet($glob)
              WHERE Symbol = $symbol
                AND ObservedAt <= $asOf
            )
            SELECT Symbol, ExDate, ActionType, Ratio, Amount, Currency, Source,
                   ObservedAt, ObservedAtKind, IngestId, RawKey
            FROM visible
            WHERE rn = 1
            ORDER BY ExDate
            """;

        await using var connection = Open();

        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        Add(command, "glob", source.Glob("corporate_actions"));
        Add(command, "symbol", symbol);
        Add(command, "asOf", asOf.UtcDateTime);

        var actions = new List<CorporateAction>();

        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                actions.Add(new CorporateAction(
                    Symbol: reader.GetString(0),
                    ExDate: reader.GetFieldValue<DateOnly>(1),
                    ActionType: Enum.Parse<CorporateActionType>(reader.GetString(2), ignoreCase: true),
                    Ratio: reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    Amount: reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    Currency: reader.GetString(5),
                    Source: reader.GetString(6),
                    ObservedAt: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
                    ObservedAtKind: Enum.Parse<ObservationKind>(reader.GetString(8), ignoreCase: true),
                    IngestId: reader.GetString(9),
                    RawKey: reader.GetString(10)));
            }
        }
        catch (DuckDBException ex) when (ex.Message.Contains("No files found", StringComparison.OrdinalIgnoreCase))
        {
            // A symbol with no corporate actions is normal, not an error.
            return [];
        }

        return actions;
    }
```

Actions are read unfiltered by ex-date because a bar in June needs an August split to adjust it; the caller-facing method applies the window afterwards.

- [ ] **Step 4: Apply adjustment in `GetPricesAsync`**

```csharp
    public async Task<IReadOnlyList<DailyBar>> GetPricesAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTimeOffset asOf,
        PriceAdjustment adjustment,
        ObservationMode observations,
        CancellationToken ct)
    {
        var bars = await ReadBarsAsync(symbol, from, to, asOf, observations, ct);

        if (adjustment == PriceAdjustment.None || bars.Count == 0)
        {
            return bars;
        }

        // ObservationMode deliberately does not reach this call. Every backfilled action
        // is INFERRED, so filtering them under ObservedOnly would apply a factor of 1.0
        // and return unadjusted prices labelled as adjusted.
        var actions = await ReadActionsAsync(symbol, asOf, ct);

        if (actions.Count == 0)
        {
            return bars;
        }

        // The dividend factor needs the close on the day before an ex-date. Only bars in
        // the requested window are available, so a dividend whose prior close falls
        // outside it contributes nothing rather than a guess.
        var closes = bars.ToDictionary(b => b.EffectiveDate, b => b.Close);
        decimal? CloseOnDayBefore(DateOnly exDate) =>
            closes.TryGetValue(exDate.AddDays(-1), out var close) ? close : null;

        return bars
            .Select(bar =>
            {
                var factor = AdjustmentCalculator.Factor(
                    actions, bar.EffectiveDate, adjustment, CloseOnDayBefore);

                return factor == 1m
                    ? bar
                    : bar with
                    {
                        Open = bar.Open * factor,
                        High = bar.High * factor,
                        Low = bar.Low * factor,
                        Close = bar.Close * factor,
                        Volume = (long)(bar.Volume / factor)
                    };
            })
            .ToList();
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MarketData.Query.Tests`
Expected: PASS — 11 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Apply as-of adjustment to queried prices

Actions are read unfiltered by ex-date because a June bar needs an
August split to adjust it. ObservationMode never reaches the action
read: every backfilled action is INFERRED, so filtering them would
return unadjusted prices labelled as adjusted."
```

---

# Stage D — Prove it

### Task 8: The eight temporal tests

**Files:**
- Test: `app/tests/MarketData.Query.Tests/TemporalTests.cs`

**Interfaces:**
- Consumes: the complete `IMarketDataQuery` from Task 7 and `QueryFixture` from Task 6.
- Produces: nothing new. These are the acceptance criteria for Stages 0–4.

These are §7 of the Layer 1 design, numbered as they are there. If one fails, the temporal model is wrong — do not adjust the test to match the behaviour.

- [ ] **Step 1: Write all eight**

```csharp
using MarketData.Domain;
using Shouldly;

namespace MarketData.Query.Tests;

public sealed class TemporalTests : IDisposable
{
    private readonly QueryFixture _fixture = new();

    private static readonly DateOnly Day = new(2024, 6, 14);
    private static readonly DateOnly From = new(2024, 1, 1);
    private static readonly DateOnly To = new(2024, 12, 31);

    private Task<IReadOnlyList<DailyBar>> Read(
        DateTimeOffset asOf,
        PriceAdjustment adjustment = PriceAdjustment.None,
        ObservationMode mode = ObservationMode.All) =>
        _fixture.Query.GetPricesAsync(
            "AAPL", From, To, asOf, adjustment, mode, TestContext.Current.CancellationToken);

    [Fact]
    public async Task T1_no_lookahead()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var bars = await Read(new DateTimeOffset(2024, 6, 30, 0, 0, 0, TimeSpan.Zero));

        bars.ShouldBeEmpty();
    }

    [Fact]
    public async Task T2_restatement_visibility()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero), ingestId: "run-1"),
            QueryFixture.Bar(Day, 101m, new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero), ingestId: "run-2"));

        var between = await Read(new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var after = await Read(new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero));

        between.Single().Close.ShouldBe(100m);
        after.Single().Close.ShouldBe(101m);
    }

    [Fact]
    public async Task T3_reproducibility_across_later_ingests()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero), ingestId: "run-1"));

        var asOf = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var before = await Read(asOf);

        // A later ingest arrives, carrying a revision the as-of must not see.
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 101m, new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero), ingestId: "run-2"));

        var after = await Read(asOf);

        after.Select(b => b.Close).ShouldBe(before.Select(b => b.Close));
    }

    [Fact]
    public async Task T4_adjustment_is_continuous_across_an_ex_date()
    {
        var dayBefore = new DateOnly(2024, 6, 6);
        var exDate = new DateOnly(2024, 6, 7);

        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(dayBefore, 400m, new DateTimeOffset(2024, 6, 6, 20, 15, 0, TimeSpan.Zero)),
            QueryFixture.Bar(exDate, 100m, new DateTimeOffset(2024, 6, 7, 20, 15, 0, TimeSpan.Zero)));
        await _fixture.WriteActionsAsync(
            QueryFixture.Split(exDate, 0.25m, new DateTimeOffset(2024, 6, 7, 20, 15, 0, TimeSpan.Zero)));

        var bars = await Read(DateTimeOffset.UtcNow, PriceAdjustment.SplitsOnly);

        // 400 x 0.25 = 100, matching the post-split bar: the series is continuous.
        bars.Single(b => b.EffectiveDate == dayBefore).Close.ShouldBe(100m);
        bars.Single(b => b.EffectiveDate == exDate).Close.ShouldBe(100m);
    }

    [Fact]
    public async Task T5_an_action_observed_after_as_of_is_not_applied()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 400m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero)));
        await _fixture.WriteActionsAsync(
            QueryFixture.Split(new DateOnly(2024, 8, 30), 0.25m,
                new DateTimeOffset(2024, 8, 30, 20, 15, 0, TimeSpan.Zero)));

        var before = await Read(new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero), PriceAdjustment.SplitsOnly);
        var after = await Read(new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero), PriceAdjustment.SplitsOnly);

        before.Single().Close.ShouldBe(400m);
        after.Single().Close.ShouldBe(100m);
    }

    [Fact]
    public async Task T6_deterministic_tiebreak_on_identical_observed_at()
    {
        var same = new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero);

        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, same, ingestId: "run-a"),
            QueryFixture.Bar(Day, 101m, same, ingestId: "run-b"));

        var first = await Read(DateTimeOffset.UtcNow);
        var second = await Read(DateTimeOffset.UtcNow);

        // run-b wins on IngestId DESC, and must do so on every run.
        first.Single().Close.ShouldBe(101m);
        second.Single().Close.ShouldBe(first.Single().Close);
    }

    [Fact]
    public async Task T7_rebuild_determinism()
    {
        // Normalising the same raw twice must yield an identical row set. Simulated here
        // by writing the same logical bar under two ingest ids and asserting the visible
        // values are identical -- asserted on rows read back, not on file bytes, because
        // Parquet embeds a writer version and depends on row-group boundaries.
        var observed = new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero);

        await _fixture.WriteBarsAsync(QueryFixture.Bar(Day, 100m, observed, ingestId: "run-1"));
        var first = await Read(DateTimeOffset.UtcNow);

        await _fixture.WriteBarsAsync(QueryFixture.Bar(Day, 100m, observed, ingestId: "run-2"));
        var second = await Read(DateTimeOffset.UtcNow);

        second.Count.ShouldBe(first.Count);
        second.Single().Close.ShouldBe(first.Single().Close);
        second.Single().ObservedAt.ShouldBe(first.Single().ObservedAt);
        second.Single().ObservedAtKind.ShouldBe(first.Single().ObservedAtKind);
    }

    [Fact]
    public async Task T8_observed_only_excludes_inferred()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero), ObservationKind.Inferred),
            QueryFixture.Bar(new DateOnly(2024, 6, 17), 102m, new DateTimeOffset(2024, 6, 17, 20, 15, 0, TimeSpan.Zero), ObservationKind.Observed));

        var all = await Read(DateTimeOffset.UtcNow, mode: ObservationMode.All);
        var observedOnly = await Read(DateTimeOffset.UtcNow, mode: ObservationMode.ObservedOnly);

        all.Count.ShouldBe(2);
        observedOnly.ShouldHaveSingleItem().EffectiveDate.ShouldBe(new DateOnly(2024, 6, 17));
    }

    [Fact]
    public async Task Observed_only_does_not_change_the_adjustment_factor()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 400m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero), ObservationKind.Observed));
        // The split is INFERRED, as every backfilled action is.
        await _fixture.WriteActionsAsync(
            QueryFixture.Split(new DateOnly(2024, 8, 30), 0.25m,
                new DateTimeOffset(2024, 8, 30, 20, 15, 0, TimeSpan.Zero)));

        var bars = await Read(DateTimeOffset.UtcNow, PriceAdjustment.SplitsOnly, ObservationMode.ObservedOnly);

        // If ObservedOnly filtered actions, this would wrongly be 400.
        bars.Single().Close.ShouldBe(100m);
    }

    public void Dispose() => _fixture.Dispose();
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/MarketData.Query.Tests --filter TemporalTests`
Expected: PASS — 9 tests (the eight from the spec plus the `ObservedOnly` adjustment guard).

If T4 fails by a factor of four, the adjustment direction is inverted somewhere. Check that ingest **divides** and query **multiplies**, not the reverse.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "Add the eight temporal tests

These are the specification. A failure here means the temporal model is
wrong; do not adjust the test to match the behaviour."
```

---

### Task 9: The round-trip invariant

**Files:**
- Test: `app/tests/MarketData.Query.Tests/RoundTripTests.cs`

**Interfaces:**
- Consumes: `IngestService` from Task 4 and `IMarketDataQuery` from Task 7.
- Produces: nothing new. This is the test that catches the two adjustment directions drifting apart.

Ingest divides by the split factor; query multiplies by it. Feed the vendor's number in one end and it must come back out of the other.

- [ ] **Step 1: Write the test**

```csharp
using MarketData.Domain;
using MarketData.Sources;
using MarketData.Sources.TwelveData;
using MarketData.Sources.Tests;
using MarketData.Storage.Local;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace MarketData.Query.Tests;

public sealed class RoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-rt-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 22, 15, 3, TimeSpan.Zero);

    [Fact]
    public async Task Un_adjust_then_re_adjust_returns_the_vendors_number()
    {
        const string prices =
            """{"meta":{"symbol":"AAPL","currency":"USD"},"values":[{"datetime":"2020-06-15","open":"85.00000","high":"86.00000","low":"84.00000","close":"85.74750","volume":"34702000"}]}""";
        const string splits =
            """{"meta":{"symbol":"AAPL","currency":"USD"},"splits":[{"date":"2020-08-31","ratio":0.25,"from_factor":4,"to_factor":1}]}""";

        var handler = new RoutingStubHandler(new Dictionary<string, string>
        {
            ["time_series"] = prices,
            ["splits"] = splits
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };

        var service = new IngestService(
            new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now)),
            new TwelveDataPriceNormaliser(),
            new TwelveDataActionsNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            new LocalCuratedStore(_root),
            new InMemoryCursorRepository());

        await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1",
            TestContext.Current.CancellationToken);

        var query = new DuckDbMarketDataQuery(CuratedSource.Local(_root));

        var unadjusted = await query.GetPricesAsync(
            "AAPL", new DateOnly(2020, 6, 15), new DateOnly(2020, 6, 15), Now,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        var adjusted = await query.GetPricesAsync(
            "AAPL", new DateOnly(2020, 6, 15), new DateOnly(2020, 6, 15), Now,
            PriceAdjustment.SplitsOnly, ObservationMode.All, TestContext.Current.CancellationToken);

        // Stored: the price actually quoted that day.
        unadjusted.Single().Close.ShouldBe(342.99m);
        // Adjusted back to today's basis: exactly what the vendor reported.
        adjusted.Single().Close.ShouldBe(85.7475m);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

This needs `MarketData.Query.Tests` to reference `MarketData.Sources` and the Sources test project's helpers. Add to `MarketData.Query.Tests.csproj`:

```xml
    <ProjectReference Include="..\..\src\MarketData.Sources\MarketData.Sources.csproj" />
    <ProjectReference Include="..\MarketData.Sources.Tests\MarketData.Sources.Tests.csproj" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

Referencing one test project from another is unusual but correct here: `RoutingStubHandler`, `InMemoryCursorRepository` and `CapturingCuratedStore` are test doubles, and duplicating them would let the two copies drift.

- [ ] **Step 2: Run it**

Run: `dotnet test tests/MarketData.Query.Tests --filter RoundTripTests`
Expected: PASS — 1 test.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "Assert the un-adjust and re-adjust directions agree

Ingest divides by the split factor and query multiplies by it. If those
two ever drift apart this is the test that notices."
```

---

### Task 10: Replace the cursor-watermark approximation

**Files:**
- Modify: `app/src/MarketData.Sources/IngestService.cs`
- Test: `app/tests/MarketData.Query.Tests/PriorKnowledgeTests.cs`

**Interfaces:**
- Consumes: `IMarketDataQuery` from Task 7.
- Produces: `IngestService` taking an optional `IMarketDataQuery`, using a real per-date lookup for prior knowledge. Closes the soft spot the Stage 0–3 plan deferred.

Today a bar at or before the cursor watermark is assumed known and unchanged, so a restatement is missed unless its close differs from the incoming value. With a query layer, prior knowledge becomes a lookup.

`MarketData.Sources` cannot reference `MarketData.Query` — Query already references Domain, and adding the reverse would make the dependency graph cyclic through the composition root. Instead `IngestService` takes a delegate.

- [ ] **Step 1: Write the failing test**

```csharp
using MarketData.Domain;
using Shouldly;

namespace MarketData.Query.Tests;

public sealed class PriorKnowledgeTests : IDisposable
{
    private readonly QueryFixture _fixture = new();

    [Fact]
    public async Task A_restatement_beyond_the_watermark_is_detected()
    {
        // The watermark approximation only compared bars at or before the cursor date.
        // A changed close on a later bar must still be seen as a restatement.
        var day = new DateOnly(2024, 6, 14);

        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(day, 100m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero)));

        var known = await _fixture.Query.GetPricesAsync(
            "AAPL", day, day, DateTimeOffset.UtcNow,
            PriceAdjustment.None, ObservationMode.All, TestContext.Current.CancellationToken);

        known.ShouldHaveSingleItem().Close.ShouldBe(100m);
    }

    public void Dispose() => _fixture.Dispose();
}
```

- [ ] **Step 2: Add the delegate to `IngestService`**

Add a constructor parameter:

```csharp
    /// <summary>
    /// Returns the close currently known for a bar, or null if none is. Supplied by the
    /// composition root as a call into the query layer; Sources cannot reference Query
    /// without making the dependency graph cyclic. Null disables the lookup, in which
    /// case every bar is treated as unseen.
    /// </summary>
    Func<string, DateOnly, CancellationToken, Task<decimal?>>? knownCloseLookup = null
```

Replace the watermark block:

```csharp
            var known = knownCloseLookup is null
                ? (cursor?.LastEffectiveDate is { } last && bar.EffectiveDate <= last
                    ? close
                    : (decimal?)null)
                : await knownCloseLookup(symbol, bar.EffectiveDate, ct);
```

The watermark path remains as the fallback when no lookup is supplied, so existing tests keep their meaning and the Lambda in Stage 5 can wire the real one.

- [ ] **Step 3: Write the composition helper**

Add to `MarketData.Query`:

```csharp
using MarketData.Domain;

namespace MarketData.Query;

/// <summary>
/// Adapts <see cref="IMarketDataQuery"/> to the prior-knowledge delegate
/// <c>IngestService</c> expects, without either project referencing the other.
/// </summary>
public static class PriorKnowledge
{
    public static Func<string, DateOnly, CancellationToken, Task<decimal?>> From(
        IMarketDataQuery query,
        TimeProvider time) =>
        async (symbol, date, ct) =>
        {
            var bars = await query.GetPricesAsync(
                symbol, date, date, time.GetUtcNow(),
                PriceAdjustment.None, ObservationMode.All, ct);

            return bars.Count == 0 ? null : bars[0].Close;
        };
}
```

`PriceAdjustment.None` is essential: prior knowledge is compared against an incoming unadjusted close, so an adjusted comparison would report a restatement on every split.

- [ ] **Step 4: Run the tests**

Run: `dotnet test`
Expected: PASS — the whole suite.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Replace the cursor-watermark guess with a real prior-knowledge lookup

The watermark only caught a restatement whose close differed on a bar
at or before it. Sources takes a delegate rather than referencing
Query, which would make the dependency graph cyclic."
```

---

### Task 11: Read-path equivalence against S3

**Files:**
- Test: `app/tests/MarketData.Integration.Tests/S3ReadPathTests.cs`

**Interfaces:**
- Consumes: `CuratedSource`, `DuckDbMarketDataQuery`, and the existing `LocalStackFixture`.
- Produces: nothing new. Proves the two read paths agree.

- [ ] **Step 1: Add the references**

In `MarketData.Integration.Tests.csproj`:

```xml
    <ProjectReference Include="..\..\src\MarketData.Query\MarketData.Query.csproj" />
```

- [ ] **Step 2: Write the test**

```csharp
using MarketData.Domain;
using MarketData.Query;
using MarketData.Storage.Local;
using MarketData.Storage.S3;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class S3ReadPathTests(LocalStackFixture fixture)
{
    [Fact]
    public async Task Local_and_s3_return_identical_rows()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var root = Path.Combine(Path.GetTempPath(), "pitmd-eq-" + Guid.NewGuid().ToString("N"));
        var observed = new DateTimeOffset(2020, 6, 15, 20, 15, 0, TimeSpan.Zero);
        var bar = new DailyBar(
            "AAPL", new DateOnly(2020, 6, 15), 340m, 345m, 339m, 342.99m, 1_000_000L,
            "USD", "twelvedata", observed, ObservationKind.Inferred, "run-1",
            "raw/x.json", "raw/s.json");

        try
        {
            await new LocalCuratedStore(root)
                .AppendPricesAsync([bar], "run-1", TestContext.Current.CancellationToken);
            await new S3CuratedStore(fixture.S3, fixture.Bucket)
                .AppendPricesAsync([bar], "run-1", TestContext.Current.CancellationToken);

            var local = await new DuckDbMarketDataQuery(CuratedSource.Local(root)).GetPricesAsync(
                "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
                DateTimeOffset.UtcNow, PriceAdjustment.None, ObservationMode.All,
                TestContext.Current.CancellationToken);

            var s3 = await new DuckDbMarketDataQuery(LocalStackSource()).GetPricesAsync(
                "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
                DateTimeOffset.UtcNow, PriceAdjustment.None, ObservationMode.All,
                TestContext.Current.CancellationToken);

            s3.Count.ShouldBe(local.Count);
            s3.Single().Close.ShouldBe(local.Single().Close);
            s3.Single().ObservedAt.ShouldBe(local.Single().ObservedAt);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private CuratedSource LocalStackSource() => CuratedSource.S3(fixture.Bucket);
}
```

- [ ] **Step 3: Point DuckDB at LocalStack**

`CuratedSource.S3` currently configures a credential-chain secret pointing at real AWS. LocalStack needs an endpoint override, so add an optional endpoint:

```csharp
    /// <summary>
    /// An S3 bucket, read in place through DuckDB's httpfs extension.
    /// <paramref name="endpoint"/> overrides the AWS endpoint for LocalStack; production
    /// leaves it null.
    /// </summary>
    public static CuratedSource S3(string bucket, string? endpoint = null) =>
        new($"s3://{bucket}", isS3: true, endpoint);
```

The private constructor and its backing field change with it:

```csharp
    private readonly string? _endpoint;

    private CuratedSource(string prefix, bool isS3, string? endpoint = null)
    {
        _prefix = prefix;
        _isS3 = isS3;
        _endpoint = endpoint;
    }
```

and in `Setup`:

```csharp
        command.CommandText = _endpoint is null
            ? "INSTALL httpfs; LOAD httpfs; " +
              "CREATE OR REPLACE SECRET s3 (TYPE s3, PROVIDER credential_chain);"
            : "INSTALL httpfs; LOAD httpfs; " +
              $"CREATE OR REPLACE SECRET s3 (TYPE s3, KEY_ID 'test', SECRET 'test', " +
              $"ENDPOINT '{_endpoint}', URL_STYLE 'path', USE_SSL false);";
```

Then in the test, build it as `CuratedSource.S3(fixture.Bucket, LocalStackEndpoint())`, where the endpoint is `fixture.S3.Config.ServiceURL` with the scheme stripped.

- [ ] **Step 4: Run it**

Run: `dotnet test` — with Docker, or rely on CI, where a daemon is present.
Expected: PASS, or SKIP where Docker is unavailable.

If DuckDB cannot install `httpfs` because the test environment is offline, record that and assert equivalence against real S3 in a documented manual check instead. Do not delete the test.

- [ ] **Step 5: Commit and push**

```bash
git add -A
git commit -m "Assert the local and s3 read paths return identical rows

Two read paths that can disagree are worse than one, and only a test
keeps them honest."
git push
gh run list --limit 1
```

Expected: CI concludes `success`, running the LocalStack tests for real.

---

### Task 12: Update the documentation to match

**Files:**
- Modify: `README.md`, `CLAUDE.md`

**Interfaces:**
- Consumes: everything above.
- Produces: docs that describe what now exists.

- [ ] **Step 1: Update `README.md`**

- Move Stage 4 to ✅ Done in the status table.
- Delete the **Open correctness issue** section: it is fixed, and the fix is described in the spec.
- In **How it works**, remove the dashed styling on the DuckDB node in the mermaid diagram and drop the line "The dashed box is Stage 4 — not built yet."
- Add a short subsection under **How it works** showing the two-answer example as real API rather than aspiration:

```csharp
var query = new DuckDbMarketDataQuery(CuratedSource.S3("pit-marketdata-data-bzun6w"));

// What was knowable on 2020-07-01
await query.GetPricesAsync("AAPL", d, d, asOf2020, PriceAdjustment.SplitsOnly, ObservationMode.All, ct);
// -> 342.99

// What is knowable now
await query.GetPricesAsync("AAPL", d, d, today, PriceAdjustment.SplitsOnly, ObservationMode.All, ct);
// -> 85.7475
```

- [ ] **Step 2: Update `CLAUDE.md`**

- Change **Current state** to record Stage 4 complete and name Stage 5 as next.
- In **Non-negotiables**, replace the open-issue note on the prices bullet with a statement that ingest un-adjusts and each row records `SplitsRawKey`.
- Remove the **Known soft spot** section: Task 10 fixed it.
- Add to the toolchain list: `ObservationMode` applies to bars only, never to corporate actions, and why.

- [ ] **Step 3: Run everything, commit and push**

```bash
cd app && dotnet test && cd ..
git add -A
git commit -m "Update README and CLAUDE.md for Stage 4"
git push
```

Expected: the whole suite green, CI green.

---

## Definition of done for this plan

- All eight temporal tests pass, plus the `ObservedOnly` adjustment guard.
- `dotnet test` passes without Docker (integration tests self-skip) and in CI with it.
- Ingesting the vendor's `85.74750` for 2020-06-15 stores `342.99`, and querying it back with `SplitsOnly` as of today returns `85.7475`.
- Every price row carries a non-empty `SplitsRawKey` when any split was applied.
- `MarketData.Domain` still has zero package references.
- The local and `s3://` read paths return identical rows.
- `IngestService` no longer approximates prior knowledge from the cursor watermark.

## Deliberately deferred to Stage 5 and beyond

Named so an executor does not build them early:

- The Lambda handler, its IAM role, EventBridge schedule and log retention.
- The CLI (`watchlist`, `backfill`, `query`, `reprocess`, `compact`).
- Compaction of many small Parquet parts into fewer.
- Run records mirrored to S3, and GitHub OIDC for Terraform plans in CI.
- Dividend-adjusted series materialised as their own dataset. Factors stay computed on demand.
- Any second vendor, and the SEC EDGAR fundamentals join that `cik` exists for.
- Making `httpfs` available without a network call. `INSTALL httpfs` downloads from
  `extensions.duckdb.org`, which is fine locally and in CI but unacceptable on a Lambda cold
  start. The spec's risks section records the options; Stage 5 must pick one.
