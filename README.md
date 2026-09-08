# pit-marketdata

A point-in-time market data warehouse for US equities.

It exists to answer one question correctly:

> **What was knowable about instrument X on date D?**

---

## The problem

Almost every home-built market data store leaks the future, and does it silently.

You download ten years of history today and analyse a strategy "as of" 2020. But the prices
you hold have been retroactively adjusted for every split and dividend since — so the 2020
numbers in your database were never the 2020 numbers anyone could see. Your backtest quietly
consults data that did not exist yet, reports an edge, and the edge evaporates in production.

The failure is not a bug you can find by reading the code. Nothing throws. The numbers look
plausible. That is what makes it dangerous.

## The approach

Every fact carries **two** timestamps:

| Column | Meaning |
|---|---|
| `effective_date` | the date the fact is *about* |
| `observed_at` | the UTC instant the fact was *learned* |

Rows are append-only — a restatement is a *new* row alongside the original, never an update.
Every read takes an `asOf` parameter, filters `observed_at <= asOf`, and takes the latest
surviving row per key.

Lookahead bias is therefore prevented **structurally, by the data model** rather than by
developer discipline. You cannot accidentally query the future, because the read path has no
way to express it.

Two supporting rules do most of the remaining work:

- **Prices are stored raw and unadjusted**, exactly as the vendor returned them. Corporate
  actions live in a separate dataset, and adjustment factors are computed at query time from
  only those actions known as at `asOf`. Vendor-adjusted history is rewritten after every
  corporate action, which is precisely what makes it irreproducible.
- **`observed_at` is stamped at fetch time into an immutable raw payload**, then copied
  through. Normalisation is a pure function of the raw store, so the curated data can be
  dropped and rebuilt byte-identically, years later, with no vendor calls.

## Status

**Pre-implementation.** The design is complete and reviewed:

📄 [Layer 1 design](docs/superpowers/specs/2026-09-08-layer1-point-in-time-market-data-design.md)

| Stage | Deliverable | Status |
|---|---|---|
| 0 | Repo, solution skeleton, pinned package baseline, CI | In progress |
| 1 | Infrastructure — Terraform: S3, DynamoDB, IAM, alarms | Not started |
| 2 | Data layer — domain, Parquet schemas, stores, repositories | Not started |
| 3 | Integration — Twelve Data source, raw envelope, normaliser | Not started |
| 4 | Query layer — as-of reads, adjustment, **8 temporal tests** | Not started |
| 5 | Application — Lambda, schedule, CLI | Not started |
| 6 | Hardening — reprocess, compaction, rebuild-from-raw proof | Not started |
| 7 | SEC EDGAR fundamentals | Not started |

Stage 4 is the part worth reading: its test suite *is* the specification. The temporal rules
themselves are fixed earlier, in Stage 2, because they are not a feature of the read path —
they are the schema.

## Stack

| Concern | Choice |
|---|---|
| Language | C# / .NET 10 |
| Storage | Parquet on S3 (curated), JSON on S3 (raw, permanent) |
| State | DynamoDB — watchlist, cursors, run records |
| Compute | AWS Lambda, ARM64, one scheduled function |
| Infrastructure | Terraform |
| Query | DuckDB over Parquet |

`.NET` was chosen over Python for two concrete reasons: `Parquet.Net` is pure managed code
with no native dependency, so Lambda packages need no layer; and `DateOnly` and
`DateTimeOffset` are distinct types, meaning this project's single most dangerous bug class —
confusing the date a fact is about with the date it was learned — is caught by the compiler.

Analysis is deliberately *not* coupled to that choice. The interop contract is Parquet on S3,
so later statistical work can be done from Python against the same files with the same `asOf`
predicate, with nothing to port.

## Layout

```
src/     Domain · Sources · Storage · Query · Ingest · Cli
tests/   Domain.Tests · Query.Tests ← the temporal suite · Integration.Tests
infra/   Terraform — fully separated from application code
docs/    Design specs
```

## Scope

US equities, daily bars, an opt-in watchlist of roughly 15–20 symbols. Not a full-market
dataset and not trying to be.

Known limitations — survivorship bias from an opt-in universe, inferred timestamps on
backfilled history, and single-vendor risk — are documented in
[§10 of the design](docs/superpowers/specs/2026-09-08-layer1-point-in-time-market-data-design.md#10-known-limitations)
rather than discovered later.

## Non-goals

No signals, no screeners, no backtesting, no UI. This layer is the foundation those would
stand on, and it is worth nothing if it leaks.

## Licence

MIT
