# Layer 1 Stage 4 — As-of query, adjustment, and the split un-adjust fix

**Status:** design, approved 2026-09-08. Supersedes nothing; extends
[the Layer 1 design](2026-09-08-layer1-point-in-time-market-data-design.md) §6 and §7 with
the detail those sections left open.

**Goal:** make the warehouse answerable. Stages 0–3 can write point-in-time data but cannot
ask point-in-time questions. This stage adds the as-of read path, the adjustment arithmetic,
and the eight temporal tests that are the acceptance criteria for everything built so far.

**Prerequisite it also fixes:** Twelve Data returns split-adjusted prices, so the ingest path
currently stores numbers that are re-based by every future split. That is corrected here,
before any data exists to migrate.

---

## 1. Why this stage exists

The project's entire claim is that a query as at a past date returns what was knowable then.
Nothing built so far tests that claim, because there is no query. §7's eight tests are the
proof, and they cannot be written until this stage exists.

Two facts make it urgent rather than merely next:

- **The store is empty.** `s3://pit-marketdata-data-bzun6w` contains no objects. The
  un-adjust fix therefore needs no migration, no reprocessing, and no dual-read period. That
  is only true until the first backfill runs.
- **The soft spot is now fixable.** `IngestService` approximates prior knowledge from the
  cursor watermark because the query layer did not exist. It does after this stage.

## 2. The correction this stage carries

`/time_series` returns prices adjusted for every split **up to the moment of the request**,
with no parameter to disable it. Measured against the historical record:

| Date | True close that day | Vendor returns | Ratio |
|---|---|---|---|
| 2020-06-15 | $342.99 | 85.74750 | ÷ 4 |
| 2020-08-28 | $499.23 | 124.80750 | ÷ 4 |
| 2014-06-06 | $645.57 | 23.056070 | ÷ 28 |

Each is a clean division by the cumulative split factor from that date to today — ÷4 for the
2020 4-for-1, ÷28 for the 2014 7-for-1 compounded with it — to five decimal places. The
adjustment is **split-only**: a dividend adjustment would push these below a clean division,
and it does not.

Storing those numbers unmodified would break two invariants at once. The stored value would
not be a fact about its own date (it moves whenever a new split occurs), and applying
query-time adjustment on top of an already-adjusted number would **double-adjust**.

The resolution is to invert the vendor's adjustment on ingest, so curated rows hold the price
actually quoted that day.

## 3. The arithmetic

One definition serves both directions.

> **Split factor** for a bar is the product of `ratio` over all splits whose `ex_date` is
> strictly after the bar's `effective_date` and which are visible at the relevant instant.

`ratio` is `to_factor / from_factor` — `0.25` for a 4-for-1 — derived from the exact integer
factors, never from the vendor's rounded `ratio` field.

```
ingest:   unadjusted = vendor_reported ÷ factor(splits known at observed_at)
query:    adjusted   = stored_unadjusted × factor(actions visible at asOf)
```

The same function, divided one way and multiplied the other. Worked through for 2020-06-15:

| Step | Value |
|---|---|
| Vendor reports | `85.74750` |
| ÷ 0.25 → stored in curated | **`342.99`** |
| × factor as of 2020-07-01 (no split known yet → 1.0) | **`342.99`** |
| × factor as of today (4-for-1 known → 0.25) | **`85.7475`** |

**Volume** moves inversely: `volume ÷ factor`. Without it a volume series jumps 4× across
2020-08-31 for no economic reason, which would corrupt any volume-based filter.

**Dividends** contribute a separate multiplicative factor, compounded over ex-dates after the
bar:

```
dividend_factor = Π (1 - amount / close_on_day_before_ex_date)
```

This is the standard back-adjustment. It makes percentage returns correct across ex-dates,
which subtraction does not, and cannot drive prices negative the way subtraction can deep in
history. `PriceAdjustment.SplitsAndDividends` multiplies both factors.

### Why the calculation is pure and lives in Domain

`AdjustmentCalculator` takes corporate actions and a date, and returns a factor. No clock, no
I/O, no database. It sits in `MarketData.Domain` beside `ObservationPolicy`, because the
adjustment rule *is* a domain rule and Domain's zero-dependency constraint keeps it honest.

This is the code whose silent failure the project most fears. Making it a pure function means
tests 4, 5 and 6 target it directly with hand-checked numbers, rather than inferring a factor
from the far end of a SQL query.

## 4. Components

```
app/src/MarketData.Domain/
    AdjustmentCalculator.cs         NEW  pure; factors, Apply, Invert
app/src/MarketData.Query/           NEW PROJECT
    IMarketDataQuery.cs             the §6 interface verbatim
    DuckDbMarketDataQuery.cs        two SELECTs, then the calculator
    CuratedSource.cs                resolves an s3:// glob or a local path
app/src/MarketData.Sources/
    IngestService.cs                MODIFIED  fetch splits, un-adjust, record the key
app/src/MarketData.Storage/
    Parquet/PriceRow.cs             MODIFIED  + SplitsRawKey
app/tests/MarketData.Query.Tests/   NEW  the eight temporal tests
```

`IPriceNormaliser.Parse(RawEnvelope)` is **unchanged**. It keeps returning prices exactly as
reported, from a single envelope, and un-adjustment is a separate step. Widening its signature
to take two envelopes would couple parsing to corporate actions for no gain — the two are
independently testable as they stand.

## 5. Read path

`DuckDbMarketDataQuery` issues two queries and combines them in C#:

1. **Visible bars** — §6's window function, unchanged:

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

2. **Visible corporate actions** — the same `observed_at <= asOf` filter over
   `dataset=corporate_actions`.

Then `AdjustmentCalculator` computes a factor per bar and applies it.

The `ingest_id DESC` tiebreak is what makes test 6 deterministic: two rows sharing an
`observed_at` resolve identically on every run.

### `ObservationMode` applies to bars only — never to corporate actions

This is the one combination that could produce a silently wrong answer, so it is settled
here rather than left to an implementer.

Every backfilled corporate action carries `observed_at_kind = INFERRED`, because its
`observed_at` is its ex-date rather than a live capture. If `ObservedOnly` also filtered
actions, then `SplitsOnly` combined with `ObservedOnly` would find no splits, apply a factor
of `1.0`, and return **unadjusted prices labelled as adjusted**. Nothing would throw.

Two things make bars-only correct rather than merely convenient:

- An action's ex-date is a matter of public record, not a reconstruction. The `INFERRED` tag
  on an action describes uncertainty about *when we learned of it*, not about the date it
  took effect. A split's ex-date is certain in a way a backfilled bar's publication instant
  is not.
- `ObservationMode` exists so a result can be defended as containing no reconstructed
  observations. Adjustment factors are derived, not observed, so excluding the inputs to a
  derivation does not make the output stricter — it makes it wrong.

A test asserts that `ObservedOnly` changes which bars are returned and does **not** change
the adjustment factor applied to them.

### What each `PriceAdjustment` returns

| Value | Result |
|---|---|
| `None` | the stored unadjusted price — exactly what was quoted that day |
| `SplitsOnly` | `unadjusted × split_factor` |
| `SplitsAndDividends` | `unadjusted × split_factor × dividend_factor` |

`None` is the literal historical record and is what the round-trip invariant in §8 asserts
against.

### Where DuckDB reads from

`CuratedSource` resolves one of two forms behind a single abstraction:

- `s3://pit-marketdata-data-bzun6w/curated/...` via DuckDB's `httpfs` extension, credentials
  from the ambient AWS chain. No sync step and no local disk — the production path.
- a local directory, for tests and offline work.

Both must produce identical results. A test asserts the same query over the same data through
both, which is the only way that stays true.

## 6. Revised ingest flow

1. Fetch the prices envelope **and** the splits envelope
2. Write both to `raw/`
3. `Parse(prices)` → as-reported bars; `ParseSplits(splits)` → actions
4. **Un-adjust** the bars by dividing by the same-run split factor
5. `ObservationPolicy` decides each bar's timestamp — unchanged
6. Append price rows carrying `SplitsRawKey`, and append the action rows

Step 4 is the only new link, and steps 3–5 remain pure. A rebuild reads the two immutable
envelopes named on the row and reproduces identical output, which is test 7.

### Determinism of the un-adjust

Un-adjustment needs a split history, and which history is used must be recoverable forever.
Each ingest run fetches prices and splits together, un-adjusts using **that run's** splits
payload, and records its raw key in `SplitsRawKey` on every price row it writes.

The alternatives both fail. Using the latest splits data at rebuild time produces different
numbers once a new split is learned, breaking test 7. Deriving the split set from curated
corporate actions makes price normalisation depend on curated state, so `raw/` could no longer
be replayed standalone — the property the whole raw store exists to provide.

### Fixing the cursor-watermark approximation

`IngestService` currently treats any bar at or before the cursor watermark as already known
and unchanged, so a restatement is detected only when its close differs from the incoming
value. That misses a restatement of some other field, and misses a changed close on a bar
beyond the watermark.

With a query layer, prior knowledge becomes a real lookup: for the symbol and date range being
ingested, read the currently-visible bars as at now, and pass each bar's known close — or null
— to `ObservationPolicy`. The approximation and its comment are removed.

## 7. Schema change

`PriceRow` gains one column:

| Column | Type | Meaning |
|---|---|---|
| `SplitsRawKey` | `string` | raw key of the splits envelope used to un-adjust this row |

Curated data is append-only and Parquet readers tolerate an added column, but the store is
empty, so this is a clean schema rather than an evolution. `CorporateActionRow` is unchanged.

## 8. Tests

**`AdjustmentCalculatorTests`** — pure, no DuckDB, no I/O:

- `342.99 × 0.25 = 85.7475` and its inverse
- `645.57 × (1/28) = 23.05607`, proving factors compound across two splits
- a split with `ex_date` equal to the bar date is **not** applied (strictly after)
- an action invisible at `asOf` contributes nothing
- `ObservedOnly` does not change the factor: an `INFERRED` split still adjusts
- dividend factors compound multiplicatively
- an empty action set yields exactly `1.0`, not an approximation

**The eight temporal tests** — `MarketData.Query.Tests`, over `LocalCuratedStore` and real
DuckDB, exactly as §7 specifies: no lookahead; restatement visibility; reproducibility;
adjustment correctness; adjustment as-of awareness; deterministic tiebreak; rebuild
determinism; `ObservedOnly` excludes `INFERRED`.

**A round-trip invariant** — `reported → un-adjust → store → query-adjust(as of now) →
reported`. If the two directions ever drift, this fails.

**Read-path equivalence** — the same query over the same data via a local path and via
`s3://` returns identical rows. Requires LocalStack, so it self-skips without Docker like the
existing integration tests.

## 9. Out of scope

Named so an implementer does not build them early:

- The Lambda handler, its IAM role, EventBridge schedule and log retention — Stage 5
- The CLI — Stage 5
- Compaction, the reprocess command, and the rebuild-from-raw proof as a shipped feature —
  Stage 6. Rebuild determinism is *tested* here; the operator-facing command is not built.
- Dividend-adjusted total-return series as a distinct dataset. The factor is computed on
  demand; nothing is materialised.
- GitHub OIDC for Terraform plans against real state.

## 10. Risks

**DuckDB native payload.** `DuckDB.NET.Data.Full` copies ~315 MB of native binaries for five
platforms into the output of every referencing project. Once `MarketData.Query` is a `src`
project, that propagates to everything downstream and previously exhausted the development
machine's disk. The plan must constrain runtime identifiers deliberately rather than
rediscover this mid-build.

**Dividend adjustment needs a prior close.** The factor divides by the close on the day before
the ex-date, which may be missing from the requested window or absent entirely. Behaviour must
be explicit: when the prior close is unavailable, the dividend contributes no factor and the
result is flagged rather than silently wrong.

**`httpfs` in tests.** DuckDB against LocalStack over `httpfs` requires path-style addressing
and an endpoint override. If that proves unreliable, read-path equivalence is asserted against
real S3 in a manual check instead, and the reason recorded — not dropped.

**`INSTALL httpfs` downloads from the internet — a Stage 5 problem, recorded here.**
`CuratedSource.Setup` issues `INSTALL httpfs; LOAD httpfs;`. `INSTALL` fetches the extension
from `extensions.duckdb.org` and caches it under `~/.duckdb/`. That is a harmless one-off on a
developer machine and in CI, but inside a Lambda it becomes a third-party network call on every
cold start, against a read-only filesystem where only `/tmp` is writable.

Stage 4 is unaffected — it runs locally and in CI. Stage 5 must resolve it, and the options are
known: bundle the extension in the deployment package and point `extension_directory` at it,
set `DUCKDB_EXTENSION_DIRECTORY` to a path under `/tmp` primed at build time, or statically
link an httpfs-enabled DuckDB build. Whichever is chosen, the Lambda must not reach
`extensions.duckdb.org` at runtime: an outage there would take ingestion down for a reason
unrelated to either AWS or the vendor.
