# Layer 1: Point-in-Time Market Data Warehouse — Design

**Date:** 2026-09-08
**Status:** Approved, pending implementation plan

---

## 1. Goal

Build a data store that answers one question correctly:

> "What was knowable about instrument X on date D?"

Every read is parameterised by an `asOf` timestamp and returns only facts observed at
or before that moment. Lookahead bias is prevented structurally by the data model, not
by developer discipline.

This is the foundation for later layers (event studies, decision journal). It is useless
if it silently leaks future information, so correctness of the temporal model outranks
features, coverage and performance.

### Success criteria

- A query for `asOf = 2024-06-30` cannot see a row observed on `2024-07-01`, and there
  is a test proving it.
- Price history is reproducible: rerunning an analysis six months later against the same
  `asOf` returns identical numbers.
- Raw vendor responses are retained permanently and the curated store can be dropped and
  rebuilt from them, without re-fetching, yielding a logically identical dataset — the same
  rows, the same values and the same `observed_at` timestamps.
- Runs unattended on a daily schedule for a negligible monthly cost.

### Non-goals

- No signals, screeners, scoring or backtesting.
- No UI. CLI plus DuckDB is sufficient through Layer 1.
- No full-market coverage. An opt-in watchlist only.
- No delisted-company history (deferred; a known survivorship-bias limitation).
- No intraday or real-time data. Daily bars only.
- No FX. USD throughout.

---

## 2. Scope decisions

| Decision | Value | Rationale |
|---|---|---|
| Market | **US equities only** | Twelve Data's free tier covers US markets, forex and crypto. ASX requires their Pro plan at US$99/month, delayed. Dropping ASX also removes franking, consolidations, dual schedules and dual calendars, and makes the EDGAR `cik` linkage universal. |
| Universe | **Opt-in, ~15-20 symbols**, growing | Demand-driven: add a symbol, it backfills and is tracked from then on. Not index-wide. |
| Currency | **USD only** | Measuring company and event behaviour, where FX is noise. `fx_rates` dropped entirely; it can be added later as its own dataset without touching prices. |
| Language | **C# / .NET 10** | Parquet.Net is pure managed with no native dependency, so Lambda packages stay small with no layer. `DateOnly` and `DateTimeOffset` are distinct types, so the project's most dangerous bug class — confusing the date a fact is *about* with the date it was *learned* — is compiler-enforced. .NET 10 has a managed Lambda runtime (Jan 2026, LTS to Nov 2028). |
| Infrastructure | **Terraform** | Industry standard, avoids CDK/jsii friction. Lives fully separated from application code. |
| Read engine | **DuckDB** over Parquet | Free, local, language-agnostic. Athena is dropped: no free tier ($5/TB scanned) and hand-written Glue table definitions are real work for no benefit at this volume. |
| Layer 2 boundary | **Parquet on S3** | The interop contract is the file format, not the language. Python can join later for statistics without porting anything. |

---

## 3. Vendor: Twelve Data (free tier)

Verified 2026-09-08 against the documented `demo` key:

| Check | Result |
|---|---|
| `/time_series` | 2,936 daily bars, 2015-01-02 to 2026-09-04, **in a single request** |
| Price basis | **Split-adjusted, not unadjusted** — see the correction below. Values are exact decimal strings |
| `/splits` | AAPL 4-for-1 (2020-08-31, `ratio: 0.25`) back to 1987 |
| `/dividends` | 83 entries back to 1988 |
| Limits | 800 credits/day, **8 credits/minute** |

**Budget.** `outputsize` maxes at 5000 bars (~20 years). A full backfill of one symbol costs
**3 credits** (prices + splits + dividends). A daily refresh costs 1 credit per symbol.
Twenty symbols is ~20 credits/day against 800, and ~2.5 minutes of wall clock at the
per-minute cap. Rate limiting is a non-issue at this scale: no distributed token bucket,
no queue, no reserved-concurrency throttle beyond `1`.

**Fetch strategy.** `/time_series` returns *full* history on every call, so a naive content
hash would differ every day (a new bar is appended) and re-ingest all 2,936 rows. Daily runs
therefore request `start_date = cursor` only; full history is for backfill.

> ### Correction, 2026-09-08: `/time_series` returns SPLIT-ADJUSTED prices
>
> The "unadjusted by default" claim above was **wrong**, and it was the assumption the
> storage model rested on. Measured against the historical record:
>
> | Date | True close that day | Vendor returned | Ratio |
> |---|---|---|---|
> | 2020-06-15 | $342.99 | 85.74750 | ÷ 4 |
> | 2020-08-28 | $499.23 | 124.80750 | ÷ 4 |
> | 2014-06-06 | $645.57 | 23.056070 | ÷ 28 |
>
> Each matches a clean division by the **cumulative split factor from that date to today**
> (÷4 for the 2020 4-for-1; ÷28 for the 2014 7-for-1 compounded with it), to five decimal
> places. There is no `adjust` parameter to turn this off.
>
> The adjustment is **split-only**. A dividend adjustment would push these values below a
> clean division by the split factor, and it does not.
>
> **Why this matters.** A price the vendor reports for 2020-06-15 is re-based every time a
> new split occurs. The same fetch performed after a future 2-for-1 would return 42.87 for
> that same day. So the "raw" payload is a snapshot of a moving target, and computing
> adjustment factors at query time on top of it would **double-adjust**.
>
> **Why it is recoverable.** Splits and their ex-dates come from `/splits`, and every raw
> envelope carries the `observed_at` at which its prices were adjusted. True unadjusted
> price = reported price × (cumulative split factor for splits with ex-date after the
> effective date, as known at `observed_at`). Nothing is lost; the un-adjust step has to be
> added, and it needs the splits payload as an input.
>
> This affects Stage 4, and the resolution is an open design decision recorded there.

**Resolved 2026-09-08.** The three calls were re-run with a real free-tier key via
`scripts/verify-vendor.sh`: `/time_series`, `/splits` and `/dividends` all returned data.
Nothing is gated, so the `source = MANUAL` fallback for corporate actions is **not** needed
and corporate actions stay in this stage. The key is stored at
`/pit-marketdata/twelvedata/apikey` in SSM as a `SecureString`.

Two payload details the live responses settled, both of which the parsers depend on:

- Prices and volume arrive as exact decimal **strings** (`"328.31000"`, `"39551800"`), which
  is what makes the parse-to-`decimal` rule work without float error.
- `/splits` reports `ratio` as a **rounded JSON number** — a 7-for-1 split comes back as
  `0.14286`, not `1/7` — while `from_factor` and `to_factor` are exact integers. Parsers
  must derive the ratio from the factors; trusting `ratio` pushes a rounding error into every
  downstream adjustment factor. `/splits` keys its date as `date`, `/dividends` as `ex_date`.

---

## 4. Architecture

Local-first, cloud-scheduled. Two entry points, one shared core.

```
CLI (local, on demand)                EventBridge (weekdays 22:15 UTC, after US close)
  watchlist add AAPL                                   |
    |- write watchlist entry                           v
    |- backfill: 3 credits, ~23s             Ingest Lambda (.NET 10, ARM64, RC=1)
    |- write raw/ + curated/                   read watchlist from DynamoDB
                                               per symbol, incremental from cursor:
  reprocess --from 2020-01-01                    fetch -> hash -> skip if unchanged
    |- re-parse raw/, rebuild curated/           write raw/, normalise in-process
    |- no vendor calls                         write ONE Parquet part per run
                                               update cursors + run record
  query / compact
    |- DuckDB over Parquet
```

**One Lambda.** At 20 symbols a dispatcher/queue/fetcher/normaliser fan-out has nothing to
fan out to. `IPriceSource` and `IPriceNormaliser` remain separate *interfaces* — that is what
allows reprocessing raw payloads with improved parsing without re-fetching — but they run in
the same process. Reprocess is a CLI command, not another Lambda.

**Backfill runs in the CLI**, not the cloud. Adding a symbol is a human action costing
3 credits and ~23 seconds, writing straight to storage. No orchestration required.

**Explicitly removed from the original spec:** SQS, the DLQ, the dispatcher/fetcher/normaliser
split, the compactor Lambda, Glue tables, Athena, and the second schedule.

**Cost.** ~2.5 min/run x 22 runs at 512 MB is ~1,650 GB-seconds against Lambda's
400,000/month free allowance. DynamoDB and S3 sit inside their free tiers at this volume.
Realistically a few cents per month — not zero, since S3's free tier is 12-month, not
perpetual. Billing alarms at US$5 and US$20 remain mandatory.

### Storage layout

```
raw/source=twelvedata/dataset=prices_daily/dt=2026-09-08/AAPL.json
raw/source=twelvedata/dataset=corporate_actions/dt=2026-09-08/AAPL.json
curated/dataset=prices_daily/year=2026/part-20260908T2215Z.parquet
```

Raw is the store of record and is never modified. Curated is derived and can be dropped and
rebuilt at any time.

Curated Parquet is partitioned by **year only**. Twenty symbols x ~250 trading days is
~5,000 rows/year; adding a month partition would produce 12 files of ~400 rows, which is
worse than not partitioning. Compaction is an annual CLI command, not a scheduled Lambda.

### DynamoDB (small mutable state only)

Single table, `marketdata`.

| PK | SK | Purpose |
|---|---|---|
| `INSTRUMENT#{symbol}` | `META` | Instrument reference data |
| `WATCHLIST` | `SYMBOL#{symbol}` | Universe membership, with `added_at` / `removed_at` |
| `CURSOR#{dataset}` | `SYMBOL#{symbol}` | Last fetched `effective_date` and payload hash |
| `RUN#{yyyy-MM-dd}` | `{dataset}#{symbol}` | Per-run outcome (mirrored to S3 permanently) |

**Watchlist membership is bitemporal and soft-deleted.** Without `added_at` / `removed_at`
the *universe itself* is not point-in-time: adding a symbol today and backfilling it would
make every historical query silently "know about" a company never heard of at the time. That
is survivorship bias re-entering through the one table exempted from the append-only rule,
and it would quietly corrupt Layer 2.

`RUN#` items carry a 90-day TTL to keep the table small, and are mirrored to S3 on write so
that expiry does not delete the audit trail of a system built for auditability.

No price or fundamental data in DynamoDB. It is columnar analytical data and belongs in
Parquet.

---

## 5. Data model

### Temporal rules (non-negotiable)

1. **Two timestamps on every fact.** `effective_date` is the date the fact is *about*;
   `observed_at` is the UTC instant it was *learned*.
2. **`observed_at` is stamped by the fetcher into the raw envelope; the normaliser copies
   it.** If the normaliser stamped `UtcNow`, a rebuild in 2028 would produce different
   timestamps and every historical answer would silently change. Stamping at fetch makes
   normalisation a pure function of raw, so a rebuild reproduces the dataset exactly. This
   resolves a direct contradiction in the original spec.
3. **Append-only.** Nothing is updated or deleted. A restatement is a new row with a later
   `observed_at` alongside the original.
4. **Reads** filter `observed_at <= asOf`, then take the latest row per natural key,
   **tiebreaking on `ingest_id` descending** so identical timestamps cannot return different
   rows on different runs. The natural key is `(symbol, effective_date)` for `prices_daily`
   and `(symbol, ex_date, action_type)` for `corporate_actions`.
5. **Prices are always stored raw unadjusted**, exactly as the vendor returned them.
   Corporate actions live in a separate dataset and adjustment factors are computed at query
   time. Vendors silently rewrite adjusted history after every split and dividend, which
   makes results irreproducible.

### The backfill timestamp rule

Backfilled history has no true `observed_at` — a ten-year backfill run today would stamp
every row with today, and a query for `asOf = 2020-06-30` would return **nothing**. The store
would be blank for every historical question, and would stay that way for years.

Therefore: **the first observation of a bar gets an inferred `observed_at`**, computed as
`effective_date` at 16:00 America/New_York plus a 15-minute lag, tagged
`observed_at_kind = INFERRED`. Bars caught live are tagged `OBSERVED`.

This is legitimate because the goal is what was **knowable**, not what was on disk. A daily
bar's publication time is reconstructible to within minutes — the close for 2020-06-30
genuinely was public shortly after the close on 2020-06-30.

Two guardrails:

- **Only a bar's first observation is inferred.** Any later value that *differs* is genuine
  news and gets `OBSERVED` with the real fetch time. Without this, a vendor silently
  restating a 2019 bar in 2027 would claim the corrected value was known in 2019 — a real
  leak.
- **Inference is confined to prices.** Fundamentals are never inferred: FY2020 earnings were
  not knowable on 2020-06-30, and EDGAR supplies a real `filed_date` anyway. For corporate
  actions the inferred `observed_at` is the `ex_date`, which deliberately *under*-claims
  knowledge (the announcement preceded it) and so errs toward not knowing.

### Schemas

**`prices_daily`** (Parquet)

```
symbol                  string
effective_date          date            -- exchange-local trading date
open, high, low, close  decimal(18,6)   -- unadjusted, exactly as returned
volume                  long
currency                string
source                  string
observed_at             timestamp(UTC)
observed_at_kind        string          -- OBSERVED | INFERRED
ingest_id               string          -- run id; deterministic tiebreak
raw_key                 string          -- key of the raw object this row came from
```

`decimal`, not `double`: Twelve Data returns exact strings such as `"328.31000"`, so parse
them exactly rather than inheriting float drift.

`raw_key` gives every row provenance back to an immutable payload, which is what makes
"rebuild from raw" verifiable rather than aspirational.

**`corporate_actions`** (Parquet)

```
symbol, ex_date, action_type, ratio, amount, currency, source,
observed_at, observed_at_kind, ingest_id, raw_key
```

`action_type`: `SPLIT | DIVIDEND`. A reverse split is a `SPLIT` with `ratio > 1`.

**`instruments`** (DynamoDB)

```
symbol, exchange, mic_code, name, currency, cik, sector,
listing_date, first_seen_at, is_active
```

`cik` is carried from the start for the Stage 5 EDGAR linkage.

**`fundamentals`** (Parquet, Stage 5, schema fixed now)

```
symbol, cik, period_end, fiscal_year, fiscal_period, concept,
value, unit, form_type, accession, filed_date, observed_at
```

### Raw envelope

Every raw object wraps the verbatim vendor payload:

```json
{
  "source_id": "twelvedata",
  "symbol": "AAPL",
  "dataset": "prices_daily",
  "observed_at": "2026-09-08T22:15:03Z",
  "request_url": "https://api.twelvedata.com/time_series?...&apikey=REDACTED",
  "content_hash": "sha256:...",
  "payload": {}
}
```

The API key **must** be redacted from `request_url`. Raw storage is permanent and versioned;
writing a secret into it is unrecoverable.

---

## 6. Query layer

```csharp
public interface IMarketDataQuery
{
    Task<IReadOnlyList<DailyBar>> GetPricesAsync(
        string symbol, DateOnly from, DateOnly to,
        DateTimeOffset asOf,
        PriceAdjustment adjustment,
        ObservationMode observations,
        CancellationToken ct);

    Task<IReadOnlyList<CorporateAction>> GetCorporateActionsAsync(
        string symbol, DateOnly from, DateOnly to,
        DateTimeOffset asOf, ObservationMode observations, CancellationToken ct);
}

public enum PriceAdjustment { None, SplitsOnly, SplitsAndDividends }
public enum ObservationMode { All, ObservedOnly }
```

`ObservationMode` is the payoff for tagging `INFERRED`; without it the tag is decoration.
`ObservedOnly` yields the strict, zero-inference view for results that must be defensible.

The temporal model reduces to one window function:

```sql
WITH visible AS (
  SELECT *, ROW_NUMBER() OVER (
    PARTITION BY symbol, effective_date
    ORDER BY observed_at DESC, ingest_id DESC) AS rn
  FROM read_parquet($glob)
  WHERE symbol = $symbol
    AND observed_at <= $asOf
    AND ($mode = 'All' OR observed_at_kind = 'OBSERVED')
)
SELECT * FROM visible WHERE rn = 1
  AND effective_date BETWEEN $from AND $to
ORDER BY effective_date;
```

**Adjustment** is the cumulative product of split ratios for actions with
`ex_date > bar_date` that were known as at `asOf`. Verified against real numbers: AAPL closed
~$499 before the 2020-08-31 4-for-1 and ~$129 after; `499 x 0.25 = 124.75`, so the adjusted
series is continuous across the ex-date.

---

## 7. Test suite

These are the specification. Write them before the implementation.

1. **No lookahead.** A row with `observed_at = 2024-07-01` is not returned by a query with
   `asOf = 2024-06-30`.
2. **Restatement visibility.** With two rows for the same `effective_date`, a query as at a
   date between them returns the original value; after both, the revised value.
3. **Reproducibility.** The same query with the same `asOf` returns identical results before
   and after new data is ingested.
4. **Adjustment correctness.** AAPL's 2020-08-31 4-for-1 produces a continuous adjusted
   series across the ex-date, verified by hand against the published ratio.
5. **Adjustment is as-of aware.** A corporate action observed after `asOf` is not applied.
6. **Deterministic tiebreak.** Two rows with identical `observed_at` return the same row on
   every run.
7. **Rebuild determinism.** Normalising the same raw object twice yields an identical row
   set — same values, same `observed_at`, same `observed_at_kind`. Asserted on rows read
   back through the query layer, not on file bytes: Parquet embeds a writer-version string
   and its bytes depend on row ordering and row-group boundaries, so byte-comparison would
   be both fragile and beside the point.
8. **`ObservedOnly` excludes `INFERRED`.**

---

## 8. Repository layout

```
src/     Domain . Sources . Storage . Query . Ingest(Lambda) . Cli
tests/   Domain.Tests . Query.Tests  <- the temporal suite . Integration.Tests
infra/terraform/
         bootstrap/   state bucket, local state, run once
         modules/     storage . ingest . observability
         *.tf, terraform.tfvars.example
scripts/ build-lambda.ps1 | .sh   -> dotnet publish -r linux-arm64 -> zip
docs/superpowers/specs/
.github/workflows/  ci.yml (dotnet build+test) . terraform.yml (fmt/validate/plan)
```

`src/` and `tests/` know nothing about Terraform. `infra/` consumes a built zip via a
variable. The only coupling is an artifact path and a few resource names, so either side can
be replaced without touching the other.

Terraform state uses the S3 backend with **native locking** (`use_lockfile = true`).
DynamoDB-based locking is deprecated, so no lock table is created. A one-shot `bootstrap/`
configuration with local state creates the state bucket.

---

## 9. Stages

Foundations first: infrastructure, then the data layer, then integrations, and only then the
application that orchestrates them.

| Stage | Deliverable | AWS |
|---|---|---|
| **0** | Repo, solution skeleton, pinned package baseline, CI | No |
| **1** | **Infrastructure.** Terraform: bootstrap state bucket, S3, DynamoDB, IAM, billing alarms, log retention. Deployed and verified | Yes |
| **2** | **Data layer.** Domain entities and temporal rules, Parquet schemas, `IRawStore` / `ICuratedStore` over local disk and S3, DynamoDB repositories (instruments, bitemporal watchlist, cursors, runs). Round-tripped against LocalStack | Yes |
| **3** | **Integration.** Real-key vendor verification, `TwelveDataPriceSource`, raw envelope, `IPriceNormaliser`, content hashing, cursor advance | Yes |
| **4** | **Query layer.** As-of reads over DuckDB, adjustment calculation, **all 8 temporal tests** | No |
| **5** | **Application.** Lambda handler, EventBridge schedule, CLI (`watchlist`, `backfill`, `query`). First unattended run | Yes |
| **6** | **Hardening.** `reprocess`, `compact`, rebuild-from-raw proof, run mirroring to S3, GitHub OIDC | Yes |
| **7+** | EDGAR fundamentals via bulk archives; Layer 1 complete | Yes |

The temporal rules are not deferred to Stage 4 — they *are* the schema, and they are fixed in
Stage 2 when `prices_daily`, `corporate_actions` and the watchlist keys are defined. Stage 4
implements and proves the read path over a data layer that already encodes them. Concretely:
tests 1, 2, 6 and 7 (no lookahead, restatement visibility, deterministic tiebreak, rebuild
determinism) can be written against fixtures the moment Stage 2 lands, and should be.

**Layer 2 (event studies) must not begin until the Stage 4 test suite passes.** Event studies
built on a leaky temporal model produce confident nonsense, which is worse than no analysis
at all.

---

## 10. Known limitations

- **Survivorship bias.** The watchlist contains only instruments chosen today. Delisted
  companies are absent, so any cross-sectional study over this universe is biased. Bitemporal
  watchlist membership limits the damage but does not remove it. Deferred to Layer 2.
- **Inferred timestamps.** Backfilled prices carry reconstructed publication times, not
  observed ones. `ObservedOnly` exists so this is always distinguishable.
- **Single vendor.** No cross-source validation until a second `IPriceSource` exists. The
  abstraction should not gain a second implementation until the first has exercised it.
- **Vendor free tier.** Twelve Data may change or withdraw its free tier without notice.
  Permanent raw retention means such a change costs a parser rewrite, not the data.

---

## 11. Package baseline

Versions verified against NuGet on 2026-09-08. All versions are pinned centrally in
`Directory.Packages.props`; no project declares a floating version.

### Domain — no dependencies

BCL only: `DateOnly`, `DateTimeOffset`, `TimeProvider`, `decimal`.

*NodaTime 3.3.3 was considered* — its `Instant` / `LocalDate` split models bitemporality
cleanly. Rejected because `DateOnly` vs `DateTimeOffset` already provides the compile-time
separation that motivated the language choice, and a second date vocabulary would obscure it.
The Domain project's dependency count staying at zero is itself the point.

### Storage

| Package | Version | Why |
|---|---|---|
| `Parquet.Net` | 6.1.0 | Pure managed; no native dependency, so the Lambda package needs no layer |
| `AWSSDK.S3` | 4.0.102 | |
| `AWSSDK.DynamoDBv2` | 4.0.103 | |
| `AWSSDK.Extensions.NETCore.Setup` | 4.0.101 | DI registration for AWS clients |

### Sources

| Package | Version | Why |
|---|---|---|
| `Microsoft.Extensions.Http.Resilience` | 10.9.0 | Standard resilience handler over `HttpClient`; wraps Polly 8. Preferred to raw `Polly` — the vendor call needs retry and timeout, not a bespoke pipeline |
| `AWSSDK.SimpleSystemsManagement` | 4.0.103 | SSM Parameter Store (SecureString) for the API key |

`System.Text.Json` from the BCL. No third-party JSON dependency.

### Query

| Package | Version | Why |
|---|---|---|
| `DuckDB.NET.Data.Full` | 1.5.5 | Bundles the native DuckDB binary. The `.Full` variant is deliberate — it removes any separate native-install step for a research tool run on a laptop |

### Lambda

| Package | Version |
|---|---|
| `Amazon.Lambda.Core` | 3.3.0 |
| `Amazon.Lambda.Serialization.SystemTextJson` | 3.0.1 |

Managed .NET 10 runtime, class-library handler, ARM64. **Native AOT is deliberately not
used**: cold start is irrelevant for a once-daily scheduled job, and Parquet.Net's
reflection-based serialisation paths are a poor AOT fit. Trimming risk for no benefit.

### CLI

`System.CommandLine` 2.0.11 — reached stable after a long preview; first-party and light.

*Spectre.Console.Cli 0.55.0 was considered* for nicer rendering, but it remains pre-1.0 and
this CLI is utilitarian.

### Testing

| Package | Version | Why |
|---|---|---|
| `xunit.v3` | 4.0.0 | |
| `xunit.runner.visualstudio` | latest | Shared across v2/v3; there is no v3-specific runner package |
| `Microsoft.NET.Test.Sdk` | 18.9.0 | |
| `Shouldly` | 4.3.0 | MIT. **Not FluentAssertions** — v8 moved to the Xceed Community License, US$129.95/developer/year for commercial use. `AwesomeAssertions` 9.6.0 is an MIT fork of v7 but is explicitly frozen with no further development |
| `Microsoft.Extensions.TimeProvider.Testing` | 10.9.0 | `FakeTimeProvider`. **Non-negotiable**: every `observed_at` in a test must come from an injected clock. A test that reaches for `DateTimeOffset.UtcNow` cannot assert anything about a temporal model |
| `Testcontainers.LocalStack` | 4.15.0 | S3 and DynamoDB integration tests without touching a real account. Requires Docker |
| `coverlet.collector` | 10.0.1 | |

### Hosting

`Microsoft.Extensions.Hosting` 10.0.11 — one composition root shared by the CLI and the
Lambda handler, so both resolve the same `IPriceSource`, `IPriceNormaliser` and store
implementations.
