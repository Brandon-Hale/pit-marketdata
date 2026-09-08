# pit-marketdata — context

A point-in-time market data warehouse for US equities. It answers one question
correctly: **what was knowable about instrument X on date D?**

## Current state

Design and implementation plan are written, reviewed and committed. **No code exists yet.**
The `src/`, `tests/`, `infra/` and `scripts/` directories are empty placeholders.

## Start here

1. Read [`docs/superpowers/specs/2026-09-08-layer1-point-in-time-market-data-design.md`](docs/superpowers/specs/2026-09-08-layer1-point-in-time-market-data-design.md) — what and why.
2. Read [`docs/superpowers/plans/2026-09-08-layer1-foundations.md`](docs/superpowers/plans/2026-09-08-layer1-foundations.md) — 19 tasks, 126 steps, Stages 0–3.
3. Invoke `superpowers:subagent-driven-development` (or `superpowers:executing-plans`) and work the plan from Task 1.

Build order is deliberately **foundations first**: repo → Terraform infrastructure → data
layer → vendor integration. The as-of query layer, the eight temporal tests, the Lambda and
the CLI are Stages 4–6 and get their own plan after this one lands. Do not build them early.

## Non-negotiables

These are the correctness rules the whole project exists to uphold. Breaking one is silent —
nothing throws, the numbers just become wrong.

- **Two timestamps on every fact.** `effective_date` is what it is *about*; `observed_at` is
  when it was *learned*. Never conflate them.
- **`observed_at` is stamped once, by the fetcher, into the raw envelope.** Normalisers copy
  it. A normaliser that reads a clock breaks rebuild determinism.
- **Parsing is a pure function of the raw envelope** — no clock, no I/O, no prior state. The
  timestamp decision lives separately in `ObservationPolicy`.
- **Append-only.** No code path updates or deletes a curated row. A restatement is a new row.
- **Prices are stored raw and unadjusted**, parsed to `decimal` from the vendor's exact
  decimal strings. Never `double`. Never store vendor-adjusted prices.
- **A restatement never gets an inferred timestamp** — it takes the real fetch time. Inferring
  one would claim a corrected value was knowable at the original date.
- **No `DateTime.UtcNow` / `DateTimeOffset.UtcNow`** outside a composition root. All time comes
  from an injected `TimeProvider` so tests can control it.
- **API keys are redacted from anything written to storage.** Raw storage is permanent and
  versioned; a leaked key is unrecoverable.
- **`MarketData.Domain` has zero package references.** An architecture test enforces it.

## Conventions

- .NET 10, `net10.0`, nullable enabled, warnings as errors.
- **Central package management** — all versions pinned in `Directory.Packages.props`. After any
  `dotnet add package`, strip the `Version=` attribute it writes into the `.csproj`.
- Writes use Parquet.Net; **reads use DuckDB, including in tests**. Parquet.Net 6's class
  deserializer fails on plain `string` properties, and DuckDB is the production read path.
- Terraform state uses the S3 backend with `use_lockfile = true`. No DynamoDB lock table.
- Small, frequent commits — one per task, as the plan specifies.

## Environment

- **Docker must be running** for the LocalStack integration tests (Task 13 onward). CI skips
  them via `--filter "Category!=Integration"`.
- **`gh` lacks the `workflow` scope** as of the last check. Task 2 pushes `.github/workflows/`
  and will be rejected without it. Run `gh auth refresh -h github.com -s workflow` in an
  interactive terminal first, then confirm with `gh auth status`.
- **AWS credentials** are needed from Task 3 (`terraform apply`). Region `ap-southeast-2`.
- **A real Twelve Data API key** is needed at Task 15. The spec's vendor findings were verified
  with their `demo` key, which may be more permissive — Task 15 re-verifies with a real key
  *before* anything is built on it. If splits/dividends turn out to be gated, the documented
  fallback is manual entry with `source = MANUAL`.

## Known soft spot

Task 19's `IngestService` approximates "what do I already know?" from the cursor watermark
rather than a real per-date lookup, because the query layer does not exist yet. It catches the
common restatement case but not every one. This is deliberate and is listed in the plan's
deferred section — fix it in the next plan, don't paper over it now.

---

Design decisions were worked through in [this session](https://claude.ai/code/session_01PQ85wGEVmnSeAVnjeC9xta).
