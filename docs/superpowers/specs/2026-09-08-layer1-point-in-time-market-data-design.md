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
- Raw vendor responses are retained permanently and the curated store can be rebuilt from
  them, byte-identically, without re-fetching.
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
| Price basis | **Unadjusted** by default; values returned as exact decimal strings |
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

**Open risk.** Verification used the `demo` key, which may be more permissive than a real
free key. **The first task of Stage 2 is re-running those three calls with a real key.** If
splits or dividends turn out to be gated, corporate actions fall back to manual entry via
the CLI with `source = MANUAL` — a legitimate mode for a point-in-time store in any case.

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

Run records mirror to S3 so the DynamoDB TTL does not delete the audit trail of a system
built for auditability.

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
   normalisation a pure function of raw, so rebuilds are byte-identical. This resolves a
   direct contradiction in the original spec.
3. **Append-only.** Nothing is updated or deleted. A restatement is a new row with a later
   `observed_at` alongside the original.
4. **Reads** filter `observed_at <= asOf`, then take the latest row per
   `(symbol, effective_date)`, **tiebreaking on `ingest_id` descending** so identical
   timestamps cannot return different rows on different runs.
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
7. **Rebuild determinism.** Normalising the same raw object twice yields byte-identical
   Parquet.
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

| Stage | Deliverable | AWS |
|---|---|---|
| **0** | Repo, solution skeleton, CI, README | No |
| **1** | Domain + query layer + **all 8 temporal tests** on local Parquet fixtures | **No** |
| **2** | Twelve Data source, normaliser, raw envelope, CLI (`watchlist`, `backfill`, `query`) against the local filesystem | No |
| **3** | Terraform: bootstrap, S3, DynamoDB, Lambda, EventBridge, IAM, alarms, log retention. Storage swaps to S3 behind the same interface. First scheduled run | Yes |
| **4** | `reprocess` and `compact`, rebuild-from-raw proof, run records to S3, GitHub OIDC | Yes |
| **5+** | EDGAR fundamentals via bulk archives; Layer 1 complete | Yes |

Stage 1 is the part worth showing another engineer, and it requires no AWS account, no API
key and no money.

**Layer 2 (event studies) must not begin until the Stage 1 test suite passes.** Event studies
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
