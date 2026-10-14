<div align="center">

# PalORM

**A .NET 11 micro-ORM for Native AOT: everything generated at compile time, zero reflection at runtime**

[![.NET](https://img.shields.io/badge/.NET-11.0.0--preview.6-512BD4)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/PalORM.Core)](https://www.nuget.org/packages/PalORM.Core)
[![CI](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml/badge.svg)](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-AGPL--3.0--only-red)](LICENSE)

[![Native AOT](https://img.shields.io/badge/Native_AOT-3_dialects_verified-512BD4)](docs/AOT部署指南.md)
[![Diagnostics](https://img.shields.io/badge/Compile--time_diagnostics-45_rules-512BD4)](docs/API参考.md)
[![Dialects](https://img.shields.io/badge/Dialects-PostgreSQL_·_MySQL_·_SQLite-512BD4)](#-database-compatibility)

[简体中文](README.md) | English

</div>

A Roslyn source generator produces SQL construction, parameter binding, object mapping, and migration DDL at compile time — zero reflection, zero IL Emit at runtime. PostgreSQL / MySQL / SQLite across three dialects, with enterprise features out of the box: multi-tenancy, optimistic locking, soft delete, auditing, advisory locks, and more.

- **Compile-time safety net**: 45 Roslyn diagnostics move failures that would otherwise be runtime crashes or silently wrong data — a missing `[Key]`, a nullable tenant column bypassing isolation, an optimistic-lock baseline of 0, N+1 queries in loops — to compile time, with errors jumping to the declaration site
- **Full-pipeline Native AOT**: the only ORM in the .NET ecosystem with publish verification passing on all three dialects — no reflection, no IL Emit, no runtime code generation; the SQLite publish exe is just 4.5 MB
- **Floor-level performance**: single-row CRUD matches the hand-written ADO.NET floor (0.94~1.01×); bulk writes automatically take the fastest per-dialect path — PG Binary COPY, MySQL LOAD DATA, single-statement bulk UPDATE (measured 3.3× faster on MySQL); 20,000-row BulkInsert allocates ~19% of Dapper; Web-style request-per-session deployments get a built-in session pool (measured P99 −18%, Gen0 collections −58% at 32 concurrent)
- **Enterprise cross-cutting out of the box**: multi-tenant column isolation (queries and all write paths), optimistic locking, soft delete, audit interceptor (sensitive columns auto-masked), read/write splitting, advisory locks, resilient retry + circuit breaker (zero Polly dependency)
- **Zero-dependency core**: PalORM.Core references no third-party NuGet packages (BCL + ADO.NET abstractions only); SQLite supports at-rest AES-256 encryption via the SQLite3MC driver

> Detailed docs (in Chinese) live under [docs/](docs/) — [API reference](docs/API参考.md), [architecture](docs/架构设计.md), [AOT deployment guide](docs/AOT部署指南.md), [benchmark methodology](docs/性能基准规范.md).

See [Performance](#-performance) for measurement details (2026-10-14 benchmark batch).

---

## Table of Contents

| This document | | Deep docs ([docs/](docs/), mostly Chinese) | |
|---|---|---|---|
| [✨ Features](#-features) | [🚀 Quick Start](#-quick-start) | [Feature panorama (source-level)](docs/功能特性.md) | [AOT deployment guide](docs/AOT部署指南.md) |
| [🆚 ORM comparison](#-comparison-with-mainstream-orms) | [📖 Usage](#-usage) | [API reference](docs/API参考.md) | [Benchmark methodology](docs/性能基准规范.md) |
| [📊 Performance](#-performance) | [✅ Best practices](#-best-practice-cheat-sheet) | [Architecture](docs/架构设计.md) | [Release SOP](docs/发布规范.md) |
| [📦 Installation](#-installation) | [🔧 Configuration](#-configuration) | [ADRs](docs/adr/) | [Coding standards](docs/编码规范.md) |
| [🔄 Upgrade guide](#-upgrade-guide) | [🧰 Development](#-development) | [Contributing](CONTRIBUTING.md) | [Changelog](CHANGELOG.md) |
| [💬 Community](#-community) | [📄 License](#-license) | | |

---

## ✨ Features

**Everything generated at compile time.** A Roslyn `IIncrementalGenerator` emits, for every `[Table]` entity, a RowFactory (materialization delegates), a CommandFactory (parameter binding), and Migration (DDL for all three dialects); DTOs marked `[Projection]` get a source-generated RowFactory too, so join/report results map straight onto non-table types (v6.0). 45 compile-time diagnostics (42 analyzers + 3 generator rules) move failures that would otherwise be runtime crashes or silently wrong data — a missing `[Key]`, a nullable tenant column bypassing isolation, an optimistic-lock baseline of 0 — to compile time, with errors jumping to the declaration site (PALORM041/045/046 carry anchors). On the `FormattableString` path, values only ever become `@pN` placeholders (compile-time parameterization, injection-safe by default); explicit escape hatches are `Raw()` (verbatim literal fragments, control characters rejected) and `SessionSetupSql`, where the caller owns the content.

**Full-pipeline Native AOT.** The only ORM in the .NET ecosystem with full-pipeline Native AOT support: publish verification passes on all three dialects — SQLite / PostgreSQL / MySQL (output `PalORM AOT verification PASSED`) — with no reflection, no IL Emit, no runtime code generation. Deployment details: [AOT deployment guide](docs/AOT部署指南.md) (Chinese).

**Per-dialect bulk strategies.** The same API picks the fastest path per dialect: PG Binary COPY, MySQL BulkCopy (LOAD DATA, automatic fallback to multi-value INSERT when disabled server-side), SQLite multi-value INSERT. Single-statement bulk UPDATE (PG `UPDATE FROM VALUES` / MySQL `UPDATE JOIN VALUES ROW` on 8.0.19+, CASE WHEN fallback on older versions); optimistic-lock entities go through a packed DbBatch — measured 7.85× on a remote PG database (v5.9 probe 28: 15.1 ms vs 118.7 ms row-by-row, 200-command transaction).

**Enterprise features out of the box.** Multi-tenant column isolation (query cache keys automatically get a tenant prefix — cross-tenant hits are impossible), optimistic locking, soft delete, audit interceptors, read/write splitting (`ForRead`), advisory locks, resilient retry + circuit breaker (covering the read-only query pipeline automatically since v5.4). No boilerplate required.

**Session pooling** (v7.0 `DataSessionPool`). A fixed-cost eliminator for the request-per-session shape of Web services: when a rented session is returned, all transient state (tenant/timeout/retry overrides) resets, while command reuse slots, the read connection, and materialization capacity survive across rentals — reuse slots only promote on the 3rd same-shape operation within one session, so without pooling they never activate in a per-request lifetime. Measured at 32 concurrent: P99 −18%, Gen0 collections −58%, −2,576 B allocated per operation, with rent/return itself around 1 µs.

**SQL assets managed at compile time.** `[SqlFile]` embeds external .sql files as constants (supports `-- @pg/@mysql/@sqlite/@all` dialect sections; editing SQL needs no rebuild; a missing file fails the build); `[SqlTemplate]` promotes an interpolated string into a template constant, paired with `.AsPrepared()` for `DbCommand.PrepareAsync` pre-compilation; AutoTagging (opt-in build switch) uses C# `[InterceptsLocation]` to intercept 9 query terminal methods, automatically injecting a "file:line member" locating comment into the SQL — zero-cost troubleshooting.

**Enterprise operations surface.** `HealthCheckAsync` SELECT 1 probe (error messages expose only the exception type name, preventing database-topology leaks); a unified cross-dialect exception family — unique-key violations translate to `UniqueConstraintViolationException` (no driver error-code memorization), optimistic-lock conflicts to `ConcurrencyConflictException`, infrastructure timeouts carry the `PalORM.InfrastructureTimeout` marker, transient faults the `PalORM.IsTransient` marker; full OpenTelemetry (`WithTracing` ActivitySource + `WithMetrics` counters `palorm.query.executions`/`palorm.query.duration`; the query cache adds four `palorm.cache.*` metrics).

**Dialect-specific capabilities.** PG: UNNEST array parameters for bulk writes, transaction-scoped advisory locks (blocking/non-blocking × long-key/dual-int-key), a LISTEN/NOTIFY background listener (auto-reconnect + 30s heartbeat + zero-allocation event dispatch) with `NotifyAsync` sending, a pgvector text-boundary route (`::vector` write / `::text` parse, AOT-compatible), and JSONB path queries via `WhereJson`. MySQL: three-tier local_infile degradation (probe cache → re-probe on failure → multi-value INSERT fallback) and runtime version probing for the 8.0.19+ `UPDATE JOIN VALUES ROW` shape. SQLite: AES-256 at-rest encryption, a 9-item automatic PRAGMA tuning set (WAL/mmap/optimize statistics), and automatic BUSY/LOCKED transient-fault retry.

**Schema migration and seeding.** `MigrateAsync` runs the compile-time three-dialect DDL with FK dependencies topologically ordered and duplicate objects idempotently skipped, then refreshes statistics (SQLite `PRAGMA optimize` / PG `ANALYZE`); `SeedAsync` seeds idempotently inside a transaction; `ValidateSchemaAsync` diffs the database against entity declarations and returns missing columns.

**Zero-dependency core.** PalORM.Core references no third-party NuGet packages (BCL + ADO.NET abstractions only).

## 🆚 Comparison with mainstream ORMs

> Version baseline: PalORM 7.0.0 / Dapper 2.1.89 / RepoDb 1.16.0 (versions actually referenced by this repo's benchmark suite); EF Core 10.0.12 (comparison reference, latest 10.x stable on NuGet). Cell evidence in the notes below.

| Feature | **PalORM** | Dapper | EF Core | RepoDb |
|------|:---:|:---:|:---:|:---:|
| **Full-pipeline Native AOT** | ✓ source-generated, verified | △ Dapper.Aot optional (experimental interceptors) | ❌ experimental, not production-ready | ❌ reflection + IL Emit |
| **Compile-time type diagnostics** | ✓ 45 rules (42 analyzers + 3 generators), errors jump to declaration sites | ❌ fails at runtime | △ migration checks (design time) | ❌ fails at runtime |
| **Compile-time SQL pre-building** | ✓ Roslyn source generation | ❌ runtime concatenation | △ precompiled queries (experimental) | ❌ runtime expression trees |
| **Runtime reflection** | Zero | △ first-use reflection + IL Emit cache | △ expression-tree compilation | ❌ reflection + IL Emit |
| **Per-dialect bulk strategies** | ✓ COPY / BulkCopy / multi-value / `= ANY` arrays | ❌ hand-written multi-value SQL | △ varies by provider | △ BulkInsert SQL Server only |
| **Single-statement multi-row UPDATE** | ✓ FROM VALUES / UPDATE JOIN VALUES ROW / CASE WHEN | ❌ | ❌ ExecuteUpdate is single-value per WHERE only | ❌ |
| **Bulk UPSERT** | ✓ `BulkMergeAsync` (ON CONFLICT / ON DUPLICATE KEY, conflict updates tenant-scoped) | ❌ hand-written | ❌ raw SQL required | △ Merge SQL Server only |
| **Optimistic locking** | ✓ `[ConcurrencyCheck]` automatic; bulk updates via packed DbBatch | ❌ hand-written | ✓ `RowVersion` automatic | ❌ hand-written |
| **Soft delete** | ✓ `[SoftDelete]` automatic filtering (bulk delete becomes UPDATE) | ❌ | ✓ global query filters | ❌ |
| **Multi-tenant column isolation** | ✓ `[TenantAware]` compile time; cross-tenant guards on bulk writes | ❌ | △ manual implementation | ❌ |
| **Value conversion + enum storage** | ✓ `[Converter]` / three enum strategies (int32/int64/text) emitted at compile time | △ hand-written TypeHandler | ✓ ValueConverter / HasConversion (runtime) | △ TypeHandler |
| **Read-replica routing** | ✓ `ParallelReadScope` + `readFromReplica` on the query family | ❌ | △ manual multi-context setup | ❌ |
| **Automatic schema migration** | ✓ `MigrateAsync` + concurrent-race tolerance (duplicate objects skipped) | ❌ | ✓ Migrations (most complete) | △ |
| **OwnedJson compile-time safety** | ✓ `[OwnedJson]` + source generation | ❌ hand-written STJ | △ Owned Types (runtime) | ❌ |
| **Audit interceptor** | ✓ | ❌ | ✓ Interceptors | ❌ |
| **Advisory locks** | ✓ `pg_advisory_xact_lock` | ❌ | ❌ | ❌ |
| **Session-level SET** | ✓ `SessionSetupSql` | ❌ | ❌ | ❌ |
| **SQL file embedding** | ✓ `[SqlFile]` compile-time validation | ❌ | ❌ | ❌ |
| **Circuit breaker + retry** | ✓ built in (concurrency-safe HalfOpen probe slots) | ❌ needs Polly | △ execution strategies | ❌ |
| **Query cache** | ✓ `WithCache` bounded cache + TTL + `EvictQueryCache` narrowing | ❌ | ❌ third-party interceptor | ✓ native `cacheKey` argument |
| **Streaming consumption** | ✓ `QueryAsyncEnumerable` / `ForEachAsync` | △ hand-written CommandBehavior | ✓ native `AsAsyncEnumerable` | ❌ |
| **Observability (OTel)** | ✓ built-in `WithMetrics` / `WithTracing` | ❌ | △ third-party instrumentation package | ❌ |
| **SQL preview** | ✓ `AsDryRun` (SQL + parameters) | ❌ | ✓ `ToQueryString` | △ builder-level `GetString` |
| **Pessimistic locking** | ✓ chained `ForUpdate` / `ForShare` | ❌ hand-written SQL | △ not in the core (PG provider extension) | ❌ |
| **Navigation object graphs** | △ `Include` generates JOINs without assembly (micro-ORM trade-off; group by parent yourself) | △ hand-written multi-mapping | ✓ full `Include` assembly + change tracking | ❌ |
| **JSON column queries** | △ `WhereJson` JSONB path queries (PG only); mapping see `[OwnedJson]` above | △ hand-written SQL | ✓ EF7+ JSON column mapping + path queries (enhanced in EF10) | △ PropertyHandler mapping, no path queries |
| **CTE / window functions** | ✓ chained API | △ raw SQL strings | △ LINQ translation (partial) | △ raw SQL |
| **Multiple result sets** | ✓ `GridReader` | ✓ `QueryMultiple` | ❌ | ✓ `ExecuteQueryMultiple` |
| **Keyset paging** | ✓ `ToPageAsync` | ❌ | ❌ | ❌ |
| **Scaffold tool** | ✓ all three providers | ❌ | ✓ `dotnet ef dbContext scaffold` | ❌ |
| **Connection-string auto-tuning** | ✓ PG 6 / MySQL 5 / SQLite 9 settings | ❌ | ❌ | ❌ |
| **Session pooling** | ✓ `DataSessionPool` built-in (P99 −18% at 32 concurrent) | ❌ | ✓ `DbContextPool` | ❌ |
| **Stored procedures** | ✓ `StoredProc` with typed input/output parameters | ✓ `DynamicParameters` | △ `FromSqlRaw` indirect | ✓ `ExecuteQuery` |
| **Whole-transaction transient retry** | ✓ `WithTransactionRetry` (dialect error codes, at-least-once) | ❌ | ✓ `EnableRetryOnFailure` execution strategy | ❌ |
| **Database health check** | ✓ `HealthCheckAsync` | ❌ hand-written SELECT 1 | ✓ `CanConnect()` | ❌ |
| **SQL file / template assets** | ✓ `[SqlFile]` dialect sections + `[SqlTemplate]` constants, compile-time validated | ❌ | ❌ | ❌ |
| **SQL locating comments** | ✓ AutoTagging injects "file:line member" automatically (opt-in) | ❌ | △ `TagWith()` manual | ❌ |
| **pgvector** | ✓ text-boundary route (`::vector` write, AOT-compatible) | ❌ | △ third-party extension packages | ❌ |
| **Seed data** | ✓ `SeedAsync` idempotent upsert in a transaction | ❌ | ✓ `HasData` (design time) | ❌ |
| **BulkInsert memory** | ≈ 19% of Dapper | baseline | highest (ChangeTracker) | medium |
| **Core NuGet dependencies** | zero | zero | high (multi-package) | medium |
| **Target frameworks** | net11.0 (single target) | multi-target (netstandard2.0+) | multi-target (net8+) | multi-target (netstandard2.0+) |
| **License** | AGPL-3.0-only | Apache-2.0 | MIT | Apache-2.0 |

Core differentiators: compile-time generation + full-pipeline AOT compatibility + per-dialect bulk strategies (COPY / BulkCopy / multi-value / arrays). Dapper is fast but reflects at runtime; EF Core is feature-complete but heavy at runtime with AOT still experimental; RepoDb is also a micro-ORM but has no source generation, and its bulk support is SQL Server only.

<details>
<summary><b>Comparison evidence (sources for each cell)</b></summary>

- **Dapper**: `Dapper.Aot` (separate package, [aot.dapperlib.dev](https://aot.dapperlib.dev)) generates AOT interceptors via Roslyn interceptors — an experimental C# feature, not enabled by default.
- **EF Core 10**: LTS ([learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)). `ExecuteUpdateAsync` only supports a single value per WHERE clause and cannot set different values per row in one SQL statement; AOT is still experimental ([issue #35945](https://github.com/dotnet/efcore/issues/35945)); no built-in UPSERT API — `MERGE` semantics require raw SQL. Query cache requires a third-party interceptor ([EFCoreSecondLevelCacheInterceptor](https://github.com/VahidN/EFCoreSecondLevelCacheInterceptor)); OTel observability has no first-class built-in metrics, wired via [OpenTelemetry.Instrumentation.EntityFrameworkCore](https://www.nuget.org/packages/OpenTelemetry.Instrumentation.EntityFrameworkCore) (third-party, Experimental); pessimistic locking is not built into the core ([multiple sources](https://milanjovanovic.tech/blog/a-clever-way-to-implement-pessimistic-locking-in-ef-core)), PG scenarios rely on the Npgsql provider's `ForUpdate` extension with cross-dialect inconsistency; `ToQueryString` since EF Core 5+.
- **RepoDb**: BulkOperation is SQL Server only ([repodb.net/operation/bulkinsert](https://repodb.net/operation/bulkinsert): *"It is only supporting the SQL Server RDBMS."*); other dialects use packed statements; `Merge` belongs to the same BulkOperation family and is subject to the same limit. Query cache is operation-level `cacheKey` + a shared `ICache` instance ([official blog](https://blogs.repodb.net/posts/2026/09/caching-in-repodb)); SQL preview only exists at the builder level via `QueryBuilder.GetString()` (aimed at `IStatementBuilder` implementers), with no first-class operation-level preview API. No navigation assembly: the [official README](https://github.com/mikependon/repodb) comparison table positions "Rich object graphs" as EF's territory while claiming "EF-like productivity without losing SQL control". JSON support is PropertyHandler-based mapping (`JsonToEntity/JsonDocument` etc., [core release notes](https://repodb.net/release/core)), with no path-query building.
- **PalORM**: diagnostic count 45 = 42 analyzers + 3 generators (`docs/API参考.md`, Chinese); bulk-UPSERT tenant guard = `BulkMergeAsync` conflict updates scoped to the current tenant (2026-10 audit closure); read-replica routing = `readFromReplica` parameter on the query family + `ParallelReadScope`; session pool = `DataSessionPool<TProvider>` (Create/RentAsync; returning resets transient state, reuse slots survive across rentals); whole-transaction retry = `WithTransactionRetry` replays the entire transaction on transient codes PG 40001/40P01, MySQL 1213/1205, SQLite BUSY/LOCKED; SQL assets = `[SqlFile]` enters the incremental pipeline via AdditionalFiles (SQL edits need no rebuild) + `[SqlTemplate]` paired with `AsPrepared()`; AutoTagging = C# `[InterceptsLocation]` intercepting 9 query terminal methods (opt-in build switch, off by default).

</details>

## 📊 Performance

> **Test environment**: AMD Ryzen 9 8945HX (32 logical cores) · Windows 10 22H2 · .NET 11 RC1 · SQLite shared memory · PG 18.6 / MySQL 8.4.11 local containers · Dapper 2.1.89. Three arms (ADO.NET / Dapper / PalORM) collected in the same process and same round with identical connection settings; each arm uses its ecosystem's idiomatic best-practice code (ADO.NET is the hand-written performance floor).
>
> **Data batches**: 2026-10-14 `PerfCli full` batch (`history-20261014-191424.json`, medians + exact allocation counts). Latency = full-path median; allocation = exact counts (1024-based). Shared PG/MySQL servers swing ±30% between batches — cross-batch absolute values are not comparable; allocation is deterministic and stable across batches. Full methodology and reproduction commands: [benchmark methodology](docs/性能基准规范.md) (Chinese) and `bench/perfhub/report.html` (the same-day BDN gate matrix numbers are also in that report).

**Legend** (shared by latency and allocation, baseline = hand-written ADO.NET floor; positive % = slower/more than baseline):
🟢 strong win ≤−30% · 🟩 mid win −29%~−10% · 🔹 slight win −9%~−1% · 🔸 slight loss +1%~+9% · 🟧 mid loss +10%~+29% · 🟥 strong loss ≥+30% · **bold** = outside the 1.3×/0.7× significant band.

### Master table · Cross-dialect PalORM / ADO.NET latency ratio (ADO median → PalORM median)

| Operation | Tier | SQLite | PostgreSQL | MySQL |
|------|---:|:---:|:---:|:---:|
| GetByKey | 2K | 9.1 µs→6.2 µs **0.68** | 206 µs→199 µs 0.97 | 235 µs→225 µs 0.96 |
| GetByKey | 20K | 22.2 µs→21.9 µs 0.99 | 204 µs→205 µs 1.00 | 176 µs→217 µs 1.23 |
| QueryAll | 2K | 961 µs→955 µs 0.99 | 654 µs→646 µs 0.99 | 1.05 ms→1.33 ms 1.27 |
| QueryAll | 20K | 9.58 ms→9.81 ms 1.02 | 4.90 ms→4.91 ms 1.00 | 7.72 ms→7.98 ms 1.03 |
| StreamAll | 2K | 936 µs→1.00 ms 1.07 | 634 µs→636 µs 1.00 | 1.04 ms→1.40 ms **1.34** |
| StreamAll | 20K | 9.41 ms→9.95 ms 1.06 | 4.85 ms→4.84 ms 1.00 | 7.34 ms→8.44 ms 1.15 |
| Insert | 2K | 17.8 µs→17.2 µs 0.97 | 578 µs→543 µs 0.94 | 1.50 ms→1.64 ms 1.10 |
| Update | 2K | 7.2 µs→7.3 µs 1.01 | 604 µs→557 µs 0.92 | 199 µs→228 µs 1.14 |
| BulkInsert | 2K | 16.57 ms→16.26 ms 0.98 | 3.37 ms→3.43 ms 1.02 | 11.51 ms→10.92 ms 0.95 |
| BulkInsert | 20K | 153.30 ms→152.23 ms 0.99 | 17.38 ms→17.31 ms 1.00 | 82.69 ms→87.62 ms 1.06 |
| BulkUpdate | 2K | 2.29 ms→2.28 ms 1.00 | 10.41 ms→4.26 ms **0.41** | 28.07 ms→9.15 ms **0.33** |
| BulkUpdate | 20K | 25.08 ms→25.38 ms 1.01 | 104.02 ms→68.59 ms **0.66** | 281.47 ms→84.18 ms **0.30** |
| BulkDelete | 2K | 6.58 ms→5.12 ms 0.78 | 2.71 ms→1.55 ms **0.57** | 8.99 ms→8.30 ms 0.92 |
| BulkDelete | 20K | 65.95 ms→35.50 ms **0.54** | 24.02 ms→9.80 ms **0.41** | 66.29 ms→64.08 ms 0.97 |
| UpsertBatch | 2K | 15.96 ms→15.86 ms 0.99 | 11.18 ms→5.90 ms **0.53** | 7.87 ms→7.84 ms 1.00 |
| UpsertBatch | 20K | 149.77 ms→148.63 ms 0.99 | 122.40 ms→71.64 ms **0.59** | 72.12 ms→76.57 ms 1.06 |

**How to read**: bulk write paths are on par with or better than the ADO.NET floor across all dialects (0.78~1.06), with three dialect-specific fast paths clearly ahead of the hand-written floor — MySQL bulk UPDATE via `UPDATE JOIN VALUES ROW` at 3~3.4×, the PG bulk family via array parameters (`= ANY`) and Binary COPY at 1.5~2.4×, and SQLite BulkDelete at 1.9× thanks to pooled command/parameter reuse; single-row reads sit at 0.68~1.00 of the floor on SQLite/PG and 0.96~1.23 on MySQL (ratios driven by ADO-floor bimodal swings between batches — judge by PalORM absolute values). The MySQL 2K-tier QueryAll 1.27 / StreamAll 1.34 gap has reproduced across multiple batches (stable ADO floor — a persistent gap, not batch noise, not yet attributed; awaiting a dedicated re-run). The MySQL 20K large-result-set slow state registered on 10-07 (once +51~62%) has returned to the normal band (1.03~1.15).

### SQLite detail · Latency and allocation (three arms)

**CRUD single-row and reads**

| Operation | Tier | ADO.NET | Dapper | PalORM | P/ADO | P/Dapper | Alloc A/D/P | Alloc ΔADO (D/P) |
|------|---:|---:|---:|---:|:---:|:---:|:---:|:---:|
| GetByKey | 2K | 9.1 µs | 6.8 µs | 6.2 µs | **0.68🟢** | 0.91🔹 | 2.1/2.8/2.7 KB | +30% / +27% |
| GetByKey | 20K | 22.2 µs | 15.8 µs | 21.9 µs | 0.99🔹 | **1.39🟥** | 2.1/2.8/2.8 KB | +32% / +30% |
| QueryAll | 2K | 961 µs | 1.34 ms | 955 µs | 0.99🔹 | 0.71🟩 | 308/513/293 KB | +66% / −5% |
| QueryAll | 20K | 9.58 ms | 13.51 ms | 9.81 ms | 1.02🔸 | 0.73🟩 | 3.3/5.3/2.9 MB | +61% / −11% |
| StreamAll | 2K | 936 µs | 1.37 ms | 1.00 ms | 1.07🔸 | 0.73🟩 | 276/481/278 KB | +74% / +1% |
| StreamAll | 20K | 9.41 ms | 13.84 ms | 9.95 ms | 1.06🔸 | 0.72🟩 | 2.8/4.8/2.8 MB | +72% / +0% |
| Insert | 2K | 17.8 µs | 21.1 µs | 17.2 µs | 0.97🔹 | 0.82🟩 | 2.8/3.6/2.7 KB | +28% / −5% |
| Update | 2K | 7.2 µs | 9.0 µs | 7.3 µs | 1.01🔸 | 0.81🟩 | 2.6/3.3/3.2 KB | +30% / +24% |
| InsertReturningId | 2K | 26.7 µs | 26.6 µs | 26.5 µs | 0.99🔹 | 1.00⚪ | 1.9/2.3/2.3 KB | +24% / +22% |

**Bulk operations**

| Operation | Tier | ADO.NET | Dapper | PalORM | P/ADO | P/Dapper | Alloc A/D/P | Alloc ΔADO (D/P) |
|------|---:|---:|---:|---:|:---:|:---:|:---:|:---:|
| BulkInsert | 2K | 16.57 ms | 103.54 ms | 16.26 ms | 0.98🔹 | **0.16🟢** | 1.7/7.9/1.6 MB | +373% / −6% |
| BulkInsert | 20K | 153.30 ms | 1043.71 ms | 152.23 ms | 0.99🔹 | **0.15🟢** | 14.9/79.4/14.7 MB | +432% / −1% |
| BulkUpdate | 2K | 2.29 ms | 8.45 ms | 2.28 ms | 1.00⚪ | **0.27🟢** | 1.7/6.4/1.8 MB | +280% / +4% |
| BulkUpdate | 20K | 25.08 ms | 249.50 ms | 25.38 ms | 1.01🔸 | **0.10🟢** | 17.0/64.9/17.6 MB | +282% / +4% |
| BulkDelete | 2K | 6.58 ms | 21.47 ms | 5.12 ms | 0.78🟩 | **0.24🟢** | 628 KB/1.6 MB/368 KB | +155% / −41% |
| BulkDelete | 20K | 65.95 ms | 213.55 ms | 35.50 ms | **0.54🟢** | **0.17🟢** | 6.1/15.6/2.6 MB | +155% / −57% |
| UpsertBatch | 2K | 15.96 ms | 102.50 ms | 15.86 ms | 0.99🔹 | **0.15🟢** | 1.7/7.9/1.6 MB | +379% / −4% |
| UpsertBatch | 20K | 149.77 ms | 1027.81 ms | 148.63 ms | 0.99🔹 | **0.14🟢** | 14.8/79.5/14.7 MB | +439% / −0% |

The four bulk shapes match the ADO.NET floor line by line (P/ADO 0.78~1.01); BulkDelete beats the floor via pooled command and parameter reuse (full batches only write `Value`; 0.54 at the 20K tier); Dapper's multi-value INSERT at the 20K tier is 6.8× slower with 5.3× allocations due to giant SQL string construction.

**Transactions**

| Operation | Tier | ADO.NET | Dapper | PalORM | P/ADO | P/Dapper | Alloc A/D/P | Alloc ΔADO (D/P) |
|------|---:|---:|---:|---:|:---:|:---:|:---:|:---:|
| TxSingleInsert | 2K | 19.9 µs | 19.8 µs | 19.8 µs | 0.99🔹 | 1.00⚪ | 4.0/4.8/4.4 KB | +20% / +11% |
| TxHundredInserts | 2K | 183 µs | 235 µs | 235 µs | 1.28🟧 | 1.00⚪ | 137/220/93.1 KB | +61% / −32% |
| TxBulkInsert | 2K | 16.51 ms | 103.14 ms | 16.16 ms | 0.98🔹 | **0.16🟢** | 1.7/7.9/1.6 MB | +373% / −6% |
| TxRollback | 2K | 572 µs | 2.10 ms | 816 µs | **1.43🟥** | **0.39🟢** | 435 KB/1.6 MB/437 KB | +279% / +1% |

### Specialized measurements

**Native AOT publish size**

| Dialect | exe size | Publish directory |
|------|:---:|:---:|
| SQLite | 4.5 MB | 26 MB |
| PostgreSQL | 11.6 MB | 61 MB |
| MySQL | 9.5 MB | 47 MB |

### Performance tip for query building: hoist expression trees to static fields

`OrderBy` / `Select` / `GroupBy` / `WhereIn` / `Set` / `Include` and friends accept `Expression<Func<T, ...>>`. C# constructs the expression tree at the call site — the cost is already paid by the time the library sees it and cannot be cached in-library; measured at 512 bytes plus 0.5–1.6 µs per tree. Hoisting the lambda to a static field eliminates it:

```csharp
// The expression tree is rebuilt on every call
await db.From<Order>().OrderBy(o => o.CreatedAt).ToListAsync();

// The expression tree is constructed once, then reused
private static readonly Expression<Func<Order, DateTime>> ByCreatedAt = o => o.CreatedAt;
await db.From<Order>().OrderBy(ByCreatedAt).ToListAsync();
```

| Scenario | Allocation reduction | Time reduction |
|------|:---:|:---:|
| `UPDATE` + `Set(...)` | −18.0% | −18.3% |
| Single-row query + `OrderBy(...)` | −12.4% | −7.2% |
| `WhereIn(500)` | −0.72% | −1.7% |
| 10K-row query | Amortized to negligible by the result set | Same |

Worth doing when queries are frequent, rows per query are few, and builder methods are on hot paths; unnecessary for bulk and reporting workloads. `Where` / `OrWhere` / `Having` take `FormattableString` and construct no expression trees — nothing to do there.

## 📦 Installation

> Pick one of the three provider packages; each automatically brings `PalORM.Core` (runtime) and `PalORM.SourceGen` (compile-time generator).
> Requirements: .NET SDK `11.0.100-preview.6` or later (`global.json` pins `rollForward: latestMinor`); your IDE must support Roslyn source generators (SourceGen builds against Microsoft.CodeAnalysis 5.9.0). Native AOT publishes additionally need about 4 GB of memory (ILC compiler) — see [Development](#-development).

```xml
<!-- PostgreSQL -->
<PackageReference Include="PalORM.PostgreSql" Version="7.0.0" />
<!-- MySQL -->
<PackageReference Include="PalORM.MySql" Version="7.0.0" />
<!-- SQLite -->
<PackageReference Include="PalORM.Sqlite" Version="7.0.0" />
```

Each provider package pulls in `PalORM.Core` (runtime) and `PalORM.SourceGen` (compile-time source generator). After installing, verify with the minimal Quick Start example below: if you can create a session and complete one insert, the install works.

### Database compatibility

| Database | Version | Driver | Encryption |
|----------|---------|--------|:---:|
| PostgreSQL | 14+ (18 recommended) | Npgsql 10.0.3 | SSL/TLS |
| MySQL | 8.0+ (8.4 LTS recommended) | MySqlConnector 2.6.2 | SSL/TLS |
| SQLite | 3.47+ (via SQLite3MC 2.4.0) | Microsoft.Data.Sqlite.Core 11.0.0-rc.1 | AES-256 (driver level, `Password=`) |

## 🚀 Quick Start

> Five steps to a running app: define entities → create a session → migrate and seed → CRUD → bulk operations. All snippets combine into a minimal runnable program.

### Define an entity

```csharp
using PalORM;

[Table("users")]
public partial class User
{
    [Key] public long Id { get; set; }
    [Column("email")] public string Email { get; set; } = "";
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("metadata")]
    [OwnedJson(typeof(UserJsonContext))]
    public UserMetadata? Metadata { get; set; }
}
```

> **Nullability convention**: when a reference-type property is declared non-nullable (e.g. `string Email`) but the column stores NULL, reads throw `SqlNullValueException` (fail loudly — never return null, never produce silently wrong data). Declare columns that may hold NULL as nullable (`string?`). The evaluation of alternatives is documented in `docs/性能优化方案-step7-pg-master.md` T13 (Chinese).

### Create a session

```csharp
using var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = "Host=localhost;Username=user;Password=xxx;Database=mydb"
});
```

Optional: pre-warm the connection pool at startup (v5.6.0) so the first burst of queries hits warm connections instead of paying connection setup each (measured ~8.5 ms per remote connection):

```csharp
// Opens N connections and returns them to the pool; SQLite has no pool and returns immediately.
// Pair with MinPoolSize to keep them warm
await DataSession<PostgreSqlProvider>.PreWarmAsync(options, count: 10);
```

### Migrate and seed

```csharp
// Runs the compile-time three-dialect DDL (idempotent: existing objects skipped; tables created in FK dependency order)
await db.MigrateAsync();

// Seed data: idempotent upsert on stable primary keys inside a transaction
await db.SeedAsync(new[] { new User { Id = 1, Email = "admin@example.com", CreatedAt = DateTime.UtcNow } });

// Diff the database against entity declarations (returns missing columns — for upgrades/audits)
List<string> missing = await db.ValidateSchemaAsync<User>();
```

### CRUD

```csharp
// Insert (auto-increment backfilled via PG/SQLite RETURNING / MySQL LAST_INSERT_ID)
var user = await db.InsertAsync(new User { Email = "alice@example.com", CreatedAt = DateTime.UtcNow });

// Query
var alice = await db.GetAsync<User>(user.Id);   // single-key lookup: prefer the dedicated API
var all = await db.From<User>().Where($"email LIKE {"%@example.com%"}").ToListAsync();

// Update (optimistic-lock entities verify the version automatically; conflicts throw ConcurrencyConflictException)
alice.Email = "new@example.com";
await db.UpdateAsync(alice);

// Insert-or-update (default key inserts, non-default key does a single-row UPSERT)
await db.SaveAsync(alice);

// Delete (soft-delete entities turn into an UPDATE of deleted_at; otherwise physical DELETE)
await db.DeleteAsync<User>(alice.Id);
```

### Bulk operations

```csharp
// Bulk insert (PG: Binary COPY / MySQL: BulkCopy or multi-value INSERT / SQLite: multi-value INSERT)
await db.BulkInsertAsync(users);

// Bulk update (row-by-row + optimistic lock; since v5.9 concurrent entities on MySQL/PG go through a packed DbBatch)
await db.BulkUpdateAsync(users);

// Bulk update (v5.0 single-statement batch)
await db.BulkUpdateBatchAsync(users);

// Bulk delete (PG: single = ANY(array) statement / MySQL & SQLite: batched IN statements, full-batch command & parameter pooling)
await db.BulkDeleteAsync<User>(keyList);

// Bulk UPSERT (single multi-row UPSERT statement; default-key new rows go row-by-row INSERT to backfill identity IDs)
await db.BulkMergeAsync(users);
```

Per-dialect SQL strategies:

| Method | PG | MySQL | SQLite |
|------|------|------|------|
| `BulkInsertAsync` | Binary COPY | BulkCopy (local_infile) or multi-value INSERT | Multi-value INSERT |
| `BulkUpdateAsync` | Row-by-row + optimistic lock (packed DbBatch) | Row-by-row + optimistic lock (packed DbBatch) | Row-by-row + optimistic lock |
| `BulkUpdateBatchAsync` | `FROM VALUES` | `UPDATE JOIN VALUES ROW` (8.0.19+; CASE WHEN fallback on older) | Automatic row-by-row fallback |
| `BulkDeleteAsync` | Single `= ANY(array)` statement | Batched `IN` statements | Batched `IN` statements |
| `BulkMergeAsync` | Multi-row UPSERT (`ON CONFLICT DO UPDATE`) | Multi-row UPSERT (`ON DUPLICATE KEY UPDATE`) | Multi-row UPSERT (`ON CONFLICT DO UPDATE`) |

`BulkMergeAsync` (set-based since v5.6.0) partitions by key state: default-key rows (new) go row-by-row INSERT to preserve the ID-backfill contract; non-default-key rows are batched by dialect parameter limits into multi-row UPSERT (SQLite 999 / PG and MySQL 65535 parameter limit; MySQL ODKU batches of 1000 rows); `[ConcurrencyCheck]` entities stay row-by-row (UPSERT cannot honor optimistic locking).

## 📖 Usage

> Queries and paging, aggregation and streaming, transactions and resilience, cross-cutting concerns, raw SQL and stored procedures, scaffolding, bulk API selection and shape-based selection.

### Queries and paging

`From<T>()` returns a `struct QueryBuilder<T>` with chaining `.Where()` / `.OrderBy()` / `.Take()` / `.Skip()` / `.Select()` / `.GroupBy()` / `.Having()` / `.Include()` / `.ThenInclude()`. Supports `InnerJoin` / `LeftJoin` / `RightJoin`, `WhereIn` / `WhereNotIn` (automatic batching), CTEs (`.With()`), window functions, pessimistic locking (`ForUpdate` / `ForShare`), SQL preview (`AsDryRun`), query caching (`WithCache`), and observability (`WithMetrics` / `WithTracing`, OTel metrics and tracing).

Declare a parallel-read lease before running multiple queries concurrently on one session:

```csharp
await using (db.ForParallelReads())
{
    await Task.WhenAll(db.From<User>().ToListAsync(), db.From<Order>().ToListAsync());
}
```

Keyset cursor paging (stable at large offsets where OFFSET degrades; returns the row list and total count):

```csharp
// First page: top 20 rows ordered by CreatedAt descending
var (rows, total) = await db.From<Order>().ToPageAsync(20, o => o.CreatedAt);

// Next page: the last row's sort value from the previous page is the cursor
var next = await db.From<Order>().ToPageAsync(20, o => o.CreatedAt, lastValue: rows[^1].CreatedAt);
```

The five aggregate functions (soft-delete and tenant filters apply automatically):

```csharp
long count = await db.CountAsync<Order>($"status = {OrderStatus.Paid}");
decimal sum = await db.SumAsync<Order>(o => o.Amount);
double avg = await db.AvgAsync<Order>(o => o.Amount);
DateTime? latest = await db.MaxAsync<Order, DateTime>(o => o.CreatedAt);
```

Streaming large result sets (neither form materializes a full `List<T>`; peak memory O(1)):

```csharp
// Callback form: consume row by row, no list built
await db.From<Order>().ForEachAsync(order => sink.Write(order));

// Async-enumerable form: await foreach over a raw-SQL result
await foreach (var order in db.QueryAsyncEnumerable<Order>($"SELECT * FROM orders WHERE status = {status}"))
    sink.Write(order);
```

Multiple result sets (`GridReader`):

```csharp
await using var grid = await db.From<User>()
    .QueryMultipleAsync($"SELECT * FROM users WHERE id = {userId}; SELECT * FROM orders WHERE user_id = {userId}");
var user = await grid.ReadFirstAsync<User>();
var orders = await grid.ReadAsync<Order>().ToListAsync();
```

### Transactions and resilience

Functional transactions `WithTransaction(callback)` commit/roll back automatically and support savepoints:

```csharp
await db.WithTransaction(async ct =>
{
    await db.InsertAsync(order, ct);
    await db.BulkInsertAsync(order.Items, ct);
    await db.ExecuteAsync($"UPDATE inventory SET stock = stock - {order.Items.Count} WHERE product_id = {productId}", ct);
});
```

Whole-transaction transient-fault retry (replays the transaction per dialect error codes, at-least-once contract: PG 40001/40P01, MySQL 1213/1205, SQLite BUSY/LOCKED); savepoints and external transactions:

```csharp
// Transaction retry: deadlock/lock-timeout replays the whole transaction (the callback must be idempotent)
await db.WithTransactionRetry(async ct => await db.BulkInsertAsync(items, ct: ct));

// Savepoints: partial rollback inside a long transaction
await using var tran = await db.BeginTransactionAsync();
await db.InsertAsync(order);
await db.SavepointAsync(tran, "sp1");
await db.ExecuteAsync($"UPDATE ...");           // on failure, roll back only to sp1
await db.RollbackToAsync(tran, "sp1");
await tran.CommitAsync();

// External transaction adoption (after the external Dispose, further commands fail loudly — no silent auto-commit)
db.UseTransaction(externalTran);

// Isolation level: session-level setting; subsequent BeginTransactionAsync calls pick it up
db.WithIsolationLevel(IsolationLevel.ReadCommitted);
await using var tran2 = await db.BeginTransactionAsync();
```

Resilience policies (`WithRetry` exponential backoff + `WithCircuitBreaker`) have covered the read-only query pipeline automatically since v5.4:

```csharp
// The Production preset already includes resilience (MaxRetries=5 / breaker threshold 10 / 60s half-open / Process scope)
await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(
    DbOptions.Production(connectionString));

// Override thresholds on the session when needed (methods live on DataSession, returning the same session)
db.WithRetry(maxRetries: 3)
  .WithCircuitBreaker(failureThreshold: 5, resetAfter: TimeSpan.FromSeconds(30));

// Transient SELECT failures (deadlocks/timeouts/connection drops) retry automatically;
// non-idempotent writes are not retried automatically — declare resilience intent explicitly
long affected = await db.ExecuteWithResilience(
    token => db.From<Order>()
        .Set(o => o.Status, OrderStatus.Paid)
        .Where($"id = {orderId}")
        .ExecuteNonQueryAsync(token), ct);
```

| Path | Resilience | Notes |
|------|:---:|------|
| `From<T>()` SELECT family, `GetAsync`/`GetAllAsync`, aggregates | Automatic | Transient failures retried per config and counted toward the breaker |
| Connection establishment | Automatic | `CreateAsync` has its own retry loop |
| Write paths (Insert/Update/Delete/Save/Bulk/stored procedures) | Direct | Auto-retrying non-idempotent writes risks double execution; use `ExecuteWithResilience` when explicitly needed |
| Queries inside transactions / `ToPageAsync` / raw SQL family | Direct | Retrying inside a transaction masks the root cause with secondary exceptions |

The per-query constant overhead of resilience on read-only queries is about 272 B allocated, independent of row count (+8% single-row, +0.2% at a thousand rows); `MaxRetries=0` + `CircuitBreakerThreshold=0` gives a fully direct path at the cost of losing timeout exceptions tagged `PalORM.InfrastructureTimeout`.

PG transaction-scoped advisory locks (`TryAcquireXactLockAsync` is the non-blocking variant; must be called inside a transaction — acquiring outside one releases immediately and reports an explicit error):

```csharp
await db.WithTransaction(async ct =>
{
    await db.AcquireXactLockAsync(resourceKey, ct);
    // critical section...
});
// the lock is released automatically when the transaction ends
```

### Cross-cutting concerns

```csharp
// Audit interceptor (v5.0): with logParameters:true, parameter values of [SensitiveData] columns are masked automatically
var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = connectionString,
    Interceptors = [new AuditInterceptor(loggerFactory.CreateLogger("Audit"))],
    SessionSetupSql = "SET TIME ZONE 'UTC'; SET search_path TO 'app, public'"
});
```

⚠️ `AuditInterceptor` coverage: entity SELECT pipeline, QueryBuilder UPDATE, and `ExecuteAsync` (integrated in v5.6.0); `InsertAsync`/`DeleteAsync`/`SaveAsync`/the Bulk family/stored procedures/migrations produce no audit records — for complete write auditing use database-level auditing or OpenTelemetry.

Other cross-cutting capabilities: `[SoftDelete]` automatic WHERE filtering, `[TenantAware]` + `WithTenant(id)` single-database column isolation (cache keys automatically prefixed `__t:{tenantId}:`; `IgnoreFilters()` uses a separate `__all__:` namespace), `[ConcurrencyCheck]` optimistic locking, `ForRead` read/write splitting. All 23 annotations and execution-method details: [API reference](docs/API参考.md) (Chinese).

### Raw SQL and SQL files

```csharp
var count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM users WHERE email LIKE {"%@example.com%"}");
```

SQL files are embedded at compile time, validated for existence, and can extract dialect sections on demand:

```csharp
public static partial class Reports
{
    [SqlFile("Reports/MonthlySales.sql")]
    public static partial string MonthlySales();

    // Provider = "pg" extracts only the -- @pg dialect section inside the file
    [SqlFile("Reports/Sales.sql", Provider = "pg")]
    public static partial string SalesPg();
}

// MonthlySales() returns the compile-time-embedded SQL text (the generator validates the file exists),
// executed via QueryAsync / ScalarAsync and friends
```

`[SqlTemplate]` promotes an interpolated string into a template constant (the method must be parameterless and non-generic; interpolation holes may only reference statically resolvable symbols), so repeated SQL skips per-call string construction, pairing with the query builder's `.AsPrepared()` for `DbCommand.PrepareAsync` pre-compilation:

```csharp
public static class UserQueries
{
    [SqlTemplate("ActiveUsers")]
    public static FormattableString ActiveUsers()
        => $"SELECT * FROM users WHERE active = 1 AND deleted_at IS NULL";
}

// The template method returns a FormattableString — feed it straight to the raw-SQL family
var users = await db.QueryAsync<User>(UserQueries.ActiveUsers());
```

PG-specific: `WhereJson` (JSONB path queries), `PgNotificationListener` (async NOTIFY/LISTEN listener with automatic reconnection), and `NotifyAsync` (parameterized `pg_notify()` sending).

### Stored procedures and health checks

```csharp
// Stored procedures: typed input/output parameters, procedure-name allowlist validation, single-use contract
var sp = db.StoredProc("sp_transfer_stock")
    .WithParam("from_sku", fromSku)
    .WithParam("qty", qty)
    .WithOutputParam<int>("rows_affected");
int affected = await sp.ExecuteAsync(ct);             // returns affected rows
int? moved = sp.GetOutputValue<int>("rows_affected"); // typed output-parameter retrieval

// Stored-procedure result sets materialize directly
List<StockRow> rows = await sp.QueryAsync<StockRow>(ct);

// Health check: SELECT 1 probe (error messages expose only the exception type name, preventing topology leaks)
HealthResult health = await db.HealthCheckAsync();

// Unique-key violations translate uniformly across dialects (no driver error-code memorization)
try { await db.InsertAsync(user); }
catch (UniqueConstraintViolationException) { /* email already registered */ }

// Optimistic-lock conflicts (version mismatch on UpdateAsync of a [ConcurrencyCheck] entity)
try { await db.UpdateAsync(order); }
catch (ConcurrencyConflictException) { /* re-read and merge */ }
```

When you need driver-native capabilities, `db.GetRawConnection()` returns the underlying `DbConnection` escape hatch.

### Scaffold tool

Reverse-generate entities from an existing database:

```bash
dotnet run --project tools/PalORM.Scaffold -- <connection-string> --dialect sqlite|pg|mysql [--namespace NS] [--output DIR]
```

Schema-to-C#-entities for all three providers, 40+ type mappings (`uuid` → `Guid`, `jsonb` → `string`, `bytea` → `byte[]`, `date` → `DateOnly`, `time` → `TimeOnly`).

### Bulk API selection cheat sheet

| Scenario | Use | Avoid | Why |
|------|-----|------|------|
| Bulk insert, no ID backfill needed | `BulkInsertAsync` | row-by-row / `SessionBatch` | Fastest path per dialect (COPY / LOAD DATA / multi-value); measured 8.4× vs row-by-row for 100 rows in a transaction |
| Bulk insert with per-row identity backfill | `BulkMergeAsync` (default-key rows backfill) or `SessionBatch` | `BulkInsertAsync` | COPY / LOAD DATA do not backfill IDs |
| Mixed statements or per-row control | `SessionBatch` (packed DbBatch) | row-by-row round trips | Multiple statements per round trip; SQLite 20 statements per batch |
| Bulk update by non-key columns (different values per row) | `BulkUpdateBatchAsync` | row-by-row | Single multi-row SET statement (PG `FROM VALUES` / MySQL `JOIN VALUES ROW`) |
| Bulk update of optimistic-lock entities | `BulkUpdateAsync` | `BulkUpdateBatchAsync` | UPSERT/single-statement shapes cannot verify versions per row; DbBatch packing preserves semantics |
| Insert-or-update (update on key conflict) | `BulkMergeAsync` | hand-written ON CONFLICT | Tenant entities are automatically scoped to the current tenant (cross-tenant hits match 0 rows) |
| Large key-set deletes (tens of thousands and up) | `BulkDeleteAsync` | per-row Delete | Automatic batching by dialect parameter limits (SQLite 999 / MySQL·PG thousands), one transaction over the whole call, pooled command/parameter reuse across full batches |

## ✅ Best-practice cheat sheet

Grouped by frequency; detailed rationale at each linked section:

**Session and connections**
- Reuse one `DataSession` per scope; do not `CreateAsync` per operation (from the 3rd same-shape operation the session reuses the command and parameter slots automatically)
- Web/API request-per-session deployments should use `DataSessionPool` (create the pool at startup, `RentAsync` to rent, `DisposeAsync` to return): reuse slots surviving across rentals is the core pooling benefit; without pooling they never activate in that shape
- Pre-warm the connection pool at startup with `PreWarmAsync` (remote connection setup measured ~8.5 ms each), paired with `MinPoolSize` to stay warm
- High-frequency single-key lookups use `GetAsync` (25~30% faster and 880 B cheaper than the chained equivalent); use `From<T>()` only with filter conditions
- With `ReadConnectionString` configured, put report/list queries on `.ForRead()` explicitly; replication-lag-sensitive strong reads stay on the primary
- Declare `await using (db.ForParallelReads())` before concurrent queries; do not call `GetAsync` inside the scope (the entry restriction fails loudly)
- A "not registered" error for cross-assembly models: each model assembly registers via `ModuleInitializer` on first touch, so make sure entity types are genuinely referenced (instantiation/member access both count as touching)

**Queries and result sets**
- Stream unbounded tables with `QueryAsyncEnumerable<T>` (peak memory O(1)) instead of `ToListAsync`
- Deep paging uses `ToPageAsync` keyset cursors, not large OFFSETs (scan volume grows linearly with offset)
- Filter large key sets with `WhereIn`/`WhereNotIn` (automatic 500-key batching, parameter-cap clamping, empty sets safely false)
- Hoist hot-path `OrderBy`/`Select` expressions to static fields (saves 512 B + 0.5~1.6 µs per call)
- Preview SQL with `AsDryRun()`/`ToSql` (SQL + parameters, no execution) when troubleshooting; in production, enable AutoTagging (`PalORMAutoTagging` build switch) to auto-inject "file:line member" comments into SQL
- `Include`/`ThenInclude` generate JOINs without navigation assembly (group by parent yourself); for column-trimmed materialization use `[Projection]` DTOs + `QueryAsync<T>` (`Select` projection only works with DryRun/ToSql)
- `WithCache` is off by default; after enabling, use `db.EvictQueryCache()` for read-after-write consistency

**Writes and bulk operations**
- Pick bulk APIs by the cheat sheet above; never loop `InsertAsync` (round trips are an order-of-magnitude difference)
- Catch `UniqueConstraintViolationException` for unique-key conflicts (unified across dialects, no driver error codes to memorize); catch `ConcurrencyConflictException` for optimistic-lock conflicts (re-read, merge, retry)
- Never call `BulkUpdateBatchAsync` on `[ConcurrencyCheck]` entities (single-statement shape cannot verify per-row versions; PALORM031 already blocks it at compile time); use `BulkUpdateAsync` (packed DbBatch preserves semantics)
- `BulkInsertAsync` does not backfill auto-increment IDs (COPY / LOAD DATA contract); for per-row backfill use `BulkMergeAsync` (default keys go row-by-row) or `SessionBatch`
- Declare nullable properties for columns that may hold NULL (`string?`); non-nullable properties fail loudly with `SqlNullValueException` on NULL (never silently null)
- Mark sensitive columns with `[SensitiveData]` (auto-masked in audit logs and interceptors); do not rely on automatic retry for non-idempotent writes (write paths are direct), wrap with `ExecuteWithResilience` only after assessing duplication risk

**Transactions**
- Inside a transaction prefer `BulkInsertAsync` over `SessionBatch` (100 rows: 5.4ms vs 16.8ms)
- Queries and raw SQL inside transactions are not retried automatically (retries would mask the root cause with secondary exceptions); use `WithTransactionRetry` for transaction-level tolerance (replays the whole transaction on dialect transient codes) with an idempotent callback (at-least-once contract)
- Nested transactions are unsupported (a second `BeginTransactionAsync` throws loudly); use savepoints `SavepointAsync`/`RollbackToAsync` for partial rollback inside long transactions
- After adopting an external transaction via `UseTransaction`, using the session after the external Dispose fails loudly (no silent auto-commit)

**Multi-tenancy and cross-cutting concerns**
- Call `WithTenant(id)` right after creating a session for `[TenantAware]` entities, before the first query (cache-key prefixes follow the tenant)
- Use `IgnoreFilters()` narrowly for cross-tenant administrative queries and return to the tenant context immediately
- `AuditInterceptor` covers the entity SELECT pipeline / QueryBuilder UPDATE / `ExecuteAsync`; `InsertAsync`/`DeleteAsync`/the bulk family/stored procedures produce no audit records — use database-level auditing or OpenTelemetry for full write auditing
- With the PG tuning `NoResetOnClose=true`, raw-SQL `SET`s/temp tables stay visible across pool tenants (ITM-652): use a dedicated connection (`Max Pool Size=1`) or an explicit `DISCARD` when isolation is needed

**AOT and deployment**
- `[OwnedJson]` object shapes must pair with a source-generated System.Text.Json `JsonSerializerContext` (a hard AOT-safety requirement; missing contexts fail at compile time with PALORM008-010)
- Enable `<ServerGarbageCollection>true</ServerGarbageCollection>` on high-throughput servers; set `GarbageCollectionHeapHardLimit` in containers to prevent OOM (allocation numbers are unchanged under Server GC; GC frequency and pause distribution differ)
- When deploying a single dialect, set the MSBuild property `PalORMTargetDialects` to trim the CRUD SQL emission surface (DDL always emits all dialects); AOT publishing needs ~4 GB of memory (the ILC compiler)

**Operations and troubleshooting**
- Use `DbOptions.Production` in production (resilience and pooling presets included); override only what you actually need
- For short-lived per-request sessions: the `Production` preset already defaults to `CircuitBreakerScope.Process` (failures accumulate across sessions, so a persistently failing database trips the breaker and requests fail fast); when building `DbOptions` by hand, set `CircuitBreakerScope.Process` explicitly, Session-scope counts die with the session, so with per-request sessions the threshold is practically unreachable
- Wire `HealthCheckAsync` into health-check endpoints (SELECT 1 probe; error messages expose only the exception type name, preventing database-topology leaks)
- Wire observability into OTel: `WithTracing` + `WithMetrics` (counters `palorm.query.executions`/`palorm.query.duration`; the query cache adds four `palorm.cache.*` metrics)
- Timeout exceptions tagged `PalORM.InfrastructureTimeout` are infrastructure-side (network/server): check the environment before the code; classify transient faults with the `Data["PalORM.IsTransient"]` marker
- On MySQL, check server-side `max_allowed_packet` first when bulk writes report `ER_NET_PACKET_TOO_LARGE`; LOAD DATA falling back to multi-value INSERT when `local_infile` is off is expected behavior (not an error)

## 🔧 Configuration

> Start from the three presets (Development / Production / Testing); override any of the 20 settings as needed. Connection-string auto-tuning only fills driver defaults and never overrides explicit values.

### Presets

```csharp
var dev  = DbOptions.Development(connectionString);            // development
var prod = DbOptions.Production(connectionString, readConn);  // production (pooling + read/write splitting)
var test = DbOptions.Testing(connectionString);               // testing (zero retries + short timeouts)
var env  = DbOptions.FromEnvironment("PALORM_CONNECTION");    // environment variables (Docker/K8s friendly)
```

Environment variables supported by `FromEnvironment`: `PALORM_CONNECTION` (required), `PALORM_READ_CONNECTION`, `PALORM_COMMAND_TIMEOUT`, `PALORM_CONNECTION_TIMEOUT`, `PALORM_MAX_RETRIES`, `PALORM_CIRCUIT_BREAKER_THRESHOLD`, `PALORM_MAX_POOL_SIZE`; the result is `Validate()`d automatically so invalid values fail at startup.

### Options

| Property | Type | Default | Notes |
|------|------|:---:|------|
| `ConnectionString` | `string` (required) | — | Primary connection string. Supports `$ENV:VAR_NAME` environment-variable references |
| `ReadConnectionString` | `string?` | null | Read replica connection string. When set, `ForRead()` routes automatically |
| `ConnectionTimeout` | `TimeSpan` | 15s | Connection establishment timeout (including retries); throws `TimeoutException` on expiry |
| `CommandTimeout` | `TimeSpan` | 30s | Command execution timeout; sub-second values round up to 1 second |
| `MaxRetries` | `int` | 3 | Max retries for transient failures, 0=disabled, capped at 10 (`Validate()` throws beyond it). Covers the read-only query pipeline; writes are not auto-retried |
| `RetryBackoff` | `Func<int, TimeSpan>?` | 100→200→400ms | Default backoff includes 50~100% jitter; custom retry intervals (parameter = retry count), negative values throw |
| `OverallDeadline` | `TimeSpan` | Zero (off) | Total budget per operation: on expiry, retries stop and a `TimeoutException` tagged `PalORM.InfrastructureTimeout` is thrown. Unset, worst-case wall clock ≈ (MaxRetries+1)×CommandTimeout+Σbackoff (about 120 s with defaults); should exceed a single `CommandTimeout` |
| `MaxPoolSize` | `int` | 100 | Pool upper bound. Ignored for SQLite (embedded database, no server-side pool) |
| `MinPoolSize` | `int` | 0 | v5.6.0. 0=keep driver default; positive values pass through — after idle trimming the pool keeps at least this many warm connections, eliminating latency spikes from rebuilding connections on bursts. Ignored for SQLite |
| `PoolIdleTimeoutSeconds` | `int` | 0 | Default 0 since v5.6.0 (30 earlier). 0=keep driver default (Npgsql 300s / MySqlConnector 180s); only positive values override, at the cost that the first query after an idle expiry rebuilds a physical connection (measured cross-subnet `SELECT 1`: 0.3 ms pooled vs 13.5 ms fresh) |
| `PoolLifetimeMinutes` | `int` | 60 | Maximum connection lifetime; connections are force-rebuilt on expiry |
| `PoolExplicitlyConfigured` | `bool` | false | true once `WithPool()` is set (internal marker) |
| `CircuitBreakerThreshold` | `int` | 5 | Consecutive-failure threshold, 0=disable the breaker |
| `CircuitBreakerResetAfter` | `TimeSpan` | 30s | Wait before the breaker moves to half-open |
| `CircuitBreakerScope` | `CircuitBreakerScope` | Session | Breaker scope: Session counts failures per session (fine for long-lived sessions); Process accumulates across sessions keyed by (Provider, connection string, threshold, cooldown) and is mandatory for short-lived per-request sessions, otherwise the breaker can never trip. The `Production` preset already defaults to `Process` |
| `NamingConvention` | `enum` | None | None / SnakeCase / LowerCase. Only affects identifier normalization for custom SQL |
| `Interceptors` | `IReadOnlyList<IQueryInterceptor>?` | null | Executed in ascending `Priority` order (`AuditInterceptor` defaults to 200) |
| `ValidateQueryColumnOrder` | `bool` | true | Compares the first result row's column order against entity declaration order on `QueryAsync`; throws on mismatch — disable when using column aliases/expression columns |
| `QueryCache` | `IQueryCache?` | 1024 entries | Process-wide shared by default; inject an independent instance for session/tenant-level isolation (must be thread-safe) |
| `SessionSetupSql` | `string?` | null | SQL executed after the primary connection first activates (`SET TIME ZONE` / `search_path` etc., semicolon-separated) |
| `ReadSessionSetupSql` | `string?` | null | Same setting for read-replica connections; read connections are reused per session, executed once per session |
| `LoggerFactory` | `ILoggerFactory?` | null | When set, `DataSession` creates `ILogger`s |

> **SQLite concurrent-write scalability** (measured 2026-09-21): under an 80/20 read/write mix, SQLite throughput drops 69% from 1 to 8 threads (WAL single-writer model; raw ADO.NET control shows the same −69%, not a PalORM-introduced lock); PostgreSQL +7.2× and MySQL +4.6× over the same range. Choose PG/MySQL for high-concurrency writes; SQLite fits read-heavy or low-concurrency-write embedded scenarios. Details: [architecture doc](docs/架构设计.md) (Chinese).

### Connection-string auto-tuning

Applied at `CreateConnection`; defaults are overridden only when the user has not set a value explicitly.

**PostgreSQL** (6 settings):

| Parameter | Default → tuned | Benefit |
|------|:---:|------|
| `MaxAutoPrepare` | 0 → 100 | Automatic prepared statements, −30~50% query latency |
| `AutoPrepareMinUsages` | 5 → 2 | Prepare from the 2nd execution |
| `NoResetOnClose` | false → true | Skip DISCARD ALL on return, +30% localhost throughput |
| `ReadBufferSize` | 8192 → 16384 | Large result-set throughput |
| `WriteBufferSize` | 8192 → 16384 | Large-value write throughput |
| `Enlist` | true → false | Skip TransactionScope checks |

**MySQL** (5 settings):

| Parameter | Default → tuned | Benefit |
|------|:---:|------|
| `AutoEnlist` | true → false | Skip TransactionScope |
| `ConnectionReset` | true → false | Skip COM_RESET_CONNECTION |
| `CancellationTimeout` | 2 → 5 | Prevent connection leaks |
| `AllowLoadLocalInfile` | false → true | Prerequisite for MySqlBulkCopy |
| `ServerRedirectionMode` | Disabled → Preferred | Direct connection on Azure MySQL |

**SQLite PRAGMA** (9 settings):

| PRAGMA | Default → tuned | Benefit |
|------|:---:|------|
| `busy_timeout` | 0 → 5000ms | Concurrent-write BUSY waits inside the engine |
| `synchronous` | FULL → NORMAL | Safe under WAL, fewer fsyncs |
| `cache_size` | 2MB → 64MB | Read-heavy improvement |
| `temp_store` | DEFAULT → MEMORY | Temp tables in memory |
| `wal_autocheckpoint` | 1000 → 1000 | Explicitly pinned to prevent drift |
| `journal_size_limit` | -1 → 64MB | Prevent unbounded WAL growth slowing checkpoints |
| `mmap_size` | 0 → 256MB | File-database I/O speedup (skipped for `:memory:`) |
| `analysis_limit` | 1000 → 400 | Caps optimize/ANALYZE sampling cost |
| `optimize` | 0 → 0x10002 | Automatic statistics optimization on connection open (official recommendation for long-lived connections; sampling cost bounded by `analysis_limit`); file databases only — memory DBs have no persistent statistics face and read-only connections would hit `SQLITE_READONLY` |

The session-state leakage trade-off of `NoResetOnClose=true` (raw-SQL SET/temp tables visible across pool tenants) is documented as ITM-652: when isolation is needed, use a dedicated connection (`Max Pool Size=1` in the connection string) or an explicit `DISCARD`.

### Advanced recipes

**SQLite** (executed via `DbOptions.SessionSetupSql`; can override the defaults above):

| Scenario | Recipe | Notes |
|------|------|------|
| Bulk/large-row workload database creation | `PRAGMA page_size=16384` | Must take effect before the database is first created; silent no-op on existing databases |
| Read-heavy | `PRAGMA mmap_size=1073741824` | Raise to 1GB (engine cap 2GB−64KB) |
| Delete-heavy | `PRAGMA secure_delete=OFF` | Removes write amplification from zeroing freed pages; deleted content is no longer zeroed — forensic residue on encrypted databases |
| Local resilience passthrough | `MaxRetries = 0` | Local BUSY is already absorbed by `busy_timeout`; the resilience machinery is pure overhead on a local database at RTT≈0. Keep the default for remote databases |

**PostgreSQL** (enabled explicitly at the connection-string level; each has trigger conditions and trade-offs):

| Scenario | Recipe | Notes |
|------|------|------|
| Local/same-container PG | `Host=/var/run/postgresql` | Unix domain socket instead of TCP (Npgsql official performance docs); a `Host` starting with a slash or drive letter is parsed as a socket directory |
| GSS negotiation tail latency | `GssEncryptionMode=Disable` | Measured (2026-09-26, Npgsql 10.0.3 × PG 18.4): median connection time unchanged, tail 149ms → ≤21ms; a security policy change — only use when the server does not require GSS encryption |
| Bulk writes to non-critical tables | `SET LOCAL synchronous_commit TO off` as the first statement in the transaction | PG official docs §28.4: the biggest application-level lever for short-transaction throughput; a crash loses the most recent ~600ms of commits (no data corruption). Acceptable for event logs/caches, not for ledgers; explicit opt-in — PalORM never enables it by default |

**MySQL** (server/DBA decisions or explicit operations):

| Scenario | Recipe | Notes |
|------|------|------|
| Planner statistics refresh | `ANALYZE TABLE name` (via `ExecuteAsync`) | InnoDB auto-recomputes at 10% row-count change by default — manual runs rarely needed; MigrateAsync does not run it |
| Bulk-write commit tail latency | `innodb_flush_log_at_trx_commit=2` (server global) | Commits write only to the OS cache without fsync; a crash loses the most recent ~1 second of commits; same criteria as PG `synchronous_commit=off` |
| Lock waits | No configuration needed | `innodb_lock_wait_timeout` (default 50s) + PalORM resilient retry (deadlock 1213 / lock wait 1205) as two layers of protection |
| Bulk-write packet limit | Server `max_allowed_packet` ≥ 16MB | LOAD DATA and large parameter batches can reach several MB per packet; exceeding it raises `ER_NET_PACKET_TOO_LARGE` or drops the connection silently — check the server value first when this error appears |
| Bulk-write LOAD DATA switch | Server `local_infile=ON` | Missing either falls back to multi-value INSERT (detection cached per connection for 60s); locally measured LOAD DATA is 1.39× faster than multi-value INSERT, with a larger gap on remote databases (official benchmarks in the 4~5× range) |
| Multi-row inserts inside a transaction | Prefer `BulkInsertAsync` | Same transaction, 100 rows: row-by-row 45.7ms → BulkInsert 5.4ms (8.44×) → `SessionBatch` 16.8ms; use SessionBatch only when you need per-row identity backfill or mixed statements |
| Sort/join memory buffers | `sort_buffer_size` / `join_buffer_size` ≥ 256KB (the default) | For remote large result sets / million-row sorted joins, first confirm no `Using filesort` via `EXPLAIN`, then adjust as needed (DBA decision) |
| Isolation level RR→RC | `WithIsolationLevel(IsolationLevel.ReadCommitted)` | The engine default RR (gap locks) amplifies hot-row lock contention; the trade-off is a change in MVCC snapshot semantics — a business decision |

## 🔄 Upgrade guide

> Ordered newest first; for data-correctness fixes read the matching section first.

### Upgrading from 6.3.x to 6.4.0: Production preset breaker scope change

Starting with 6.4.0, the `DbOptions.Production` preset explicitly sets `CircuitBreakerScope = Process` (previously Session):

- **What changes**: with the `Production` preset, breaker failure counts accumulate across sessions keyed by (Provider, connection string, threshold, cooldown). Under per-request sessions, a persistently failing database now trips the breaker and requests fail fast, instead of the previous shape where every request retried 5 times and failed on its own while the breaker never tripped.
- **Unchanged**: bare `new DbOptions { ... }` still defaults to `Session`; long-lived sessions and test scenarios see no change.
- **Multi-tenant note**: tenants sharing one connection string share the breaker; deployments needing tenant-level isolation should use separate connection strings, or fall back explicitly with `with { CircuitBreakerScope = CircuitBreakerScope.Session }`.

### Upgrading from 6.2.x and earlier to 6.3.x (data-correctness fix — strongly recommended)

6.3.0 fixes **R-UNNESTB: silently wrong row counts from the PG auto-prepare × command-reuse-slot interaction** (affects 5.7.0 ~ 6.2.0, seven versions); 6.3.1 additionally restores the pooled BulkDelete IN-form performance on top (20K keys at the 37 ms fast band, 0.55× the floor, see [Performance](#-performance)). R-UNNESTB trigger: `MaxAutoPrepare` enabled in the connection string (PalORM's default tuning sets it to 100) plus a third and subsequent equal-length bulk batch in the same session; the consequence is that from the third batch on, the second batch's parameters are silently re-sent (bulk deletes miss rows, bulk UPSERTs write wrong keys) with no exception and no warning.

- **Upgrading to 6.3.1** fixes it completely (parameter objects stay stable from creation to release, so the auto-prepare cache never holds a stale reference)
- **Hotfix if you cannot upgrade yet**: add `MaxAutoPrepare=0` to the connection string (costs the auto-prepare latency win; 0 is the Npgsql default)
- Exposure check: if historical workloads matched the trigger conditions, reconcile the affected tables' bulk-operation results (row counts / key sets)

6.3.0 also moves three PG bulk-write paths to array form (`UPDATE FROM VALUES(...)` UNNEST and `DELETE ... = ANY(array)`, −40~74% latency on the bulk family) with no behavioral break.

### Upgrading from 5.x to 6.0

<details>
<summary><b>Four breaking changes (full table in ADR-N)</b></summary>

| Removal / change | Migration action |
|-----------|---------|
| `IRowFactory<T>` interface | None — zero implementations and zero consumers; if external code referenced it (no official usage exists), delete the reference |
| `DataSession.DiffAsync<T>()` | Switch to `ValidateSchemaAsync<T>()`; prepend `[DIFF]` yourself if needed |
| `DbOptions.NamingConvention` (incl. `ApplyNaming`) | **Just delete the setting — it never took effect** (column mapping is compile-time via `[Table]`/`[Column]`); use `[Column("...")]` to rename |
| `DbOptions.PoolExplicitlyConfigured` | None — an internal marker with zero readers; `WithPool`/`PALORM_MAX_POOL_SIZE` behavior unchanged |
| `[Column]` `Length/Precision/Scale`: `int?` → `int` (0 = unset) | None — `[Column(Length = 64)]` never compiled in 5.x (CS0655); the syntax becomes usable and DDL-relevant in v6.0 |

Full breaking-change table: [ADR-N: v6.0 breaking changes](docs/adr/ADR-N-v6.0-破坏性变更汇总.md) (Chinese).

</details>

Full version history: [CHANGELOG.md](CHANGELOG.md) (Chinese).

## 🧰 Development

> Build environment, project layout, and the registration contract; full build paths and commit defenses live in [CONTRIBUTING.md](CONTRIBUTING.md).

### Requirements

| Dimension | JIT runtime | Native AOT publish |
|------|:---:|:---:|
| CPU architecture | x64 / ARM64 | x64 verified (ARM64 untested), RID required (`-r win-x64` / `linux-x64`) |
| Memory | 256 MB | 4 GB (ILC compiler; runtime needs only ~100 MB) |
| Disk | 50 MB | 100 MB, SSD recommended (ILC writes many temp files; HDD compiles 5-10x slower) |

Software: .NET SDK 11.0.100-preview.6+ (pinned by `global.json`) · C# `latest` · Windows 10+ / Linux / macOS · Visual Studio 2026 / Rider / VS Code (Roslyn source generator support required).

### Project structure

```
PalORM.Core           Runtime core (DataSession / QueryBuilder / Resilience / IQueryInterceptor)
PalORM.PostgreSql     Npgsql adapter + JSONB / NOTIFY / Binary COPY / AdvisoryXactLock
PalORM.MySql          MySqlConnector adapter + MySqlBulkCopy
PalORM.Sqlite         MDS + SQLite3MC adapter + PRAGMA tuning
PalORM.SourceGen      Roslyn IIncrementalGenerator (netstandard2.0 compiler plugin)
PalORM.Testing        Test helpers (TestEnvironment / TestDb)
```

**Cross-assembly registration contract**: each model assembly's generated output registers with `PalORM_Runtime` via `ModuleInitializer` when that module is first touched (any member called, type instantiated, or static field accessed). Referencing a library assembly without ever touching any of its types means its entities are not registered — consumers across assemblies must ensure entity types are actually referenced.

### Build, test, and benchmark

SQLite tests run out of the box (no external database needed); PG/MySQL integration tests require external databases. Build paths, local commit gates, and the benchmark environment (BenchmarkDotNet local fork) are described in [CONTRIBUTING.md](CONTRIBUTING.md); architecture decisions in [docs/架构设计.md](docs/架构设计.md) and [docs/adr/](docs/adr/) (Chinese).

## 🤝 Contributing

Issues and PRs are welcome. Before submitting, run the relevant test suites and keep zero warnings (`TreatWarningsAsErrors`); performance-related changes should include before/after benchmarks. See [CONTRIBUTING.md](CONTRIBUTING.md) for the detailed workflow.

## 📄 License

[AGPL-3.0-only](LICENSE)

<div align="right">

[⬆ Back to top](#palorm)

</div>
