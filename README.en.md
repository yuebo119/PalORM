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
[![Dialects](https://img.shields.io/badge/Dialects-PostgreSQL_·_MySQL_·_SQLite-512BD4)](#database-compatibility)

[简体中文](README.md) | English

</div>

A Roslyn source generator produces SQL construction, parameter binding, object mapping, and migration DDL at compile time — zero reflection, zero IL Emit at runtime. PostgreSQL / MySQL / SQLite across three dialects, with enterprise features out of the box: multi-tenancy, optimistic locking, soft delete, auditing, advisory locks, and more.

> Detailed docs (in Chinese) live under [docs/](docs/) — [API reference](docs/API参考.md), [architecture](docs/架构设计.md), [AOT deployment guide](docs/AOT部署指南.md), [benchmark methodology](docs/性能基准规范.md).

---

Deep docs: [Best practices](docs/最佳实践.md) · [Feature panorama](docs/功能特性.md) · [API reference](docs/API参考.md) · [Configuration reference](docs/配置参考.md) · [AOT deployment guide](docs/AOT部署指南.md) · [Architecture](docs/架构设计.md) · [Benchmark methodology](docs/性能基准规范.md) · [Coding standards](docs/编码规范.md) · [Release SOP](docs/发布规范.md) · [Upgrade guide](docs/升级指南.md) · [ADRs](docs/adr/) · [Contributing](CONTRIBUTING.md) · [Changelog](CHANGELOG.md)

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

> Versions (verified 2026-10-26): PalORM 6.8.0 · Dapper 2.1.89 + Dapper.AOT 1.1.0 · EF Core 10.0.14 · RepoDb 1.17.0.

| Feature | **PalORM** | Dapper | EF Core | RepoDb |
|------|:---:|:---:|:---:|:---:|
| **AOT & generation timing** | ✓ everything source-generated at compile time (SQL / binding / mapping / DDL), zero reflection at runtime | △ hand-written SQL; optional Dapper.AOT (interceptor-based generation) | △ runtime expression trees + model building; AOT needs Compiled Model + precompiled queries | △ runtime reflection + compiled expressions, AOT relies on trimming annotations |
| **Compile-time diagnostics** | ✓ 45 rules (42 analyzers + 3 generators), jumps to declarations | ❌ fails at runtime | △ migration checks (design time) | ❌ fails at runtime |
| **SQL injection surface** | ✓ single raw-SQL shape `FormattableString`, no bare-`string` overload | △ `string` + anonymous-object params (reflection-bound) | △ LINQ parameterized, `FromSqlRaw` keeps a bare-string shape | △ values flow into runtime expression trees |
| **Query authoring model** | ✓ chained builder (SQL visible) + `FormattableString` | △ hand-written SQL | ✓ strongly-typed LINQ translation | △ QueryBuilder |
| **Per-dialect bulk strategies** | ✓ COPY / BulkCopy / row-prepared / `= ANY`; IN sets batch in 500s | ❌ hand-written multi-value SQL and IN strings | △ varies by provider | △ bulk extension packages cover 17 providers, no auto-batching |
| **Single-statement multi-row UPDATE** | ✓ FROM VALUES / JOIN VALUES ROW / CASE WHEN | ❌ | ❌ `ExecuteUpdate` is single-value per WHERE | ❌ |
| **Bulk UPSERT** | ✓ `BulkMergeAsync` (conflict updates tenant-scoped) | ❌ hand-written | ❌ raw SQL required | △ `Merge` in the core package |
| **Optimistic locking** | ✓ `[ConcurrencyCheck]`; bulk uses versioned single statements | ❌ hand-written | ✓ `RowVersion` automatic | ❌ hand-written |
| **Soft delete** | ✓ `[SoftDelete]` automatic filtering (bulk delete becomes UPDATE) | ❌ | ✓ global filters | ❌ |
| **Multi-tenant isolation** | ✓ `[TenantAware]` compile-time isolation + cross-tenant write guards | ❌ | △ manual implementation | ❌ |
| **Schema migration** | ✓ `MigrateAsync` (FK order + race tolerance) | ❌ no migration system | ✓ Migrations (most complete) | ❌ |
| **JSON columns** | ✓ `[OwnedJson]` source-generated; PG `WhereJson` path queries | △ PropertyHandler (AOT needs `JsonTypeInfo`) | ✓ EF7+ mapping + path queries | △ PropertyHandler, no path queries |
| **Navigation assembly** | △ `Include` emits JOINs without assembly (group by parent yourself) | △ hand-written multi-mapping | ✓ full assembly + change tracking | ❌ |
| **Change tracking / UoW** | ❌ stateless design (zero tracking overhead) | ❌ | ✓ ChangeTracker | ❌ |
| **Streaming** | ✓ `QueryAsyncEnumerable` / `ForEachAsync` (O(1) memory) | △ hand-written CommandBehavior | ✓ `AsAsyncEnumerable` | ❌ |
| **Breadth & dependencies** | three dialects deeply tuned; zero third-party core deps; single target `net11.0`; Apache-2.0 | any ADO.NET; multi-target (netstandard2.0 / net8.0 / net461); Apache-2.0 | provider matrix; many packages, heavy deps; `net10.0`; MIT | 20 provider packages; core multi-target; Apache-2.0 |

### AOT implementation approaches (PalORM / Dapper.AOT / RepoDb / EF Core / FreeSql)

What matters for AOT is how each library replaces runtime reflection: **compile-time source generation** (PalORM, Dapper.AOT), **expression interpretation + trimming annotations** (RepoDb), **runtime building with optional pre-generation** (EF Core), **runtime reflection with no adaptation** (FreeSql).

| Dimension&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp; | PalORM 6.8.0                                                                                                                                           | Dapper.AOT 1.1.0                                                                                                                                                                                                                                                             | RepoDb 1.17.0&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;                                         | EF Core 10.0.14&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;                                                                       | FreeSql 3.5.311                                                                                                                                                                              |
|-----------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Implementation approach** | Compile-time source generation, zero runtime reflection | Source generation + interceptors replacing call sites | Runtime expression trees, interpreted under AOT | Runtime expression-tree compilation + model building | Runtime reflection, no AOT adaptation |
| **AOT support level** | ✅ **Full**: three-dialect publish passes, zero IL warnings, 4 CI gates | 🟦 **High**: core APIs generated, explicit opt-in | 🟨 **Medium**: regular entities and LINQ work | 🟧 **Low**: Compiled Model + precompiled queries, experimental | 🟥 **None**: zero annotations, no AOT CI, 7 of 9 default-publish projects fail |
| **Defects & failure modes** | Unsupported shapes rejected at compile time, no runtime failure surface | Uncovered APIs fall back to reflection (unsafe); Contrib and non-generic `typeof` unsupported; misconfigured interceptors silently skip | dynamic / Expando annotated unsafe; SQLite smoke only; interpreted execution slower | Precompiled queries and model must be hand-written and maintained; capability is a JIT subset | Needs TrimmerRootAssembly roots; 212 IL warnings with analyzers on; crashes at runtime (trimmed constructors) |
| **Performance under AOT** | Equivalent to JIT | Bypasses reflection; officially AOT-safe | Per-query overhead above JIT | Depends on precompilation, no commitment | Interpreted degradation + one-time metadata build |
| **Integration constraints** | `[Table]` / `[Projection]` annotations + public parameterless ctor; generator ships with the package | Zero rewrite; explicit opt-in; declarative type handlers | Works out of the box; avoid dynamic APIs under AOT | Precompiled queries + Compiled Model required | TrimmerRootAssembly + per-feature native smoke tests |
| **Escape hatch & boundaries** | `FormattableString` / `[SqlFile]` are the AOT-safe outlets; no dynamic entities, no full LINQ translation | Uncovered calls must be rewritten; minor behavioral differences allowed | No safe outlet; wide API surface under JIT, 20 providers | No escape-hatch promise; most complete JIT feature set | No escape hatch; broadest surface, AOT is the user's responsibility |

> Scale (under AOT): ✅ Full · 🟦 High (with defects) · 🟨 Medium · 🟧 Low · 🟥 None

## 📊 Performance

> **Test environment**: AMD Ryzen 9 8945HX (32 logical cores) · Windows 10 22H2 · .NET 11 RC2 · SQLite temp-file DB (WAL + 64 MB cache + mmap) · PG 18.6 / MySQL 8.4.11 local containers. **Five arms, same process, same round, same data, same instrumentation**: ADO.NET (hand-written floor) / Dapper 2.1.89 / FreeSql 3.5.311 / RepoDb 1.17.0 / PalORM — each arm uses its ecosystem's idiomatic best practice; every operation runs twice with reversed arm order and takes the median (removes arm-order phase bias).
>
> **Data batch**: 2026-10-28, code `04d40f3`. CrossBench five-arm complete window (`crossbench-20261028-122807` / `-130313` / `-132112.json`, 870 measurements each; **3-batch median** absorbs single-batch spikes — across this window only one cell exceeded the 20% ADO floor-spread marker, down from 19 in the previous window). All numbers are derived from the batch JSON by the frozen template script (`scripts/crossbench-report.cs`); full report at [fivearm-20261028.md](bench/crossbench/results/fivearm-20261028.md) (committed). PerfHub/PerfGate gate caliber is a different fixture and tier set — see [bench/perfhub/README.md](bench/perfhub/README.md).

**Reading the tables**: each cell = time [ratio vs ADO] / memory [ratio vs ADO]; [1.00] = ±0.5% parity band; ≤0.67 or ≥1.5 bold = outside the line; 📌 = flagged for review; 🏆 = fastest arm in the cell. Color scale (positive % = slower/more than baseline): 🟢 strong win ≤−30% · 🟩 mid win −29%~−10% · 🔹 slight win −9%~−1% · 🔸 slight loss +1%~+9% · 🟧 mid loss +10%~+29% · 🟥 strong loss ≥+30%.

### Overview

5 arms × 3 dialects × 14 operations (CRUD and bulk at two tiers each, transactions at the 2000 tier only) = **69 cells**, 3-batch median window. Every ratio is against hand-written ADO.NET SQL [1.00].

#### CRUD single-row and reads (baseline = ADO [1.00])

- 🥇 GetByKey SQLite: PalORM **0.77** (23% faster), FreeSql **4.58** (358% slower, worst across dialects at 5.36)
- QueryAll MySQL 20000 allocation: PalORM **0.91** (9% faster), Dapper 1.11 (11% slower)
- Insert SQLite: PalORM 0.96 (4% faster), FreeSql **1.73** (73% slower)
- RepoDb single-row and reads: worst 1.38 (38% slower) — expression-based materialization, same order of magnitude as hand-written SQL

#### Bulk processing (baseline = ADO [1.00])

- 🥇 BulkDelete PG 20000: PalORM **0.40** (60% faster), allocation **0.10** (90% faster)
- 🥇 UpsertBatch PG 20000: PalORM **0.62** (38% faster), allocation **0.37** (63% faster)
- BulkInsert SQLite 20000: PalORM 0.99 (1% faster), Dapper 1.73 (73% slower), FreeSql **4.14** (314% slower)
- BulkUpdate three dialects both tiers: PalORM 0.94~1.03, Dapper worst **3.80** (280% slower)
- RepoDb bulk family: best BulkInsert MySQL **0.97** (3% faster), worst BulkDelete SQLite **23.85** (2285% slower)

#### Transactions (baseline = ADO [1.00])

- PalORM across all items 0.95~1.13, fastest EnumInserts MySQL **0.95**
- FreeSql worst TxRollback SQLite **13.31**, Dapper worst TxRollback SQLite **3.63**
- EnumInserts SQLite allocation: PalORM 1.00 (even), FreeSql **8.42** (742% slower)

#### Fastest wins

| Arm | Fastest / 69 cells |
|---|---:|
| PalORM | **32** (46%) |
| ADO.NET (floor) | **31** (45%) |
| Dapper | **4** (6%) |
| RepoDb | **2** (3%) |
| FreeSql | **0** (0%) |

> P/ADO median **1.00** (even with the hand-written SQL floor), range 0.40~1.13.

### Table 1: CRUD single-row and reads (three dialects, two tiers)

> Wins: PalORM 14 · ADO 13 · Dapper 2 · RepoDb 1 · FreeSql 0

**SQLite**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| GetByKey | 2000 | 🏆 **PalORM** | 7.5 µs [1.00] / 2.1KB [1.00] | **5.8 µs [0.77] / 2.1KB [1.02]** | 10.0 µs [1.34] / 2.8KB [1.34] | 34.1 µs [**4.58**] / 10.2KB [**4.85**] | 9.5 µs [1.28] / 4.4KB [**2.10**] |
| GetAllAsync | 2000 | 🏆 **PalORM** | 963.3 µs [1.00] / 308KB [1.00] | **941.0 µs [0.98] / 294KB [0.95]** | 1.35 ms [1.40] / 513KB [**1.66**] | 1.36 ms [1.42] / 314KB [1.02] | 1.16 ms [1.20] / 372KB [1.21] |
| QueryAll | 2000 | 🏆 **PalORM** | 954.6 µs [1.00] / 308KB [1.00] | **942.9 µs [0.99] / 292KB [0.95]** | 1.35 ms [1.42] / 513KB [**1.66**] | 1.35 ms [1.41] / 314KB [1.02] | 1.15 ms [1.20] / 372KB [1.21] |
| Insert | 2000 | 🏆 **PalORM** | 17.4 µs [1.00] / 2.8KB [1.00] | **16.6 µs [0.96] / 2.7KB [0.97]** | 18.0 µs [1.04] / 3.6KB [1.29] | 30.0 µs [**1.73**] / 5.7KB [**2.07**] | 19.1 µs [1.10] / 3.0KB [1.08] |
| Update | 2000 | 🏆 **PalORM** | 7.1 µs [1.00] / 2.5KB [1.00] | **7.0 µs [0.99] / 2.9KB [1.15]** | 7.8 µs [1.09] / 3.3KB [1.30] | 19.4 µs [**2.73**] / 7.4KB [**2.94**] | 9.0 µs [1.27] / 4.1KB [**1.62**] |
| GetByKey | 20000 | ADO | **5.8 µs [1.00] / 2.1KB [1.00]** | 5.9 µs [1.01] / 2.1KB [1.02] | 7.0 µs [1.20] / 2.8KB [1.33] | 31.4 µs [**5.36**] / 10.4KB [**4.94**] | 8.1 µs [1.38] / 4.4KB [**2.10**] |
| GetAllAsync | 20000 | 🏆 **PalORM** | 9.75 ms [1.00] / 3.3MB [1.00] | **9.64 ms [0.99] / 2.9MB [0.89]** | 13.77 ms [1.41] / 5.3MB [**1.61**] | 13.68 ms [1.40] / 3.3MB [1.00] | 11.82 ms [1.21] / 3.9MB [1.19] |
| QueryAll | 20000 | 🏆 **PalORM** | 9.68 ms [1.00] / 3.3MB [1.00] | **9.59 ms [0.99] / 2.9MB [0.89]** | 13.74 ms [1.42] / 5.3MB [**1.61**] | 13.81 ms [1.43] / 3.3MB [1.00] | 11.99 ms [1.24] / 3.9MB [1.19] |
| Insert | 20000 | 🏆 **PalORM** | 17.5 µs [1.00] / 2.8KB [1.00] | **17.1 µs [0.98] / 2.8KB [1.00]** | 18.1 µs [1.04] / 3.6KB [1.30] | 30.5 µs [**1.74**] / 5.8KB [**2.08**] | 19.2 µs [1.10] / 3.0KB [1.08] |
| Update | 20000 | ADO | **7.2 µs [1.00] / 2.5KB [1.00]** | 7.3 µs [1.01] / 2.9KB [1.15] | 7.9 µs [1.09] / 3.3KB [1.30] | 22.9 µs [**3.17**] / 7.4KB [**2.95**] | 9.1 µs [1.26] / 4.1KB [**1.62**] |

**MySQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| GetByKey | 2000 | 🏆 **PalORM** | 193.1 µs [1.00] / 4.5KB [1.00] | **191.8 µs [0.99] / 4.9KB [1.09]** | 196.4 µs [1.02] / 5.8KB [1.29] | 415.9 µs [**2.15**] / 16.6KB [**3.67**] | 207.3 µs [1.07] / 8.5KB [**1.89**] |
| GetAllAsync | 2000 | 🏆 **PalORM** | 1.09 ms [1.00] / 398KB [1.00] | **1.08 ms [0.99] / 384KB [0.96]** | 1.13 ms [1.03] / 447KB [1.12] | 1.36 ms [1.25] / 407KB [1.02] | 1.14 ms [1.05] / 401KB [1.01] |
| QueryAll | 2000 | ADO | **1.07 ms [1.00] / 398KB [1.00]** | 1.09 ms [1.02] / 384KB [0.96] | 1.12 ms [1.04] / 447KB [1.12] | 1.36 ms [1.27] / 407KB [1.02] | 1.15 ms [1.07] / 401KB [1.01] |
| Insert | 2000 | RepoDb | 3.99 ms [1.00] / 16.6KB [1.00] | 3.88 ms [0.97] / 16.5KB [1.00] | 3.89 ms [0.98] / 16.3KB [0.98] | 4.19 ms [1.05] / 22.3KB [1.35] | **3.88 ms [0.97] / 17.0KB [1.03]** |
| Update | 2000 | ADO | **189.9 µs [1.00] / 5.3KB [1.00]** | 192.8 µs [1.02] / 5.9KB [1.10] | 192.9 µs [1.02] / 5.2KB [0.97] | 400.4 µs [**2.11**] / 13.3KB [**2.49**] | 208.9 µs [1.10] / 7.6KB [1.42] |
| GetByKey | 20000 | ADO | **184.8 µs [1.00] / 4.5KB [1.00]** | 186.9 µs [1.01] / 4.9KB [1.09] | 188.1 µs [1.02] / 5.8KB [1.30] | 402.2 µs [**2.18**] / 16.8KB [**3.74**] | 197.6 µs [1.07] / 8.5KB [**1.89**] |
| GetAllAsync | 20000 | ADO | **9.43 ms [1.00] / 4.0MB [1.00]** | 9.43 ms [1.00] / 3.7MB [0.91] | 9.91 ms [1.05] / 4.5MB [1.11] | 10.56 ms [1.12] / 4.1MB [1.00] | 10.06 ms [1.07] / 4.0MB [1.00] |
| QueryAll | 20000 | 🏆 **PalORM** | 9.53 ms [1.00] / 4.0MB [1.00] | **9.53 ms [1.00] / 3.7MB [0.91]** | 9.97 ms [1.05] / 4.5MB [1.11] | 10.56 ms [1.11] / 4.1MB [1.00] | 10.06 ms [1.06] / 4.0MB [1.00] |
| Insert | 20000 | ADO | **3.85 ms [1.00] / 16.4KB [1.00]** | 3.92 ms [1.02] / 16.5KB [1.01] | 3.85 ms [1.00] / 16.5KB [1.00] | 4.24 ms [1.10] / 22.5KB [1.37] | 3.87 ms [1.01] / 17.1KB [1.04] |
| Update | 20000 | ADO | **190.5 µs [1.00] / 5.2KB [1.00]** | 191.2 µs [1.00] / 5.7KB [1.10] | 198.8 µs [1.04] / 5.2KB [1.00] | 402.9 µs [**2.11**] / 13.2KB [**2.54**] | 201.6 µs [1.06] / 7.6KB [1.46] |

**PostgreSQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| GetByKey | 2000 | 🏆 **PalORM** | 198.0 µs [1.00] / 3.0KB [1.00] | **196.1 µs [0.99] / 3.4KB [1.13]** | 198.3 µs [1.00] / 3.2KB [1.10] | 266.4 µs [1.35] / 12.0KB [**4.05**] | 209.7 µs [1.06] / 5.8KB [**1.95**] |
| GetAllAsync | 2000 | ADO | **639.1 µs [1.00] / 239KB [1.00]** | 642.9 µs [1.01] / 225KB [0.94] | 658.2 µs [1.03] / 442KB [**1.85**] | 752.6 µs [1.18] / 244KB [1.02] | 660.3 µs [1.03] / 241KB [1.01] |
| QueryAll | 2000 | ADO | **631.0 µs [1.00] / 239KB [1.00]** | 641.4 µs [1.02] / 225KB [0.94] | 646.1 µs [1.02] / 442KB [**1.85**] | 750.1 µs [1.19] / 244KB [1.02] | 650.2 µs [1.03] / 241KB [1.01] |
| Insert | 2000 | ADO | **1.35 ms [1.00] / 7.4KB [1.00]** | 1.36 ms [1.01] / 7.4KB [1.00] | 1.36 ms [1.01] / 7.2KB [0.97] | 1.42 ms [1.05] / 10.9KB [1.47] | 1.39 ms [1.03] / 7.8KB [1.04] |
| Update | 2000 | ADO | **1.36 ms [1.00] / 7.2KB [1.00]** | 1.39 ms [1.02] / 7.8KB [1.08] | 1.41 ms [1.03] / 7.2KB [1.00] | 1.46 ms [1.07] / 13.1KB [**1.81**] | 1.37 ms [1.01] / 9.5KB [1.31] |
| GetByKey | 20000 | ADO | **197.7 µs [1.00] / 3.0KB [1.00]** | 199.4 µs [1.01] / 3.4KB [1.14] | 199.8 µs [1.01] / 3.3KB [1.10] | 268.3 µs [1.36] / 12.1KB [**4.07**] | 207.9 µs [1.05] / 5.8KB [**1.95**] |
| GetAllAsync | 20000 | 🏆 **PalORM** | 5.09 ms [1.00] / 2.5MB [1.00] | **5.00 ms [0.98] / 2.2MB [0.86]** | 5.27 ms [1.04] / 4.5MB [**1.79**] | 5.80 ms [1.14] / 2.5MB [1.00] | 5.07 ms [1.00] / 2.5MB [1.00] |
| QueryAll | 20000 | 🏆 **PalORM** | 5.12 ms [1.00] / 2.5MB [1.00] | **5.02 ms [0.98] / 2.2MB [0.86]** | 5.25 ms [1.03] / 4.5MB [**1.79**] | 5.91 ms [1.15] / 2.5MB [1.00] | 5.10 ms [1.00] / 2.5MB [1.00] |
| Insert | 20000 | Dapper | 1.40 ms [1.00] / 7.6KB [1.00] | 1.38 ms [0.99] / 7.5KB [0.99] | **1.37 ms [0.98] / 7.3KB [0.96]** | 1.41 ms [1.01] / 10.9KB [1.44] | 1.39 ms [0.99] / 7.7KB [1.02] |
| Update | 20000 | Dapper | 1.38 ms [1.00] / 7.4KB [1.00] | 1.40 ms [1.01] / 7.8KB [1.07] | **1.37 ms [0.99] / 7.0KB [0.96]** | 1.45 ms [1.05] / 13.1KB [**1.78**] | 1.41 ms [1.02] / 9.5KB [1.29] |

### Table 2: Bulk processing (three dialects, two tiers)

> Wins: PalORM 13 · ADO 10 · RepoDb 1 · Dapper 0 · FreeSql 0

**SQLite**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| BulkInsert | 2000 | ADO | **2.52 ms [1.00] / 1.7MB [1.00]** | 2.54 ms [1.01] / 1.7MB [1.00] | 4.24 ms [**1.68**] / 4.3MB [**2.48**] | 10.51 ms [**4.17**] / 6.5MB [**3.79**] | 15.94 ms [**6.33**] / 2.6MB [**1.54**] |
| BulkUpdate | 2000 | ADO | **2.28 ms [1.00] / 1.7MB [1.00]** | 2.32 ms [1.02] / 1.8MB [1.04] | 8.68 ms [**3.80**] / 6.3MB [**3.75**] | 13.14 ms [**5.76**] / 7.0MB [**4.15**] | 6.77 ms [**2.97**] / 5.2MB [**3.06**] |
| BulkDelete | 2000 | ADO | **802.2 µs [1.00] / 197KB [1.00]** | 803.5 µs [1.00] / 198KB [1.01] | 21.70 ms [**27.05**] / 1.6MB [**8.12**] | 1.56 ms [**1.94**] / 568KB [**2.89**] | 19.13 ms [**23.85**] / 3.1MB [**15.96**] |
| UpsertBatch | 2000 | ADO | **2.33 ms [1.00] / 1.7MB [1.00]** | 2.41 ms [1.03] / 1.7MB [1.01] | 4.49 ms [**1.93**] / 4.2MB [**2.50**] | 3.57 ms [**1.53**] / 2.5MB [1.47] | 17.76 ms [**7.62**] / 11.6MB [**6.84**] |
| BulkInsert | 20000 | 🏆 **PalORM** | 28.08 ms [1.00] / 17.2MB [1.00] | **27.78 ms [0.99] / 17.2MB [1.00]** | 48.60 ms [**1.73**] / 42.5MB [**2.48**] | 116.33 ms [**4.14**] / 66.2MB [**3.85**] | 151.39 ms [**5.39**] / 26.2MB [**1.52**] |
| BulkUpdate | 20000 | 🏆 **PalORM** | 25.24 ms [1.00] / 17.0MB [1.00] | **24.94 ms [0.99] / 17.6MB [1.04]** | 91.86 ms [**3.64**] / 63.5MB [**3.74**] | 135.29 ms [**5.36**] / 73.4MB [**4.32**] | 70.85 ms [**2.81**] / 51.8MB [**3.05**] |
| BulkDelete | 20000 | 🏆 **PalORM** | 8.50 ms [1.00] / 2.0MB [1.00] | **8.19 ms [0.96] / 2.0MB [1.00]** | 217.06 ms [**25.54**] / 15.6MB [**7.70**] | 15.98 ms [**1.88**] / 6.0MB [**2.94**] | 192.10 ms [**22.60**] / 30.4MB [**15.01**] |
| UpsertBatch | 20000 | ADO | **25.52 ms [1.00] / 17.0MB [1.00]** | 26.63 ms [1.04] / 17.1MB [1.01] | 49.14 ms [**1.93**] / 42.4MB [**2.49**] | 40.96 ms [**1.61**] / 25.8MB [**1.52**] | 172.92 ms [**6.78**] / 116.0MB [**6.82**] |

**MySQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| BulkInsert | 2000 | RepoDb | 11.51 ms [1.00] / 478KB [1.00] | 11.68 ms [1.01] / 481KB [1.01] | 24.86 ms [**2.16**] / 6.7MB [**14.26**] | 17.08 ms [1.48] / 3.2MB [**6.92**] | **11.13 ms [0.97] / 475KB [0.99]** |
| BulkUpdate | 2000 | 🏆 **PalORM** | 10.06 ms [1.00] / 4.6MB [1.00] | **9.43 ms [0.94] / 3.7MB [0.80]** | 10.96 ms [1.09] / 6.5MB [1.41] | 9.69 ms [0.96] / 2.1MB [**0.45**] | 157.75 ms [**15.69**] / 903KB [**0.19**] |
| BulkDelete | 2000 | 🏆 **PalORM** | 12.31 ms [1.00] / 882KB [1.00] | **11.86 ms [0.96] / 806KB [0.91]** | 12.05 ms [0.98] / 1.3MB [**1.56**] | 16.94 ms [1.38] / 614KB [0.70] | 26.88 ms [**2.18**] / 19.3MB [**22.40**] |
| UpsertBatch | 2000 | 🏆 **PalORM** | 8.18 ms [1.00] / 2.9MB [1.00] | **8.12 ms [0.99] / 2.5MB [0.85]** | 10.52 ms [1.29] / 6.6MB [**2.28**] | 8.97 ms [1.10] / 2.5MB [0.86] | 161.31 ms [**19.73**] / 915KB [**0.31**] |
| BulkInsert | 20000 | ADO | **57.89 ms [1.00] / 4.4MB [1.00]** | 59.09 ms [1.02] / 4.4MB [1.00] | 257.05 ms [**4.44**] / 66.5MB [**15.00**] | 122.54 ms [**2.12**] / 33.2MB [**7.49**] | 58.10 ms [1.00] / 4.5MB [1.00] |
| BulkUpdate | 20000 | ADO | **84.87 ms [1.00] / 14.6MB [1.00]** | 87.18 ms [1.03] / 13.7MB [0.94] | 138.90 ms [**1.64**] / 64.5MB [**4.42**] | 92.02 ms [1.08] / 21.8MB [1.49] | 378.97 ms [**4.47**] / 5.2MB [**0.36**] |
| BulkDelete | 20000 | 🏆 **PalORM** | 74.12 ms [1.00] / 8.4MB [1.00] | **66.88 ms [0.90] / 3.5MB [0.42]** | 75.14 ms [1.01] / 13.2MB [**1.57**] | 180.75 ms [**2.44**] / 6.3MB [0.74] | 228.35 ms [**3.08**] / 193.5MB [**22.93**] |
| UpsertBatch | 20000 | ADO | **75.36 ms [1.00] / 12.9MB [1.00]** | 78.39 ms [1.04] / 12.6MB [0.98] | 129.68 ms [**1.72**] / 66.1MB [**5.15**] | 123.20 ms [**1.63**] / 25.8MB [**2.00**] | 388.18 ms [**5.15**] / 5.2MB [**0.41**] |

**PostgreSQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| BulkInsert | 2000 | ADO | **4.64 ms [1.00] / 341KB [1.00]** | 4.86 ms [1.05] / 343KB [1.01] | 16.59 ms [**3.58**] / 6.7MB [**20.10**] | 13.07 ms [**2.82**] / 3.8MB [**11.56**] | 5.36 ms [1.16] / 847KB [**2.48**] |
| BulkUpdate | 2000 | ADO | **5.30 ms [1.00] / 418KB [1.00]** | 5.42 ms [1.02] / 422KB [1.01] | 17.02 ms [**3.21**] / 6.5MB [**16.00**] | 13.62 ms [**2.57**] / 2.7MB [**6.64**] | 50.85 ms [**9.60**] / 3.8MB [**9.28**] |
| BulkDelete | 2000 | 🏆 **PalORM** | 3.49 ms [1.00] / 784KB [1.00] | **2.55 ms [0.73] / 89.6KB [0.11]** | 3.89 ms [1.12] / 1.3MB [**1.71**] | 5.83 ms [**1.67**] / 571KB [0.73] | 18.89 ms [**5.42**] / 19.2MB [**25.04**] |
| 📌UpsertBatch | 2000 | 🏆 **PalORM** | 14.86 ms [1.00] / 2.7MB [1.00] | **7.58 ms [0.51] / 445KB [0.16]** | 19.06 ms [1.28] / 6.7MB [**2.47**] | 15.76 ms [1.06] / 2.7MB [1.01] | 23.38 ms [**1.57**] / 5.8MB [**2.12**] |
| BulkInsert | 20000 | 🏆 **PalORM** | 19.89 ms [1.00] / 3.3MB [1.00] | **19.51 ms [0.98] / 3.3MB [1.00]** | 191.88 ms [**9.65**] / 66.9MB [**20.54**] | 111.82 ms [**5.62**] / 39.6MB [**12.15**] | 22.55 ms [1.13] / 8.0MB [**2.46**] |
| BulkUpdate | 20000 | 🏆 **PalORM** | 66.36 ms [1.00] / 4.1MB [1.00] | **64.51 ms [0.97] / 4.1MB [1.00]** | 221.68 ms [**3.34**] / 65.3MB [**16.02**] | 123.42 ms [**1.86**] / 28.3MB [**6.93**] | 522.83 ms [**7.88**] / 37.9MB [**9.29**] |
| 📌BulkDelete | 20000 | 🏆 **PalORM** | 25.09 ms [1.00] / 7.6MB [1.00] | **10.12 ms [0.40] / 812KB [0.10]** | 28.38 ms [1.13] / 13.0MB [**1.71**] | 61.07 ms [**2.43**] / 6.0MB [0.78] | 177.21 ms [**7.06**] / 192.4MB [**25.29**] |
| 📌UpsertBatch | 20000 | 🏆 **PalORM** | 136.81 ms [1.00] / 11.6MB [1.00] | **84.27 ms [0.62] / 4.3MB [0.37]** | 215.54 ms [**1.58**] / 66.9MB [**5.79**] | 153.72 ms [1.12] / 28.4MB [**2.45**] | 243.87 ms [**1.78**] / 57.3MB [**4.96**] |

### Table 3: Transactions (three dialects, smallest tier only)

> Wins: ADO 8 · PalORM 5 · Dapper 2 · FreeSql 0 · RepoDb 0

**SQLite**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| TxSingleInsert | 2000 | ADO | **19.3 µs [1.00] / 3.9KB [1.00]** | 19.6 µs [1.02] / 4.3KB [1.10] | 20.4 µs [1.05] / 4.7KB [1.20] | 34.6 µs [**1.80**] / 7.0KB [**1.78**] | 21.2 µs [1.10] / 4.2KB [1.07] |
| TxHundredInserts | 2000 | 🏆 **PalORM** | 185.1 µs [1.00] / 137KB [1.00] | **183.8 µs [0.99] / 92.7KB [0.68]** | 264.0 µs [1.43] / 220KB [**1.61**] | 1.53 ms [**8.24**] / 541KB [**3.96**] | 558.8 µs [**3.02**] / 295KB [**2.16**] |
| TxBulkInsert | 2000 | ADO | **2.54 ms [1.00] / 1.7MB [1.00]** | 2.61 ms [1.03] / 1.7MB [1.00] | 4.26 ms [**1.68**] / 4.3MB [**2.48**] | 7.72 ms [**3.04**] / 3.5MB [**2.02**] | 15.80 ms [**6.22**] / 2.6MB [**1.54**] |
| TxRollback | 2000 | ADO | **572.8 µs [1.00] / 435KB [1.00]** | 628.1 µs [1.10] / 437KB [1.01] | 2.08 ms [**3.63**] / 1.6MB [**3.74**] | 7.62 ms [**13.31**] / 3.5MB [**8.16**] | 2.65 ms [**4.62**] / 2.0MB [**4.65**] |
| EnumInserts | 2000 | ADO | **130.6 µs [1.00] / 59.0KB [1.00]** | 147.4 µs [1.13] / 59.1KB [1.00] | 349.8 µs [**2.68**] / 223KB [**3.77**] | 1.26 ms [**9.64**] / 497KB [**8.42**] | 469.1 µs [**3.59**] / 231KB [**3.92**] |

**MySQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| TxSingleInsert | 2000 | Dapper | 4.36 ms [1.00] / 21.0KB [1.00] | 4.38 ms [1.00] / 22.2KB [1.06] | **4.29 ms [0.99] / 21.0KB [1.00]** | 4.61 ms [1.06] / 27.2KB [1.29] | 4.37 ms [1.00] / 21.9KB [1.04] |
| TxHundredInserts | 2000 | 🏆 **PalORM** | 25.71 ms [1.00] / 371KB [1.00] | **25.52 ms [0.99] / 304KB [0.82]** | 25.86 ms [1.01] / 351KB [0.95] | 29.78 ms [1.16] / 814KB [**2.19**] | 28.21 ms [1.10] / 617KB [**1.66**] |
| TxBulkInsert | 2000 | 🏆 **PalORM** | 11.72 ms [1.00] / 486KB [1.00] | **11.28 ms [0.96] / 491KB [1.01]** | 21.07 ms [**1.80**] / 6.7MB [**14.02**] | 20.27 ms [**1.73**] / 3.4MB [**7.20**] | 11.58 ms [0.99] / 489KB [1.01] |
| TxRollback | 2000 | ADO | **94.47 ms [1.00] / 1.4MB [1.00]** | 95.38 ms [1.01] / 1.4MB [1.02] | 99.73 ms [1.06] / 2.4MB [**1.78**] | 117.04 ms [1.24] / 4.9MB [**3.61**] | 107.77 ms [1.14] / 3.6MB [**2.65**] |
| EnumInserts | 2000 | 🏆 **PalORM** | 25.33 ms [1.00] / 276KB [1.00] | **24.13 ms [0.95] / 282KB [1.02]** | 25.91 ms [1.02] / 425KB [**1.54**] | 28.31 ms [1.12] / 770KB [**2.79**] | 27.48 ms [1.09] / 524KB [**1.90**] |

**PostgreSQL**

| Operation | Tier | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |
|---|---:|:-:|---|---|---|---|---|
| TxSingleInsert | 2000 | 🏆 **PalORM** | 1.50 ms [1.00] / 8.4KB [1.00] | **1.48 ms [0.99] / 9.2KB [1.09]** | 1.52 ms [1.02] / 8.1KB [0.96] | 1.61 ms [1.07] / 12.3KB [1.46] | 1.52 ms [1.02] / 8.7KB [1.03] |
| TxHundredInserts | 2000 | Dapper | 20.67 ms [1.00] / 313KB [1.00] | 20.60 ms [1.00] / 212KB [0.68] | **20.49 ms [0.99] / 282KB [0.90]** | 23.51 ms [1.14] / 702KB [**2.24**] | 22.38 ms [1.08] / 433KB [1.38] |
| TxBulkInsert | 2000 | ADO | **4.83 ms [1.00] / 340KB [1.00]** | 4.84 ms [1.00] / 342KB [1.01] | 15.92 ms [**3.29**] / 6.7MB [**20.15**] | 21.39 ms [**4.43**] / 3.4MB [**10.27**] | 5.30 ms [1.10] / 844KB [**2.48**] |
| TxRollback | 2000 | ADO | **101.99 ms [1.00] / 929KB [1.00]** | 103.57 ms [1.02] / 1.0MB [1.11] | 106.33 ms [1.04] / 1.7MB [**1.89**] | 124.20 ms [1.22] / 4.4MB [**4.87**] | 112.01 ms [1.10] / 2.9MB [**3.19**] |
| EnumInserts | 2000 | ADO | **20.16 ms [1.00] / 164KB [1.00]** | 20.24 ms [1.00] / 184KB [1.13] | 20.73 ms [1.03] / 281KB [**1.72**] | 23.61 ms [1.17] / 661KB [**4.03**] | 21.66 ms [1.07] / 352KB [**2.15**] |

### Table 4: Cross-dialect PalORM/ADO ratio (bulk family + GetAllAsync; raw ratio, 📌 = outside the line)

| Operation | Tier | SQLite | PostgreSQL | MySQL |
|---|---:|---:|---:|---:|
| BulkInsert | 2000 | 1.01 | 1.05 | 1.01 |
| BulkInsert | 20000 | 0.99 | 0.98 | 1.02 |
| BulkUpdate | 2000 | 1.02 | 1.02 | 0.94 |
| BulkUpdate | 20000 | 0.99 | 0.97 | 1.03 |
| BulkDelete | 2000 | 1.00 | 0.73 | 0.96 |
| BulkDelete | 20000 | 0.96 | 📌0.40 | 0.90 |
| UpsertBatch | 2000 | 1.03 | 📌0.51 | 0.99 |
| UpsertBatch | 20000 | 1.04 | 📌0.62 | 1.04 |
| GetAllAsync | 2000 | 0.98 | 1.01 | 0.99 |
| GetAllAsync | 20000 | 0.99 | 0.98 | 1.00 |

## 🚀 Quick Start

> `.NET 11` (every package targets `net11.0` only) + SDK `11.0.100-rc.2` or later; the IDE must support Roslyn source generators. Native AOT publish additionally needs about 4 GB of memory (ILC compile time, not runtime).

```xml
<PackageReference Include="PalORM.PostgreSql" Version="6.8.0" />   <!-- or PalORM.MySql / PalORM.Sqlite -->
```

The package brings `PalORM.Core` and `PalORM.SourceGen`; five steps below, full patterns and decision tables in [Best practices](docs/最佳实践.md).

### Define an entity

```csharp
using PalORM;

[Table("users")]
public partial class User
{
    [Key] public long Id { get; set; }
    [Column("email")] public string Email { get; set; } = "";
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}
```

> A non-nullable reference property reading a NULL column throws `SqlNullValueException` (fail loudly, never return null); declare nullable (`string?`) for columns that may hold NULL.

### Create a session

```csharp
using var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = "Host=localhost;Username=user;Password=xxx;Database=mydb"
});
```

Reuse one `db` per scope (the third same-shape operation promotes a command reuse slot); `PreWarmAsync(options, count: 10)` warms the pool at startup.

### Migrate and seed

```csharp
await db.MigrateAsync();                                                                    // three-dialect DDL, idempotent; FK order
await db.SeedAsync([new User { Id = 1, Email = "admin@example.com", CreatedAt = DateTime.UtcNow }]);   // idempotent upsert
List<string> missing = await db.ValidateSchemaAsync<User>();                                // diff the database against the entity
```

### CRUD

```csharp
var user  = await db.InsertAsync(new User { Email = "alice@example.com", CreatedAt = DateTime.UtcNow });  // identity backfilled
var alice = await db.GetAsync<User>(user.Id);                                                             // single-key lookup
var all   = await db.From<User>().Where($"email LIKE {"%@example.com%"}").ToListAsync();

alice.Email = "new@example.com";
await db.UpdateAsync(alice);            // optimistic-lock entities verify the version; conflicts throw ConcurrencyConflictException
await db.SaveAsync(alice);              // default key inserts, non-default key does a single-row UPSERT
await db.DeleteAsync<User>(alice.Id);   // soft-delete entities become an UPDATE
```

**Use `GetAsync` for single-key lookups**: ~880 B per operation less and 25~30% faster than the equivalent `From<T>().Where(Id==x).FirstOrDefaultAsync()`.

### Bulk operations

```csharp
await db.BulkInsertAsync(users);          // PG Binary COPY / MySQL BulkCopy or multi-value INSERT / SQLite row-prepared
await db.BulkUpdateAsync(users);          // optimistic-lock entities: versioned single statement (falls back to DbBatch)
await db.BulkUpdateBatchAsync(users);     // single multi-row SET (PG FROM VALUES / MySQL JOIN VALUES ROW)
await db.BulkDeleteAsync<User>(keyList);  // PG = ANY / SQLite json_each / MySQL batched IN
await db.BulkMergeAsync(users);           // multi-row UPSERT
```

Default-key new rows go row-by-row INSERT to preserve ID backfill; `[ConcurrencyCheck]` entities stay row-by-row.

## 🔧 Configuration

> Start from the three presets (Development / Production / Testing); override any of the 24 settings as needed, connection-string auto-tuning only fills driver defaults. Full list: [configuration reference](docs/配置参考.md) (Chinese).

```csharp
var dev  = DbOptions.Development(connectionString);            // development
var prod = DbOptions.Production(connectionString, readConn);  // production (pooling + read/write splitting)
var test = DbOptions.Testing(connectionString);               // testing (zero retries + short timeouts)
var env  = DbOptions.FromEnvironment("PALORM_CONNECTION");    // environment variables (Docker/K8s friendly)
```

`FromEnvironment` reads `PALORM_*` variables (`PALORM_CONNECTION` required) and validates them at startup; full list in the [configuration reference](docs/配置参考.md) (Chinese).

## Database compatibility

| Database | Verified | Driver | Floor |
| --- | --- | --- | --- |
| PostgreSQL | **18.6** (CI `postgres:18`) | Npgsql 10.0.3 | none; bulk writes need 9.3+, JSONB path queries 9.4+, advisory locks 9.5+ |
| MySQL | **8.4.11** (CI `mysql:8.4`) | MySqlConnector 2.6.2 | **8.0.19** (`UPDATE JOIN VALUES ROW`; older falls back to CASE WHEN); below 8.0 unsupported |
| SQLite | **3.54.0** (SQLite3MC 2.6.1) | Microsoft.Data.Sqlite.Core 11.0.0-rc.2 | **3.35+** (`INSERT ... RETURNING`; bulk delete `json_each` needs 3.38+) |

> Verified versions are what CI runs; floors are syntax limits visible in the source. Build and benchmark environment: see [CONTRIBUTING.md](CONTRIBUTING.md).

## 🤝 Contributing

Issues and PRs are welcome; keep zero warnings (`TreatWarningsAsErrors`) and attach before/after benchmarks for performance changes. Workflow: [CONTRIBUTING.md](CONTRIBUTING.md).

## 💬 Community

<div align="center">

<img src="docs/qq-group.jpg" width="260" alt="QQ group QR code: C#/.NET New Tech Exchange Group (group no. 1125599744)">

**C#/.NET New Tech Exchange Group** (group no. **1125599744**)

</div>

## 📄 License

[Apache-2.0](LICENSE)

Released versions up to and including v6.7.0 remain under AGPL-3.0-only;<br>
versions from v6.8.0 onward are uniformly released under Apache-2.0.
