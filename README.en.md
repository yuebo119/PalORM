<div align="center">

# PalORM

**A .NET 11 micro-ORM for Native AOT: everything generated at compile time, zero reflection at runtime**

*An AOT-native, compile-time-verifiable micro-ORM with SQL you control*

[![.NET](https://img.shields.io/badge/.NET-11.0.0--rc.2-512BD4)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/PalORM.Core)](https://www.nuget.org/packages/PalORM.Core)
[![CI](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml/badge.svg)](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

[![Native AOT](https://img.shields.io/badge/Native_AOT-3_dialects_verified-512BD4)](docs/AOT部署指南.md)
[![Diagnostics](https://img.shields.io/badge/Compile--time_diagnostics-45_rules-512BD4)](docs/API参考.md)
[![Dialects](https://img.shields.io/badge/Dialects-PostgreSQL_·_MySQL_·_SQLite-512BD4)](#-database-compatibility)

[简体中文](README.md) | English

</div>

A Roslyn source generator produces SQL construction, parameter binding, object mapping, and migration DDL at compile time — zero reflection, zero IL Emit at runtime. PostgreSQL / MySQL / SQLite across three dialects, with enterprise features out of the box: multi-tenancy, optimistic locking, soft delete, auditing, advisory locks, and more.

> Detailed docs (in Chinese) live under [docs/](docs/) — [API reference](docs/API参考.md), [architecture](docs/架构设计.md), [AOT deployment guide](docs/AOT部署指南.md), [benchmark methodology](docs/性能基准规范.md).

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

- **Compile-time generation**: A Roslyn source generator emits RowFactory, CommandFactory, and three-dialect Migration DDL for every `[Table]` entity, plus materializers for `[Projection]` DTOs, with zero runtime reflection and zero IL Emit.
- **Compile-time safety net**: 45 Roslyn diagnostics catch missing keys, nullable tenant columns bypassing isolation, zero-value optimistic-lock baselines, and loop N+1 queries at build time, with errors jumping to the declaration site.
- **Full-pipeline Native AOT**: Publish verification and native execution pass on SQLite, PostgreSQL, and MySQL, and consumers need no TrimmerRootAssembly.
- **Floor-level performance**: Single-row CRUD matches hand-written ADO.NET, bulk writes automatically take the fastest per-dialect path (PG Binary COPY, MySQL LOAD DATA, SQLite row-prepared batches and json_each bulk delete), and a session pool is built in for request-per-session web services.
- **Enterprise cross-cutting**: Multi-tenant column isolation, optimistic locking, soft delete, audit interception, read/write splitting, advisory locks, resilient retry, and circuit breaking work out of the box.
- **SQL assets managed at compile time**: `[SqlFile]` embeds and validates .sql files, `[SqlTemplate]` turns interpolated SQL into constants, and AutoTagging injects "file:line member" locating comments automatically.
- **Operations and observability**: Health checks, a cross-dialect unified exception family, and OpenTelemetry tracing and metrics are built in.
- **Dialect-specific capabilities**: PostgreSQL gets UNNEST, advisory locks, LISTEN/NOTIFY, pgvector, and JSONB path queries; MySQL gets local_infile degradation and UPDATE JOIN; SQLite gets AES-256 encryption and automatic PRAGMA tuning.
- **Schema and seeding**: `MigrateAsync` runs three-dialect DDL in foreign-key topological order, `SeedAsync` seeds idempotently inside a transaction, and `ValidateSchemaAsync` reports missing columns against entity declarations.
- **Zero-dependency core**: PalORM.Core references no third-party NuGet packages, only BCL and ADO.NET abstractions.

## 🆚 Comparison with mainstream ORMs

> Version baseline (verified against NuGet latest on 2026-10-26): PalORM 6.7.0 / Dapper 2.1.89 / RepoDb 1.17.0 (versions actually referenced by this repo's benchmark suite); EF Core 10.0.13 (latest 10.x LTS patch, comparison reference).

| Feature | **PalORM** | Dapper | EF Core | RepoDb |
|------|:---:|:---:|:---:|:---:|
| **Full-pipeline Native AOT** | ✓ source-generated, verified | △ Dapper.Aot optional (experimental interceptors) | ❌ experimental, not production-ready | △ NativeAOT/trimming annotations across all packages + SQLite AOT smoke test (reflection-based, no source generation) |
| **Compile-time type diagnostics** | ✓ 45 rules (42 analyzers + 3 generators), errors jump to declaration sites | ❌ fails at runtime | △ migration checks (design time) | ❌ fails at runtime |
| **Compile-time SQL pre-building** | ✓ Roslyn source generation | ❌ runtime concatenation | △ precompiled queries (experimental) | ❌ runtime expression trees |
| **Runtime reflection** | Zero | △ first-use reflection + IL Emit cache | △ expression-tree compilation | ❌ reflection + compiled expressions (IL Emit removed) |
| **Query authoring model** | ✓ chained builder (SQL visible) + `FormattableString` raw SQL | △ hand-written SQL | ✓ strongly-typed LINQ translation | △ QueryBuilder |
| **Model build cost** | ✓ compile-time generation, zero runtime build | ✓ no model (first-use reflection + cache) | △ runtime build (Compiled Model needs manual regeneration) | ✓ cached on first use |
| **Per-dialect bulk strategies** | ✓ COPY / BulkCopy / row-prepared & multi-value / `= ANY` arrays | ❌ hand-written multi-value SQL | △ varies by provider | △ Bulk extension packages cover 13+ databases (SQL Server/MySQL/Oracle etc., some preview) |
| **Single-statement multi-row UPDATE** | ✓ FROM VALUES / UPDATE JOIN VALUES ROW / CASE WHEN | ❌ | ❌ ExecuteUpdate is single-value per WHERE only | ❌ |
| **IN collection parameter translation** | ✓ automatic 500-key batching + PG `= ANY` array form | ❌ hand-written | ✓ EF10 makes multi-parameter the default, translation mode configurable (JSON/constant) | △ DeleteAll batches internally by `MaxParameterCount`; query-side IN hand-written |
| **Bulk UPSERT** | ✓ `BulkMergeAsync` (ON CONFLICT / ON DUPLICATE KEY, conflict updates tenant-scoped) | ❌ hand-written | ❌ raw SQL required | △ Merge via extension packages across multiple providers (SQL Server/MySQL/Oracle etc.) |
| **Optimistic locking** | ✓ `[ConcurrencyCheck]` automatic; bulk updates via versioned single statements (PG/MySQL; falls back to packed DbBatch) | ❌ hand-written | ✓ `RowVersion` automatic | ❌ hand-written |
| **Soft delete** | ✓ `[SoftDelete]` automatic filtering (bulk delete becomes UPDATE) | ❌ | ✓ global query filters (EF10 named filters can be disabled by name) | ❌ |
| **Multi-tenant column isolation** | ✓ `[TenantAware]` compile time; cross-tenant guards on bulk writes | ❌ | △ manual implementation | ❌ |
| **Value conversion + enum storage** | ✓ `[Converter]` / three enum strategies (int32/int64/text) emitted at compile time | △ hand-written TypeHandler | ✓ ValueConverter / HasConversion (runtime) | △ TypeHandler |
| **Read-replica routing** | ✓ `ParallelReadScope` + `readFromReplica` on the query family | ❌ | △ manual multi-context setup | ❌ |
| **Automatic schema migration** | ✓ `MigrateAsync` + concurrent-race tolerance (duplicate objects skipped) | ❌ (no migration system) | ✓ Migrations (most complete) | ❌ |
| **OwnedJson compile-time safety** | ✓ `[OwnedJson]` + source generation | △ PropertyHandler mapping (runtime reflection annotated RUC/DC; AOT requires a source-generated `JsonTypeInfo`) | △ Owned Types (runtime) | ❌ |
| **Audit interceptor** | ✓ | ❌ | ✓ Interceptors | ❌ |
| **Advisory locks** | ✓ `pg_advisory_xact_lock` | ❌ | ❌ | ❌ |
| **Session-level SET** | ✓ `SessionSetupSql` | ❌ | ❌ | ❌ |
| **SQL file embedding** | ✓ `[SqlFile]` compile-time validation | ❌ | ❌ | ❌ |
| **Circuit breaker + retry** | ✓ built in (concurrency-safe HalfOpen probe slots) | ❌ needs Polly | △ execution strategies | ❌ |
| **Query cache** | ✓ `WithCache` bounded cache + TTL + `EvictQueryCache` narrowing | ❌ | ❌ third-party interceptor | ✓ native `cacheKey` argument |
| **Streaming consumption** | ✓ `QueryAsyncEnumerable` / `ForEachAsync` | △ hand-written CommandBehavior | ✓ native `AsAsyncEnumerable` | ❌ |
| **Observability (OTel)** | ✓ built-in `WithMetrics` / `WithTracing` | ❌ | △ third-party instrumentation package | △ own telemetry extension packages (Telemetry.Core/Default, not OTel) |
| **SQL preview** | ✓ `AsDryRun` (SQL + parameters) | ❌ | ✓ `ToQueryString` | △ builder-level `GetString` |
| **Pessimistic locking** | ✓ chained `ForUpdate` / `ForShare` | ❌ hand-written SQL | △ not in the core (PG provider extension) | ❌ |
| **Navigation object graphs** | △ `Include` generates JOINs without assembly (micro-ORM trade-off; group by parent yourself) | △ hand-written multi-mapping | ✓ full `Include` assembly + change tracking | ❌ |
| **Change tracking / unit of work** | ❌ stateless design (zero tracking overhead) | ❌ | ✓ ChangeTracker (identity resolution + state machine) | ❌ |
| **JSON column queries** | △ `WhereJson` JSONB path queries (PG only); mapping see `[OwnedJson]` above | △ hand-written SQL | ✓ EF7+ JSON column mapping + path queries (EF10: SQL Server 2025 json type, ExecuteUpdate support) | △ PropertyHandler mapping, no path queries |
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
| **BulkInsert memory** | SQLite ≈ 40% of Dapper (PG / MySQL 5%~7%) | baseline | highest (ChangeTracker) | medium |
| **Database breadth** | three dialects (deeply tuned) | any ADO.NET (dialect handling manual) | official provider matrix (SQL Server / SQLite / PG / MySQL / Cosmos…) | 15+ providers (majorly expanded in 1.16/1.17) |
| **Core NuGet dependencies** | zero | zero | high (multi-package) | medium |
| **Target frameworks** | net11.0 (single target) | multi-target (netstandard2.0+) | multi-target (net8+) | multi-target (netstandard2.0+) |
| **License** | Apache-2.0 | Apache-2.0 | MIT | Apache-2.0 |

Core differentiators: compile-time generation + full-pipeline AOT compatibility + per-dialect bulk strategies (COPY / BulkCopy / row-prepared / arrays). Dapper is fast but reflects at runtime; EF Core is feature-complete but heavy at runtime with AOT still experimental; RepoDb is also a micro-ORM but has no source generation, with bulk support extended via companion packages to SQL Server/MySQL/Oracle and 13+ databases.


## 📊 Performance

> **Test environment**: AMD Ryzen 9 8945HX (32 logical cores) · Windows 10 22H2 · .NET 11 RC2 · SQLite temp-file DB (WAL + 64 MB cache + mmap) · PG 18.6 / MySQL 8.4.11 local containers. **Five arms, same process, same round, same data, same instrumentation**: ADO.NET (hand-written floor) / Dapper 2.1.89 / FreeSql 3.5.311 / RepoDb 1.17.0 / PalORM — each arm uses its ecosystem's idiomatic best practice; every operation runs twice with reversed arm order and takes the median (removes arm-order phase bias).
>
> **Data batch**: 2026-10-26, code `dbff816`. CrossBench five-arm full batch `crossbench-20261026-055849.json` (3 dialects × 2 tiers × 17 operations × 5 arms, two reversed passes; 5 arms × 29 measurements × 2 passes × 3 dialects = 870 rows). All numbers are derived from the batch JSON by the frozen template script (`scripts/crossbench-report.cs`); full report at `bench/crossbench/results/fivearm-20261026.md`; PerfHub/PerfGate four-table caliber (incl. RepoDb columns) under `bench/reports/`.

**Reading the tables**: each cell = time [ratio vs ADO] / memory [ratio vs ADO]; [1.00] = ±0.5% parity band; ≤0.67 or ≥1.5 bold = outside the line; 📌 = flagged for review; 🏆 = fastest arm in the cell. Color scale (positive % = slower/more than baseline): 🟢 strong win ≤−30% · 🟩 mid win −29%~−10% · 🔹 slight win −9%~−1% · 🔸 slight loss +1%~+9% · 🟧 mid loss +10%~+29% · 🟥 strong loss ≥+30%.

### Overview (5 arms × 3 dialects × 14 operations = 69 cells)

**CRUD single-row and reads (baseline = ADO [1.00])**

- 🥇 GetByKey SQLite: PalORM **0.80** (20% faster), FreeSql **4.98** (398% slower, worst across dialects at 5.45)
- QueryAll MySQL 20000 allocation: PalORM **0.91** (9% faster), Dapper 1.11 (11% slower)
- Insert SQLite: PalORM 0.98 (2% faster), FreeSql **1.72** (72% slower)
- RepoDb single-row and reads: worst cell **1.46** (46% slower) — expression-based materialization, same order of magnitude as hand-written SQL

**Bulk processing (baseline = ADO [1.00])**

- 🥇 BulkDelete PG 20000: PalORM **0.40** (60% faster), allocation **0.10** (90% faster)
- 🥇 UpsertBatch PG 20000: PalORM **0.67** (33% faster), allocation **0.37** (63% faster)
- BulkInsert SQLite 20000: PalORM 1.03 (3% slower), Dapper 1.88 (88% slower), FreeSql **3.81** (281% slower)
- BulkUpdate three dialects both tiers: PalORM 0.92~1.04, Dapper worst **3.70** (270% slower)
- RepoDb bulk family: best BulkInsert MySQL **0.96** (4% faster), worst BulkDelete SQLite **24.05** (2305% slower)

**Transactions (baseline = ADO [1.00])**

- PalORM 0.95~1.37 across all items, fastest EnumInserts PostgreSQL **0.95**
- FreeSql worst TxRollback SQLite **12.79**, Dapper worst TxRollback SQLite **3.56**
- EnumInserts SQLite allocation: PalORM 1.00 (even), FreeSql **8.42** (742% slower)

**Fastest wins**

| Arm | Fastest / 69 cells |
|---|---:|
| ADO.NET (floor) | **32** (46%) |
| PalORM | **32** (46%) |
| Dapper | **3** (4%) |
| RepoDb | **2** (3%) |
| FreeSql | **0** (0%) |

> P/ADO median **1.00** (even with the hand-written SQL floor), range 0.40~1.37.

### Table 1: CRUD single-row and reads (three dialects, two tiers)

**SQLite**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| GetByKey | 2000 | 🏆 **PalORM** | 7.2 µs [1.00] / 2.1KB [1.00] | **5.8 µs [0.80] / 2.1KB [1.02]** | 10.2 µs [1.41] / 2.8KB [1.34] | 36.1 µs [**4.98**] / 10.2KB [**4.86**] | 10.6 µs [1.46] / 4.8KB [**2.30**] |
| GetAllAsync | 2000 | 🏆 **PalORM** | 953.3 µs [1.00] / 308KB [1.00] | **933.4 µs [0.98] / 294KB [0.95]** | 1.38 ms [1.45] / 513KB [**1.66**] | 1.38 ms [1.44] / 314KB [1.02] | 1.15 ms [1.20] / 388KB [1.26] |
| QueryAll | 2000 | ADO | **942.1 µs [1.00] / 308KB [1.00]** | 943.9 µs [1.00] / 292KB [0.95] | 1.38 ms [1.47] / 513KB [**1.66**] | 1.38 ms [1.46] / 314KB [1.02] | 1.15 ms [1.22] / 388KB [1.26] |
| Insert | 2000 | 🏆 **PalORM** | 17.4 µs [1.00] / 2.8KB [1.00] | **17.0 µs [0.98] / 2.7KB [0.97]** | 17.9 µs [1.03] / 3.5KB [1.28] | 29.9 µs [**1.72**] / 5.7KB [**2.06**] | 19.1 µs [1.10] / 3.0KB [1.08] |
| Update | 2000 | 🏆 **PalORM** | 7.1 µs [1.00] / 2.5KB [1.00] | **6.9 µs [0.97] / 2.9KB [1.15]** | 7.8 µs [1.09] / 3.3KB [1.30] | 19.6 µs [**2.75**] / 7.4KB [**2.95**] | 8.8 µs [1.25] / 4.0KB [**1.60**] |
| GetByKey | 20000 | ADO | **6.0 µs [1.00] / 2.1KB [1.00]** | 6.0 µs [1.02] / 2.1KB [1.02] | 7.1 µs [1.19] / 2.8KB [1.33] | 32.5 µs [**5.45**] / 10.4KB [**4.94**] | 8.6 µs [1.44] / 4.8KB [**2.28**] |
| GetAllAsync | 20000 | ADO | **9.51 ms [1.00] / 3.3MB [1.00]** | 9.66 ms [1.01] / 2.9MB [0.89] | 14.40 ms [**1.51**] / 5.3MB [**1.61**] | 13.93 ms [1.46] / 3.3MB [1.01] | 11.67 ms [1.23] / 4.0MB [1.24] |
| QueryAll | 20000 | ADO | **9.49 ms [1.00] / 3.3MB [1.00]** | 9.57 ms [1.01] / 2.9MB [0.89] | 14.48 ms [**1.53**] / 5.3MB [**1.61**] | 13.78 ms [1.45] / 3.3MB [1.00] | 11.71 ms [1.23] / 4.0MB [1.24] |
| Insert | 20000 | 🏆 **PalORM** | 17.1 µs [1.00] / 2.8KB [1.00] | **17.1 µs [1.00] / 2.8KB [1.00]** | 18.0 µs [1.05] / 3.6KB [1.30] | 30.1 µs [**1.76**] / 5.7KB [**2.07**] | 18.8 µs [1.10] / 3.0KB [1.08] |
| Update | 20000 | ADO | **7.1 µs [1.00] / 2.5KB [1.00]** | 7.1 µs [1.00] / 2.9KB [1.15] | 7.7 µs [1.08] / 3.3KB [1.30] | 22.2 µs [**3.13**] / 7.4KB [**2.95**] | 8.8 µs [1.23] / 4.0KB [**1.60**] |

**MySQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| GetByKey | 2000 | ADO | **197.9 µs [1.00] / 4.5KB [1.00]** | 199.4 µs [1.01] / 4.9KB [1.09] | 202.4 µs [1.02] / 5.8KB [1.29] | 424.6 µs [**2.14**] / 16.6KB [**3.67**] | 201.3 µs [1.02] / 9.0KB [**2.00**] |
| GetAllAsync | 2000 | ADO | **1.08 ms [1.00] / 398KB [1.00]** | 1.10 ms [1.02] / 384KB [0.96] | 1.15 ms [1.06] / 447KB [1.12] | 1.39 ms [1.28] / 407KB [1.02] | 1.14 ms [1.06] / 416KB [1.05] |
| QueryAll | 2000 | ADO | **1.09 ms [1.00] / 398KB [1.00]** | 1.10 ms [1.01] / 383KB [0.96] | 1.15 ms [1.05] / 447KB [1.12] | 1.39 ms [1.28] / 407KB [1.02] | 1.13 ms [1.04] / 416KB [1.05] |
| Insert | 2000 | Dapper | 4.03 ms [1.00] / 16.9KB [1.00] | 3.88 ms [0.96] / 17.3KB [1.02] | **3.77 ms [0.94] / 16.4KB [0.97]** | 4.24 ms [1.05] / 23.0KB [1.36] | 3.83 ms [0.95] / 17.0KB [1.01] |
| Update | 2000 | 🏆 **PalORM** | 200.1 µs [1.00] / 5.4KB [1.00] | **198.7 µs [0.99] / 5.9KB [1.10]** | 202.7 µs [1.01] / 5.2KB [0.97] | 404.8 µs [**2.02**] / 13.2KB [**2.47**] | 217.7 µs [1.09] / 7.6KB [1.42] |
| GetByKey | 20000 | 🏆 **PalORM** | 198.9 µs [1.00] / 4.5KB [1.00] | **198.6 µs [1.00] / 4.9KB [1.09]** | 205.5 µs [1.03] / 5.8KB [1.29] | 433.1 µs [**2.18**] / 16.8KB [**3.72**] | 215.0 µs [1.08] / 9.1KB [**2.01**] |
| GetAllAsync | 20000 | ADO | **9.38 ms [1.00] / 4.0MB [1.00]** | 9.54 ms [1.02] / 3.7MB [0.91] | 10.01 ms [1.07] / 4.5MB [1.11] | 10.87 ms [1.16] / 4.1MB [1.00] | 9.92 ms [1.06] / 4.2MB [1.04] |
| QueryAll | 20000 | ADO | **9.44 ms [1.00] / 4.0MB [1.00]** | 9.58 ms [1.01] / 3.7MB [0.91] | 10.09 ms [1.07] / 4.5MB [1.11] | 10.87 ms [1.15] / 4.1MB [1.00] | 9.93 ms [1.05] / 4.2MB [1.04] |
| Insert | 20000 | Dapper | 4.02 ms [1.00] / 16.8KB [1.00] | 3.85 ms [0.96] / 16.3KB [0.96] | **3.79 ms [0.94] / 16.3KB [0.97]** | 4.19 ms [1.04] / 22.4KB [1.33] | 3.86 ms [0.96] / 17.0KB [1.01] |
| Update | 20000 | 🏆 **PalORM** | 192.8 µs [1.00] / 5.2KB [1.00] | **185.1 µs [0.96] / 5.7KB [1.09]** | 201.9 µs [1.05] / 5.2KB [1.00] | 386.9 µs [**2.01**] / 13.1KB [**2.53**] | 206.2 µs [1.07] / 7.5KB [1.45] |

**PostgreSQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| GetByKey | 2000 | ADO | **201.1 µs [1.00] / 3.0KB [1.00]** | 202.3 µs [1.01] / 3.4KB [1.13] | 202.2 µs [1.01] / 3.3KB [1.10] | 270.8 µs [1.35] / 12.0KB [**4.06**] | 213.5 µs [1.06] / 6.3KB [**2.13**] |
| GetAllAsync | 2000 | ADO | **657.6 µs [1.00] / 239KB [1.00]** | 727.4 µs [1.11] / 226KB [0.94] | 668.2 µs [1.02] / 442KB [**1.85**] | 751.1 µs [1.14] / 244KB [1.02] | 676.2 µs [1.03] / 256KB [1.07] |
| QueryAll | 2000 | Dapper | 691.4 µs [1.00] / 240KB [1.00] | 670.5 µs [0.97] / 225KB [0.94] | **664.4 µs [0.96] / 442KB [1.84]** | 763.6 µs [1.10] / 244KB [1.02] | 670.1 µs [0.97] / 256KB [1.07] |
| Insert | 2000 | ADO | **1.39 ms [1.00] / 7.5KB [1.00]** | 1.41 ms [1.01] / 7.7KB [1.02] | 1.42 ms [1.02] / 7.3KB [0.97] | 1.44 ms [1.04] / 11.0KB [1.46] | 1.43 ms [1.03] / 7.9KB [1.05] |
| Update | 2000 | ADO | **1.38 ms [1.00] / 7.4KB [1.00]** | 1.38 ms [1.00] / 7.8KB [1.06] | 1.38 ms [1.00] / 7.0KB [0.95] | 1.44 ms [1.05] / 13.1KB [**1.78**] | 1.42 ms [1.03] / 9.4KB [1.28] |
| GetByKey | 20000 | ADO | **203.8 µs [1.00] / 3.0KB [1.00]** | 204.0 µs [1.00] / 3.4KB [1.13] | 204.3 µs [1.00] / 3.3KB [1.09] | 272.1 µs [1.34] / 12.1KB [**4.05**] | 223.7 µs [1.10] / 6.4KB [**2.13**] |
| GetAllAsync | 20000 | 🏆 **PalORM** | 5.05 ms [1.00] / 2.5MB [1.00] | **5.02 ms [0.99] / 2.2MB [0.86]** | 5.23 ms [1.03] / 4.5MB [**1.79**] | 5.54 ms [1.10] / 2.5MB [1.00] | 5.10 ms [1.01] / 2.7MB [1.06] |
| QueryAll | 20000 | ADO | **4.93 ms [1.00] / 2.5MB [1.00]** | 5.00 ms [1.01] / 2.2MB [0.86] | 5.05 ms [1.03] / 4.5MB [**1.79**] | 5.82 ms [1.18] / 2.5MB [1.00] | 5.07 ms [1.03] / 2.7MB [1.06] |
| Insert | 20000 | ADO | **1.38 ms [1.00] / 7.9KB [1.00]** | 1.47 ms [1.06] / 7.7KB [0.97] | 1.39 ms [1.00] / 7.2KB [0.92] | 1.44 ms [1.04] / 11.1KB [1.40] | 1.42 ms [1.03] / 8.2KB [1.04] |
| Update | 20000 | ADO | **1.36 ms [1.00] / 7.4KB [1.00]** | 1.42 ms [1.05] / 8.7KB [1.18] | 1.41 ms [1.04] / 7.1KB [0.96] | 1.48 ms [1.09] / 14.2KB [**1.92**] | 1.43 ms [1.06] / 9.6KB [1.29] |

### Table 2: Bulk processing (three dialects, two tiers)

**SQLite**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| BulkInsert | 2000 | ADO | **2.58 ms [1.00] / 1.7MB [1.00]** | 2.60 ms [1.01] / 1.7MB [1.00] | 4.75 ms [**1.84**] / 4.3MB [**2.48**] | 10.10 ms [**3.91**] / 6.5MB [**3.79**] | 16.12 ms [**6.25**] / 2.6MB [**1.54**] |
| BulkUpdate | 2000 | ADO | **2.33 ms [1.00] / 1.7MB [1.00]** | 2.35 ms [1.01] / 1.8MB [1.04] | 8.63 ms [**3.70**] / 6.3MB [**3.75**] | 13.21 ms [**5.67**] / 7.0MB [**4.15**] | 6.72 ms [**2.88**] / 5.2MB [**3.06**] |
| BulkDelete | 2000 | ADO | **803.0 µs [1.00] / 198KB [1.00]** | 828.3 µs [1.03] / 198KB [1.00] | 21.85 ms [**27.22**] / 1.6MB [**8.08**] | 1.60 ms [**1.99**] / 569KB [**2.87**] | 19.31 ms [**24.05**] / 3.2MB [**16.66**] |
| UpsertBatch | 2000 | 🏆 **PalORM** | 2.39 ms [1.00] / 1.7MB [1.00] | **2.36 ms [0.99] / 1.7MB [1.01]** | 4.08 ms [**1.71**] / 4.2MB [**2.50**] | 3.62 ms [**1.52**] / 2.5MB [1.47] | 17.90 ms [**7.50**] / 11.5MB [**6.78**] |
| BulkInsert | 20000 | ADO | **28.64 ms [1.00] / 17.2MB [1.00]** | 29.55 ms [1.03] / 17.2MB [1.00] | 53.84 ms [**1.88**] / 42.6MB [**2.48**] | 109.10 ms [**3.81**] / 66.1MB [**3.85**] | 154.45 ms [**5.39**] / 26.2MB [**1.52**] |
| BulkUpdate | 20000 | ADO | **25.29 ms [1.00] / 17.0MB [1.00]** | 25.71 ms [1.02] / 17.6MB [1.04] | 91.96 ms [**3.64**] / 63.5MB [**3.74**] | 136.10 ms [**5.38**] / 73.4MB [**4.32**] | 71.31 ms [**2.82**] / 51.8MB [**3.05**] |
| BulkDelete | 20000 | 🏆 **PalORM** | 8.83 ms [1.00] / 2.0MB [1.00] | **8.21 ms [0.93] / 2.0MB [1.00]** | 219.25 ms [**24.82**] / 15.6MB [**7.71**] | 16.58 ms [**1.88**] / 6.0MB [**2.94**] | 193.40 ms [**21.89**] / 31.6MB [**15.63**] |
| UpsertBatch | 20000 | 🏆 **PalORM** | 26.20 ms [1.00] / 17.0MB [1.00] | **25.88 ms [0.99] / 17.1MB [1.01]** | 51.78 ms [**1.98**] / 42.4MB [**2.49**] | 42.69 ms [**1.63**] / 25.8MB [**1.52**] | 177.45 ms [**6.77**] / 114.9MB [**6.76**] |

**MySQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| BulkInsert | 2000 | RepoDb | 11.09 ms [1.00] / 477KB [1.00] | 11.42 ms [1.03] / 480KB [1.01] | 24.84 ms [**2.24**] / 6.7MB [**14.28**] | 16.95 ms [**1.53**] / 3.2MB [**6.93**] | **10.61 ms [0.96] / 474KB [0.99]** |
| BulkUpdate | 2000 | 🏆 **PalORM** | 10.11 ms [1.00] / 4.6MB [1.00] | **9.29 ms [0.92] / 3.7MB [0.80]** | 11.03 ms [1.09] / 6.5MB [1.41] | 9.84 ms [0.97] / 2.1MB [**0.45**] | 161.14 ms [**15.94**] / 927KB [**0.20**] |
| BulkDelete | 2000 | ADO | **11.42 ms [1.00] / 880KB [1.00]** | 12.96 ms [1.13] / 814KB [0.92] | 11.91 ms [1.04] / 1.3MB [**1.56**] | 17.13 ms [1.50] / 610KB [0.69] | 26.65 ms [**2.33**] / 19.3MB [**22.44**] |
| UpsertBatch | 2000 | 🏆 **PalORM** | 8.22 ms [1.00] / 2.9MB [1.00] | **7.97 ms [0.97] / 2.5MB [0.85]** | 10.45 ms [1.27] / 6.6MB [**2.28**] | 8.92 ms [1.09] / 2.5MB [0.86] | 164.17 ms [**19.98**] / 923KB [**0.31**] |
| BulkInsert | 20000 | 🏆 **PalORM** | 56.99 ms [1.00] / 4.5MB [1.00] | **56.94 ms [1.00] / 4.4MB [1.00]** | 259.60 ms [**4.55**] / 66.5MB [**14.94**] | 121.32 ms [**2.13**] / 33.3MB [**7.47**] | 58.49 ms [1.03] / 4.4MB [1.00] |
| BulkUpdate | 20000 | 🏆 **PalORM** | 88.55 ms [1.00] / 14.6MB [1.00] | **85.58 ms [0.97] / 13.7MB [0.94]** | 149.20 ms [**1.68**] / 64.6MB [**4.42**] | 94.08 ms [1.06] / 21.8MB [1.49] | 388.75 ms [**4.39**] / 5.5MB [**0.38**] |
| BulkDelete | 20000 | 🏆 **PalORM** | 76.11 ms [1.00] / 8.5MB [1.00] | **70.35 ms [0.92] / 3.5MB [0.41]** | 74.28 ms [0.98] / 13.2MB [**1.57**] | 186.01 ms [**2.44**] / 6.2MB [0.74] | 232.73 ms [**3.06**] / 193.5MB [**22.90**] |
| UpsertBatch | 20000 | ADO | **74.00 ms [1.00] / 12.9MB [1.00]** | 78.12 ms [1.06] / 12.6MB [0.98] | 141.30 ms [**1.91**] / 66.2MB [**5.15**] | 123.44 ms [**1.67**] / 25.8MB [**2.00**] | 404.37 ms [**5.46**] / 5.4MB [**0.42**] |

**PostgreSQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| BulkInsert | 2000 | ADO | **4.77 ms [1.00] / 341KB [1.00]** | 4.81 ms [1.01] / 343KB [1.00] | 16.92 ms [**3.55**] / 6.7MB [**20.09**] | 13.16 ms [**2.76**] / 3.8MB [**11.56**] | 5.40 ms [1.13] / 849KB [**2.49**] |
| BulkUpdate | 2000 | ADO | **5.26 ms [1.00] / 418KB [1.00]** | 5.45 ms [1.04] / 422KB [1.01] | 17.00 ms [**3.23**] / 6.5MB [**16.00**] | 14.12 ms [**2.68**] / 2.7MB [**6.65**] | 52.82 ms [**10.04**] / 3.8MB [**9.29**] |
| BulkDelete | 2000 | 🏆 **PalORM** | 3.61 ms [1.00] / 784KB [1.00] | **2.44 ms [0.68] / 89.5KB [0.11]** | 3.83 ms [1.06] / 1.3MB [**1.71**] | 5.95 ms [**1.65**] / 571KB [0.73] | 18.76 ms [**5.19**] / 19.2MB [**25.02**] |
| 📌UpsertBatch | 2000 | 🏆 **PalORM** | 14.69 ms [1.00] / 2.7MB [1.00] | **7.77 ms [0.53] / 446KB [0.16]** | 20.24 ms [1.38] / 6.7MB [**2.47**] | 16.35 ms [1.11] / 2.7MB [1.01] | 24.39 ms [**1.66**] / 5.7MB [**2.12**] |
| BulkInsert | 20000 | 🏆 **PalORM** | 20.14 ms [1.00] / 3.3MB [1.00] | **19.29 ms [0.96] / 3.3MB [1.00]** | 195.97 ms [**9.73**] / 66.9MB [**20.55**] | 108.88 ms [**5.41**] / 39.6MB [**12.15**] | 21.99 ms [1.09] / 8.0MB [**2.46**] |
| BulkUpdate | 20000 | 🏆 **PalORM** | 67.40 ms [1.00] / 4.1MB [1.00] | **63.43 ms [0.94] / 4.1MB [1.00]** | 237.87 ms [**3.53**] / 65.4MB [**16.02**] | 126.22 ms [**1.87**] / 28.3MB [**6.93**] | 535.80 ms [**7.95**] / 37.9MB [**9.29**] |
| 📌BulkDelete | 20000 | 🏆 **PalORM** | 24.75 ms [1.00] / 7.6MB [1.00] | **9.96 ms [0.40] / 812KB [0.10]** | 27.72 ms [1.12] / 13.0MB [**1.71**] | 62.61 ms [**2.53**] / 6.0MB [0.78] | 178.23 ms [**7.20**] / 192.4MB [**25.29**] |
| 📌UpsertBatch | 20000 | 🏆 **PalORM** | 136.77 ms [1.00] / 11.6MB [1.00] | **91.17 ms [0.67] / 4.3MB [0.37]** | 222.41 ms [**1.63**] / 67.0MB [**5.79**] | 152.19 ms [1.11] / 28.3MB [**2.45**] | 248.94 ms [**1.82**] / 57.4MB [**4.96**] |

### Table 3: Transactions (three dialects, smallest tier only)

**SQLite**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| TxSingleInsert | 2000 | ADO | **19.1 µs [1.00] / 4.0KB [1.00]** | 19.9 µs [1.04] / 4.3KB [1.08] | 20.3 µs [1.06] / 4.7KB [1.18] | 33.2 µs [**1.73**] / 7.0KB [**1.76**] | 20.9 µs [1.09] / 4.2KB [1.04] |
| TxHundredInserts | 2000 | 🏆 **PalORM** | 186.2 µs [1.00] / 137KB [1.00] | **180.6 µs [0.97] / 92.7KB [0.68]** | 248.4 µs [1.33] / 220KB [**1.61**] | 1.53 ms [**8.20**] / 541KB [**3.96**] | 558.5 µs [**3.00**] / 295KB [**2.16**] |
| TxBulkInsert | 2000 | 🏆 **PalORM** | 2.56 ms [1.00] / 1.7MB [1.00] | **2.52 ms [0.98] / 1.7MB [1.00]** | 4.68 ms [**1.83**] / 4.3MB [**2.48**] | 7.62 ms [**2.97**] / 3.5MB [**2.02**] | 16.01 ms [**6.24**] / 2.6MB [**1.54**] |
| TxRollback | 2000 | ADO | **596.8 µs [1.00] / 435KB [1.00]** | 636.9 µs [1.07] / 437KB [1.01] | 2.12 ms [**3.56**] / 1.6MB [**3.74**] | 7.63 ms [**12.79**] / 3.5MB [**8.16**] | 2.65 ms [**4.45**] / 2.0MB [**4.60**] |
| EnumInserts | 2000 | ADO | **134.3 µs [1.00] / 59.1KB [1.00]** | 147.8 µs [1.10] / 59.1KB [1.00] | 357.4 µs [**2.66**] / 223KB [**3.77**] | 1.28 ms [**9.53**] / 497KB [**8.42**] | 477.1 µs [**3.55**] / 232KB [**3.92**] |

**MySQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| TxSingleInsert | 2000 | 🏆 **PalORM** | 4.44 ms [1.00] / 25.4KB [1.00] | **4.43 ms [1.00] / 22.3KB [0.88]** | 4.52 ms [1.02] / 21.8KB [0.86] | 4.60 ms [1.04] / 27.5KB [1.08] | 4.50 ms [1.01] / 22.2KB [0.87] |
| TxHundredInserts | 2000 | 🏆 **PalORM** | 25.90 ms [1.00] / 377KB [1.00] | **25.45 ms [0.98] / 304KB [0.81]** | 26.88 ms [1.04] / 353KB [0.94] | 28.44 ms [1.10] / 811KB [**2.15**] | 27.50 ms [1.06] / 612KB [**1.63**] |
| TxBulkInsert | 2000 | RepoDb | 11.84 ms [1.00] / 489KB [1.00] | 16.27 ms [1.37] / 508KB [1.04] | 20.00 ms [**1.69**] / 6.7MB [**13.93**] | 21.00 ms [**1.77**] / 3.4MB [**7.16**] | **11.80 ms [1.00] / 489KB [1.00]** |
| TxRollback | 2000 | 🏆 **PalORM** | 96.38 ms [1.00] / 1.4MB [1.00] | **95.36 ms [0.99] / 1.4MB [1.02]** | 101.83 ms [1.06] / 2.4MB [**1.78**] | 118.15 ms [1.23] / 4.9MB [**3.61**] | 108.02 ms [1.12] / 3.6MB [**2.63**] |
| EnumInserts | 2000 | 🏆 **PalORM** | 25.43 ms [1.00] / 277KB [1.00] | **25.04 ms [0.98] / 286KB [1.03]** | 25.95 ms [1.02] / 423KB [**1.53**] | 28.91 ms [1.14] / 772KB [**2.78**] | 26.88 ms [1.06] / 522KB [**1.88**] |

**PostgreSQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| TxSingleInsert | 2000 | ADO | **1.52 ms [1.00] / 8.5KB [1.00]** | 1.55 ms [1.02] / 9.3KB [1.10] | 1.55 ms [1.02] / 8.2KB [0.97] | 1.57 ms [1.03] / 12.3KB [1.45] | 1.57 ms [1.03] / 8.8KB [1.04] |
| TxHundredInserts | 2000 | 🏆 **PalORM** | 21.24 ms [1.00] / 316KB [1.00] | **20.98 ms [0.99] / 213KB [0.68]** | 21.43 ms [1.01] / 285KB [0.90] | 24.42 ms [1.15] / 704KB [**2.23**] | 22.91 ms [1.08] / 435KB [1.38] |
| TxBulkInsert | 2000 | 🏆 **PalORM** | 4.97 ms [1.00] / 339KB [1.00] | **4.94 ms [0.99] / 340KB [1.00]** | 15.14 ms [**3.04**] / 6.7MB [**20.17**] | 21.32 ms [**4.29**] / 3.4MB [**10.28**] | 5.43 ms [1.09] / 843KB [**2.48**] |
| TxRollback | 2000 | ADO | **104.60 ms [1.00] / 938KB [1.00]** | 105.77 ms [1.01] / 1.0MB [1.11] | 104.73 ms [1.00] / 1.7MB [**1.86**] | 120.15 ms [1.15] / 4.4MB [**4.82**] | 114.48 ms [1.09] / 2.9MB [**3.15**] |
| EnumInserts | 2000 | 🏆 **PalORM** | 20.40 ms [1.00] / 165KB [1.00] | **19.42 ms [0.95] / 182KB [1.11]** | 21.03 ms [1.03] / 283KB [**1.72**] | 24.21 ms [1.19] / 662KB [**4.02**] | 22.31 ms [1.09] / 354KB [**2.15**] |

### Table 4: Cross-dialect PalORM/ADO ratio (bulk family + GetAllAsync; raw ratio, 📌 = outside the line)

| Operation | Tier | SQLite | PostgreSQL | MySQL |
|---|---:|---:|---:|---:|
| BulkInsert | 2000 | 1.01 | 1.01 | 1.03 |
| BulkInsert | 20000 | 1.03 | 0.96 | 1.00 |
| BulkUpdate | 2000 | 1.01 | 1.04 | 0.92 |
| BulkUpdate | 20000 | 1.02 | 0.94 | 0.97 |
| BulkDelete | 2000 | 1.03 | 0.68 | 1.13 |
| BulkDelete | 20000 | 0.93 | 📌0.40 | 0.92 |
| UpsertBatch | 2000 | 0.99 | 📌0.53 | 0.97 |
| UpsertBatch | 20000 | 0.99 | 📌0.67 | 1.06 |
| GetAllAsync | 2000 | 0.98 | 1.11 | 1.02 |
| GetAllAsync | 20000 | 1.01 | 0.99 | 1.02 |

## 📦 Installation

> Pick one of the three provider packages; each automatically brings `PalORM.Core` (runtime) and `PalORM.SourceGen` (compile-time generator).
> Requirements: .NET SDK `11.0.100-rc.2` or later (`global.json` pins `rollForward: disable`); your IDE must support Roslyn source generators (SourceGen builds against Microsoft.CodeAnalysis 5.9.0). Native AOT publishes additionally need about 4 GB of memory (ILC compiler) — see [Development](#-development).

```xml
<!-- PostgreSQL -->
<PackageReference Include="PalORM.PostgreSql" Version="6.8.0" />
<!-- MySQL -->
<PackageReference Include="PalORM.MySql" Version="6.8.0" />
<!-- SQLite -->
<PackageReference Include="PalORM.Sqlite" Version="6.8.0" />
```

Each provider package pulls in `PalORM.Core` (runtime) and `PalORM.SourceGen` (compile-time source generator). After installing, verify with the minimal Quick Start example below: if you can create a session and complete one insert, the install works.

### Database compatibility

| Database | Version | Driver | Encryption |
|----------|---------|--------|:---:|
| PostgreSQL | 14+ (18 recommended) | Npgsql 10.0.3 | SSL/TLS |
| MySQL | 8.0+ (8.4 LTS recommended) | MySqlConnector 2.6.2 | SSL/TLS |
| SQLite | 3.54+ (via SQLite3MC 2.6.1) | Microsoft.Data.Sqlite.Core 11.0.0-rc.2 | AES-256 (driver level, `Password=`) |

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
// Bulk insert (PG: Binary COPY / MySQL: BulkCopy or multi-value INSERT / SQLite: row-prepared bulk)
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
| `BulkInsertAsync` | Binary COPY | BulkCopy (local_infile) or multi-value INSERT | Row-prepared bulk |
| `BulkUpdateAsync` | Versioned single statement + optimistic lock (falls back to packed DbBatch) | Versioned single statement + optimistic lock (falls back to packed DbBatch) | Row-by-row + optimistic lock |
| `BulkUpdateBatchAsync` | `FROM VALUES` | `UPDATE JOIN VALUES ROW` (8.0.19+; CASE WHEN fallback on older) | Automatic row-by-row fallback |
| `BulkDeleteAsync` | Single `= ANY(array)` statement | Batched `IN` statements | `json_each` single server-side-expanded parameter |
| `BulkMergeAsync` | Multi-row UPSERT (`ON CONFLICT DO UPDATE`) | Multi-row UPSERT (`ON DUPLICATE KEY UPDATE`) | Row-prepared UPSERT (`ON CONFLICT DO UPDATE`, single-statement cache reuse) |

`BulkMergeAsync` (set-based since v5.6.0) partitions by key state: default-key rows (new) go row-by-row INSERT to preserve the ID-backfill contract; on PG/MySQL non-default-key rows are batched by dialect parameter limits into multi-row UPSERT (PG and MySQL 65535 parameter limit; MySQL ODKU batches of 1000 rows), while SQLite uses the row-prepared UPSERT (single-statement cache reuse, no parameter-limit batching); `[ConcurrencyCheck]` entities stay row-by-row (UPSERT cannot honor optimistic locking).

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
| Bulk insert, no ID backfill needed | `BulkInsertAsync` | row-by-row / `SessionBatch` | Fastest path per dialect (COPY / LOAD DATA / row-prepared); measured 8.4× vs row-by-row for 100 rows in a transaction |
| Bulk insert with per-row identity backfill | `BulkMergeAsync` (default-key rows backfill) or `SessionBatch` | `BulkInsertAsync` | COPY / LOAD DATA do not backfill IDs |
| Mixed statements or per-row control | `SessionBatch` (packed DbBatch) | row-by-row round trips | Multiple statements per round trip; SQLite 20 statements per batch |
| Bulk update by non-key columns (different values per row) | `BulkUpdateBatchAsync` | row-by-row | Single multi-row SET statement (PG `FROM VALUES` / MySQL `JOIN VALUES ROW`) |
| Bulk update of optimistic-lock entities | `BulkUpdateAsync` | `BulkUpdateBatchAsync` | UPSERT/single-statement shapes cannot verify versions per row; DbBatch packing preserves semantics |
| Insert-or-update (update on key conflict) | `BulkMergeAsync` | hand-written ON CONFLICT | Tenant entities are automatically scoped to the current tenant (cross-tenant hits match 0 rows) |
| Large key-set deletes (tens of thousands and up) | `BulkDeleteAsync` | per-row Delete | SQLite json_each single server-side-expanded parameter (no parameter-limit batching) / PG = ANY array / MySQL IN batched by limit, one transaction over the whole call, parameter objects reused across batches |

## ✅ Best-practice cheat sheet

Grouped by frequency; detailed rationale at each linked section:

**Session and connections**
- Reuse one `DataSession` per scope; do not `CreateAsync` per operation (from the 3rd same-shape operation the session reuses the command and parameter slots automatically)
- Web/API request-per-session deployments should use `DataSessionPool` (create the pool at startup, `RentAsync` to rent, `DisposeAsync` to return): reuse slots surviving across rentals is the core pooling benefit; without pooling they never activate in that shape
- Pre-warm the connection pool at startup with `PreWarmAsync` (remote connection setup measured ~8.5 ms each), paired with `MinPoolSize` to stay warm
- High-frequency single-key lookups use `GetAsync` (25~30% faster and 880 B cheaper than the chained equivalent); use `From<T>()` only with filter conditions
- With `ReadConnectionString` configured, put report/list queries on `.ForRead()` explicitly; replication-lag-sensitive strong reads stay on the primary
- Declare `await using (db.ForParallelReads())` before concurrent queries; inside the scope `GetAsync` and the query family use the scope-local read pool (up to 8 connections); writes that overlap an in-flight read are rejected by the single-operation gate
- A "not registered" error for cross-assembly models: each model assembly registers via `ModuleInitializer` on first touch, so make sure entity types are genuinely referenced (instantiation/member access both count as touching)

**Queries and result sets**
- Stream unbounded tables with `QueryAsyncEnumerable<T>` (peak memory O(1)) instead of `ToListAsync`
- Deep paging uses `ToPageAsync` keyset cursors, not large OFFSETs (scan volume grows linearly with offset)
- Filter large key sets with `WhereIn`/`WhereNotIn` (automatic 500-key batching, empty sets safely false; the total bind-parameter count is still bounded by the dialect, SQLite 999 / general 65535, and exceeding it throws `ArgumentException` up front — use a temp-table JOIN for larger sets)
- Hoist hot-path `OrderBy`/`Select` expressions to static fields (saves 512 B + 0.5~1.6 µs per call)
- Preview SQL with `AsDryRun()`/`ToSql` (SQL + parameters, no execution) when troubleshooting; in production, enable AutoTagging (`PalORMAutoTagging` build switch) to auto-inject "file:line member" comments into SQL
- `Include`/`ThenInclude` generate JOINs without navigation assembly (group by parent yourself); for column-trimmed materialization use `[Projection]` DTOs + `QueryAsync<T>` (`Select` projection only works with DryRun/ToSql)
- `WithCache` is opt-in per query (off by default); without an injected `DbOptions.QueryCache` it uses the process-wide default instance (1024 entries, tenant-isolated by the `__t:` prefix); inject an independent instance to control capacity/TTL per tenant; TTL is eventually consistent, use `db.EvictQueryCache()` to narrow the read-after-write window

**Writes and bulk operations**
- Pick bulk APIs by the cheat sheet above; never loop `InsertAsync` (round trips are an order-of-magnitude difference)
- Catch `UniqueConstraintViolationException` for unique-key conflicts (unified across dialects, no driver error codes to memorize); catch `ConcurrencyConflictException` for optimistic-lock conflicts (re-read, merge, retry)
- Never call `BulkUpdateBatchAsync` on `[ConcurrencyCheck]` entities (single-statement shape cannot verify per-row versions; PALORM031 already blocks it at compile time); use `BulkUpdateAsync` (packed DbBatch preserves semantics)
- `BulkInsertAsync` does not backfill auto-increment IDs (COPY / LOAD DATA contract); for per-row backfill use `BulkMergeAsync` (default keys go row-by-row) or `SessionBatch`
- Declare nullable properties for columns that may hold NULL (`string?`); non-nullable properties fail loudly with `SqlNullValueException` on NULL (never silently null)
- Mark sensitive columns with `[SensitiveData]` (`Set()` values for those columns are masked in audit logs; values in hand-written Where SQL have no column mapping and are not masked); do not rely on automatic retry for non-idempotent writes (write paths are direct), wrap with `ExecuteWithResilience` only after assessing duplication risk

**Transactions**
- Inside a transaction prefer `BulkInsertAsync` over `SessionBatch` (100 rows: 5.4ms vs 16.8ms)
- Queries and raw SQL inside transactions are not retried automatically (retries would mask the root cause with secondary exceptions); use `WithTransactionRetry` for transaction-level tolerance (replays the whole transaction on dialect transient codes) with an idempotent callback (at-least-once contract)
- Nested transactions are unsupported (a second `BeginTransactionAsync` throws loudly); use savepoints `SavepointAsync`/`RollbackToAsync` for partial rollback inside long transactions
- After adopting an external transaction via `UseTransaction`, using the session after the external Dispose fails loudly (no silent auto-commit)

**Multi-tenancy and cross-cutting concerns**
- Call `WithTenant(id)` right after creating a session for `[TenantAware]` entities, before the first query (cache-key prefixes follow the tenant)
- `InsertAsync`/`BulkInsertAsync`/`BulkMergeAsync` add no tenant predicate: the caller must assign `tenant_id` (PALORM040 only catches nullable columns); the tenant column is immutable in Update/BulkUpdate and UPSERT conflict updates (prevents cross-tenant moves) — use raw SQL for a deliberate migration
- Use `IgnoreFilters()` for cross-tenant administrative queries; it switches the session to the unfiltered view and there is no public reset API, so use a separate session afterwards (renting again from the pool is enough) rather than continuing tenant-scoped work on the same session
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

> Start from the three presets (Development / Production / Testing); override any of the 24 settings as needed. Connection-string auto-tuning only fills driver defaults and never overrides explicit values.

### Presets

```csharp
var dev  = DbOptions.Development(connectionString);            // development
var prod = DbOptions.Production(connectionString, readConn);  // production (pooling + read/write splitting)
var test = DbOptions.Testing(connectionString);               // testing (zero retries + short timeouts)
var env  = DbOptions.FromEnvironment("PALORM_CONNECTION");    // environment variables (Docker/K8s friendly)
```

Environment variables supported by `FromEnvironment`: `PALORM_CONNECTION` (required), `PALORM_READ_CONNECTION`, `PALORM_COMMAND_TIMEOUT`, `PALORM_CONNECTION_TIMEOUT`, `PALORM_MAX_RETRIES`, `PALORM_CIRCUIT_BREAKER_THRESHOLD`, `PALORM_MAX_POOL_SIZE`; the result is `Validate()`d automatically so invalid values fail at startup.

### Options

**Connection & pooling**

| Property | Type | Default | Notes |
|---|---|:---:|---|
| `ConnectionString` | `string` (required) | — | Primary connection string, supports `$ENV:VAR` references |
| `ReadConnectionString` | `string?` | null | Read-replica string; `ForRead()` routes automatically when set |
| `MaxPoolSize` | `int` | 100 | Pool upper bound (ignored for SQLite) |
| `MinPoolSize` | `int` | 0 | Warm-connection floor (0=driver default) |
| `PoolIdleTimeoutSeconds` | `int` | 0 | Idle timeout seconds (0=driver default) |
| `PoolLifetimeMinutes` | `int` | 60 | Max connection lifetime; force-rebuilt on expiry |
| `PoolConnectionValidationInterval` | `TimeSpan` | 30s | Liveness-probe interval on rentals (0=off) |

**Timeouts, retries, breaker**

| Property | Type | Default | Notes |
|---|---|:---:|---|
| `ConnectionTimeout` | `TimeSpan` | 15s | Connection establishment timeout (including retries) |
| `CommandTimeout` | `TimeSpan` | 30s | Command execution timeout; sub-second values round up to 1 second |
| `MaxRetries` | `int` | 3 | Max transient-failure retries (0=disabled, cap 10; read pipeline only) |
| `RetryBackoff` | `Func<int, TimeSpan>?` | 100→200→400ms | Custom retry backoff (parameter = retry count) |
| `OverallDeadline` | `TimeSpan` | Zero (off) | Total budget per operation; retries stop and a timeout is thrown on expiry |
| `CircuitBreakerThreshold` | `int` | 5 | Consecutive-failure threshold (0=disable the breaker) |
| `CircuitBreakerResetAfter` | `TimeSpan` | 30s | Wait before the breaker moves to half-open |
| `CircuitBreakerScope` | `CircuitBreakerScope` | Session | Breaker scope (Session/Process, see notes) |

**Session & behavior**

| Property | Type | Default | Notes |
|---|---|:---:|---|
| `SessionSetupSql` | `string?` | null | SQL executed when the primary connection first activates (semicolon-separated) |
| `ReadSessionSetupSql` | `string?` | null | Same for read-replica connections (once per session) |
| `Interceptors` | `IReadOnlyList<IQueryInterceptor>?` | null | Query interceptors, ascending `Priority` order |
| `ValidateQueryColumnOrder` | `bool` | true | First-row column-order check on `QueryAsync` (see notes) |
| `UseSynchronousRowRead` | `bool` | false | Materialized reads use sync `Read()` (recommended on MySQL, see notes) |
| `PrepareCacheSize` | `int` | 100 | PG AutoPrepare cache capacity (0=disable; other dialects ignore) |
| `PrepareThreshold` | `int` | 2 | PG auto-prepare threshold for repeated SQL (min 1; other dialects ignore) |
| `QueryCache` | `IQueryCache?` | 1024 entries | Query cache (process-wide shared by default) |
| `LoggerFactory` | `ILoggerFactory?` | null | When set, `DataSession` creates `ILogger`s |

Notes (mechanics):

- `OverallDeadline`: unset, worst-case wall clock ≈ (MaxRetries+1)×CommandTimeout+Σbackoff (about 120 s with defaults); should exceed a single `CommandTimeout`. On expiry a `TimeoutException` tagged `PalORM.InfrastructureTimeout` is thrown.
- `CircuitBreakerScope`: Session counts failures per session (fine for long-lived sessions); Process accumulates across sessions keyed by (Provider, connection string, threshold, cooldown) and is mandatory for short-lived per-request sessions, otherwise the breaker can never trip. The `Production` preset already defaults to `Process`.
- `MinPoolSize` / `PoolIdleTimeoutSeconds`: 0=keep driver default (Npgsql 300s / MySqlConnector 180s); only positive values override. Positive floors eliminate latency spikes from rebuilding connections on bursts, at the cost that the first query after an idle expiry rebuilds a physical connection (measured cross-subnet `SELECT 1`: 0.3 ms pooled vs 13.5 ms fresh; IdleTimeout defaults to 0 since v5.6.0).
- `RetryBackoff`: default backoff includes 50~100% jitter; negative values throw.
- `UseSynchronousRowRead`: measured on MySQL at ~998 vs 1846~2043 ns/row; SQLite is always synchronous (in-process engine); streaming APIs are always async; validate thread-pool pressure on high-concurrency servers first.
- `ValidateQueryColumnOrder`: disable when using column aliases/expression columns.
- `QueryCache`: inject an independent instance for session/tenant-level isolation (must be thread-safe).

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

Software: .NET SDK 11.0.100-rc.2+ (pinned by `global.json`) · C# `latest` · Windows 10+ / Linux / macOS · Visual Studio 2026 / Rider / VS Code (Roslyn source generator support required).

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

[Apache-2.0](LICENSE)

Released versions up to and including v6.7.0 remain under AGPL-3.0-only;<br>
versions from v6.8.0 onward are uniformly released under Apache-2.0.

<div align="right">

[⬆ Back to top](#palorm)

</div>
