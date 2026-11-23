<div align="center">

# PalORM

**A .NET 11 micro-ORM for Native AOT: everything generated at compile time, zero reflection at runtime**

*An AOT-native, compile-time-verifiable micro-ORM with SQL you control*

[![.NET](https://img.shields.io/badge/.NET-11.0.0-512BD4)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/PalORM.Core)](https://www.nuget.org/packages/PalORM.Core)
[![CI](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml/badge.svg)](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

[![Native AOT](https://img.shields.io/badge/Native_AOT-3_dialects_verified-512BD4)](docs/AOT部署指南.md)
[![Diagnostics](https://img.shields.io/badge/Compile--time_diagnostics-46_rules-512BD4)](docs/API参考.md)
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
- **Compile-time safety net**: 46 Roslyn diagnostics catch missing keys, nullable tenant columns bypassing isolation, zero-value optimistic-lock baselines, and loop N+1 queries at build time, with errors jumping to the declaration site.
- **Full-pipeline Native AOT**: Publish verification and native execution pass on SQLite, PostgreSQL, and MySQL, and consumers need no TrimmerRootAssembly.
- **Floor-level performance**: Single-row CRUD matches hand-written ADO.NET, bulk writes automatically take the fastest per-dialect path (PG Binary COPY, MySQL LOAD DATA, SQLite row-prepared batches and json_each bulk delete), and a session pool is built in for request-per-session web services.
- **Enterprise cross-cutting**: Multi-tenant column isolation, optimistic locking, soft delete, audit interception, read/write splitting, advisory locks, resilient retry, and circuit breaking work out of the box.
- **SQL assets managed at compile time**: `[SqlFile]` embeds and validates .sql files, `[SqlTemplate]` turns interpolated SQL into constants, and AutoTagging injects "file:line member" locating comments automatically.
- **Operations and observability**: Health checks, a cross-dialect unified exception family, and OpenTelemetry tracing and metrics are built in.
- **Dialect-specific capabilities**: PostgreSQL gets UNNEST, advisory locks, LISTEN/NOTIFY, pgvector, and JSONB path queries; MySQL gets local_infile degradation and UPDATE JOIN; SQLite gets driver-level AES-256 encryption at rest and automatic PRAGMA tuning.
- **Schema and seeding**: `MigrateAsync` runs three-dialect DDL in foreign-key topological order, `SeedAsync` seeds idempotently inside a transaction, and `ValidateSchemaAsync` reports missing columns against entity declarations.
- **Zero-dependency core**: PalORM.Core references no third-party NuGet packages, only BCL and ADO.NET abstractions.

> **Audit boundary**: `IQueryInterceptor` covers SELECT, QueryBuilder UPDATE, `ExecuteAsync`, and `QueryPipeline`. Entity Insert/Update/Delete/Save, the Bulk family, stored procedures, and multi-result sets bypass interceptors. For write auditing, use database-level auditing or OpenTelemetry.

## 🆚 Comparison with mainstream ORMs

> Versions (verified 2026-11-22): PalORM 6.9.0 · Dapper 2.1.100 + Dapper.AOT 1.1.0 · EF Core 10.0.14 · RepoDb 1.17.0.

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
| **Breadth & dependencies** | three dialects deeply tuned; zero third-party core deps; single target `net11.0`; Apache-2.0 | any ADO.NET; multi-target (netstandard2.0 / net8.0 / net10.0 / net461); Apache-2.0 | provider matrix; many packages, heavy deps; `net10.0`; MIT | 22 provider packages; core multi-target; Apache-2.0 |

### AOT implementation approaches (PalORM / Dapper.AOT / RepoDb / EF Core / FreeSql)

What matters for AOT is how each library replaces runtime reflection: **compile-time source generation** (PalORM, Dapper.AOT), **expression interpretation + trimming annotations** (RepoDb), **runtime building with optional pre-generation** (EF Core), **runtime reflection with no adaptation** (FreeSql).

| Dimension&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp; | PalORM 6.9.0                                                                                                                                           | Dapper.AOT 1.1.0                                                                                                                                                                                                                                                             | RepoDb 1.17.0&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;                                         | EF Core 10.0.14&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;                                                                       | FreeSql 3.5.311                                                                                                                                                                              |
|-----------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Implementation approach** | Compile-time source generation, zero runtime reflection | Source generation + interceptors replacing call sites | Runtime expression trees, interpreted under AOT | Runtime expression-tree compilation + model building | Runtime reflection, no AOT adaptation |
| **AOT support level** | ✅ **Full**: three-dialect publish passes, zero IL warnings, 4 CI gates | 🟦 **High**: core APIs generated, explicit opt-in | 🟨 **Medium**: regular entities and LINQ work | 🟧 **Low**: Compiled Model + precompiled queries, experimental | 🟥 **None**: zero annotations, no AOT CI, 7 of 9 default-publish projects fail |
| **Defects & failure modes** | Unsupported shapes rejected at compile time, no runtime failure surface | Uncovered APIs fall back to reflection (unsafe); Contrib and non-generic `typeof` unsupported; misconfigured interceptors silently skip | dynamic / Expando annotated unsafe; SQLite smoke only; interpreted execution slower | Precompiled queries and model must be hand-written and maintained; capability is a JIT subset | Needs TrimmerRootAssembly roots; 212 IL warnings with analyzers on; crashes at runtime (trimmed constructors) |
| **Performance under AOT** | Equivalent to JIT | Bypasses reflection; officially AOT-safe | Per-query overhead above JIT | Depends on precompilation, no commitment | Interpreted degradation + one-time metadata build |
| **Integration constraints** | `[Table]` / `[Projection]` annotations + public parameterless ctor; generator ships with the package | Zero rewrite; explicit opt-in; declarative type handlers | Works out of the box; avoid dynamic APIs under AOT | Precompiled queries + Compiled Model required | TrimmerRootAssembly + per-feature native smoke tests |
| **Escape hatch & boundaries** | `FormattableString` / `[SqlFile]` are the AOT-safe outlets; no dynamic entities, no full LINQ translation | Uncovered calls must be rewritten; minor behavioral differences allowed | No safe outlet; wide API surface under JIT, 22 providers | No escape-hatch promise; most complete JIT feature set | No escape hatch; broadest surface, AOT is the user's responsibility |

> Scale (under AOT): ✅ Full · 🟦 High (with defects) · 🟨 Medium · 🟧 Low · 🟥 None

## 📊 Performance

> **Test environment**: AMD Ryzen 9 8945HX · Windows 10 22H2 · .NET 11 GA · SQLite temp-file database (WAL + 64 MB cache + mmap) · PostgreSQL 18.6 / MySQL 8.4.11 local containers.
>
> **Data source**: 9 groups measured in the same run, 2026-11-22, batch `crossbench-20261122-214255.json` (the first complete batch after the GA dependency upgrade: Npgsql 11 / MySqlConnector 3.0). The groups are PalORM / Dapper / Dapper.AOT / linq2db / EF Core / SqlSugar / FreeSql / RepoDb plus hand-written ADO.NET SQL as the baseline, each written in its ecosystem idiomatic form. Full report: [性能全量报告-2026-11-22.md](docs/性能全量报告-2026-11-22.md).

### Overview

9 groups × three dialects × two tiers (2000 / 20000) × 17 operations (transactions on the smallest tier only) = **87 cells**, each operation averaged over two reverse-order passes. All ratios use hand-written ADO.NET SQL from the same batch as the 1.00 baseline; below 1 means faster.

| Group | Wins /87 | Median latency ratio | Median memory ratio |
|---|---:|---:|---:|
| ADO | — | 1.00 | 1.00 |
| PalORM | 32 | 1.00 | 1.00 |
| Dapper.AOT | 8 | 1.06 | 1.15 |
| RepoDb | 1 | 1.16 | 1.52 |
| EF Core | 8 | 3.14 | 8.77 |
| Dapper | 1 | 1.10 | 1.69 |
| FreeSql | 1 | 1.44 | 1.99 |
| linq2db | 1 | 1.41 | 2.89 |
| SqlSugar | 1 | 2.06 | 3.52 |

- **PalORM**: wins 32/87 (37%), median latency ratio **1.00** (full range 0.48 ~ 1.14), median memory ratio **1.00**, no cell at 1.5x or worse.
- **Largest outliers**: SqlSugar UpsertBatch (SQLite 110.4x, 101.3x) and EF Core single-row Insert (62.5 ~ 63.3x, the per-operation DbContext shape).

### Operation comparison (latency [ratio] and memory [relative to baseline]; colors follow the six percentage bands via MathJax)

#### SQLite (2000 and 20000)

| Operation | Tier | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 7.8&nbsp;µs<br>1.00<br>2.1&nbsp;KB<br>1.00 | 🏆&nbsp;6.3&nbsp;µs<br>$\color{#65a30d}{0.80}$<br>2.2&nbsp;KB<br>$\color{#d97706}{1.03}$ | 💾&nbsp;7.0&nbsp;µs<br>0.90<br>2.0&nbsp;KB<br>0.97 | 16.6&nbsp;µs<br>**2.13**<br>4.5&nbsp;KB<br>**2.13** | 142.1&nbsp;µs<br>**18.22**<br>49.2&nbsp;KB<br>**23.29** | 10.6&nbsp;µs<br>1.36<br>2.8&nbsp;KB<br>1.34 | 37.1&nbsp;µs<br>**4.76**<br>10.2&nbsp;KB<br>**4.84** | 23.8&nbsp;µs<br>**3.04**<br>6.4&nbsp;KB<br>**3.03** | 84.2&nbsp;µs<br>**10.79**<br>30.0&nbsp;KB<br>**14.21** |
| GetByKey | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 6.6&nbsp;µs<br>1.00<br>2.1&nbsp;KB<br>1.00 | 🏆&nbsp;6.5&nbsp;µs<br>$\color{#2563eb}{0.99}$<br>2.2&nbsp;KB<br>$\color{#d97706}{1.03}$ | 💾&nbsp;7.3&nbsp;µs<br>1.10<br>2.1&nbsp;KB<br>0.97 | 9.3&nbsp;µs<br>1.40<br>4.4&nbsp;KB<br>**2.07** | 82.3&nbsp;µs<br>**12.48**<br>48.9&nbsp;KB<br>**23.13** | 7.7&nbsp;µs<br>1.17<br>2.8&nbsp;KB<br>1.33 | 33.0&nbsp;µs<br>**4.99**<br>10.4&nbsp;KB<br>**4.92** | 17.1&nbsp;µs<br>**2.59**<br>6.4&nbsp;KB<br>**3.02** | 76.5&nbsp;µs<br>**11.58**<br>29.8&nbsp;KB<br>**14.11** |
| GetAllAsync | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 1.04&nbsp;ms<br>1.00<br>309&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;1.03&nbsp;ms<br>$\color{#2563eb}{0.99}$<br>292&nbsp;KB<br>$\color{#2563eb}{0.95}$ | 1.21&nbsp;ms<br>1.16<br>356&nbsp;KB<br>1.15 | 1.24&nbsp;ms<br>1.20<br>372&nbsp;KB<br>1.21 | 1.48&nbsp;ms<br>1.42<br>728&nbsp;KB<br>**2.36** | 1.48&nbsp;ms<br>1.42<br>513&nbsp;KB<br>1.66 | 1.48&nbsp;ms<br>1.42<br>314&nbsp;KB<br>1.02 | 1.45&nbsp;ms<br>1.39<br>311&nbsp;KB<br>1.01 | 3.42&nbsp;ms<br>**3.29**<br>580&nbsp;KB<br>1.88 |
| GetAllAsync | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 10.45&nbsp;ms<br>1.00<br>3343&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;10.29&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>2984&nbsp;KB<br>$\color{#65a30d}{0.89}$ | 12.17&nbsp;ms<br>1.16<br>3814&nbsp;KB<br>1.14 | 12.56&nbsp;ms<br>1.20<br>3972&nbsp;KB<br>1.19 | 14.38&nbsp;ms<br>1.38<br>7146&nbsp;KB<br>**2.14** | 14.88&nbsp;ms<br>1.42<br>5385&nbsp;KB<br>1.61 | 14.75&nbsp;ms<br>1.41<br>3356&nbsp;KB<br>1.00 | 14.33&nbsp;ms<br>1.37<br>3353&nbsp;KB<br>1.00 | 33.93&nbsp;ms<br>**3.25**<br>5920&nbsp;KB<br>1.77 |
| QueryAll | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 1.02&nbsp;ms<br>1.00<br>308&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;1.02&nbsp;ms<br>$\color{#2563eb}{1.00}$<br>293&nbsp;KB<br>$\color{#2563eb}{0.95}$ | 1.21&nbsp;ms<br>1.18<br>356&nbsp;KB<br>1.15 | 1.23&nbsp;ms<br>1.21<br>372&nbsp;KB<br>1.21 | 1.43&nbsp;ms<br>1.40<br>728&nbsp;KB<br>**2.36** | 1.46&nbsp;ms<br>1.43<br>513&nbsp;KB<br>1.66 | 1.47&nbsp;ms<br>1.44<br>314&nbsp;KB<br>1.02 | 1.44&nbsp;ms<br>1.41<br>311&nbsp;KB<br>1.01 | 3.40&nbsp;ms<br>**3.33**<br>580&nbsp;KB<br>1.88 |
| QueryAll | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 10.96&nbsp;ms<br>1.00<br>3344&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;10.33&nbsp;ms<br>$\color{#2563eb}{0.94}$<br>2984&nbsp;KB<br>$\color{#65a30d}{0.89}$ | 12.24&nbsp;ms<br>1.12<br>3814&nbsp;KB<br>1.14 | 12.37&nbsp;ms<br>1.13<br>3972&nbsp;KB<br>1.19 | 14.67&nbsp;ms<br>1.34<br>7146&nbsp;KB<br>**2.14** | 14.85&nbsp;ms<br>1.36<br>5385&nbsp;KB<br>1.61 | 14.91&nbsp;ms<br>1.36<br>3357&nbsp;KB<br>1.00 | 14.75&nbsp;ms<br>1.35<br>3354&nbsp;KB<br>1.00 | 33.64&nbsp;ms<br>**3.07**<br>5921&nbsp;KB<br>1.77 |
| Insert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 18.9&nbsp;µs<br>1.00<br>2.8&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;18.4&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>2.7&nbsp;KB<br>$\color{#2563eb}{0.98}$ | 18.5&nbsp;µs<br>0.98<br>2.9&nbsp;KB<br>1.04 | 20.3&nbsp;µs<br>1.07<br>3.0&nbsp;KB<br>1.09 | 1.18&nbsp;ms<br>**62.51**<br>55.3&nbsp;KB<br>**19.89** | 19.9&nbsp;µs<br>1.05<br>3.6&nbsp;KB<br>1.30 | 31.1&nbsp;µs<br>1.65<br>5.8&nbsp;KB<br>**2.07** | 1.12&nbsp;ms<br>**59.29**<br>19.4&nbsp;KB<br>**6.98** | 1.13&nbsp;ms<br>**59.71**<br>15.0&nbsp;KB<br>**5.40** |
| Insert | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 18.7&nbsp;µs<br>1.00<br>2.9&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;17.9&nbsp;µs<br>$\color{#2563eb}{0.96}$<br>2.7&nbsp;KB<br>$\color{#2563eb}{0.95}$ | 18.2&nbsp;µs<br>0.97<br>2.8&nbsp;KB<br>0.99 | 20.6&nbsp;µs<br>1.10<br>3.0&nbsp;KB<br>1.05 | 1.18&nbsp;ms<br>**63.32**<br>55.6&nbsp;KB<br>**19.41** | 19.8&nbsp;µs<br>1.06<br>3.6&nbsp;KB<br>1.27 | 31.6&nbsp;µs<br>1.69<br>5.7&nbsp;KB<br>1.99 | 1.09&nbsp;ms<br>**58.20**<br>19.5&nbsp;KB<br>**6.79** | 1.15&nbsp;ms<br>**61.48**<br>15.8&nbsp;KB<br>**5.50** |
| Update | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;7.6&nbsp;µs<br>1.00<br>2.5&nbsp;KB<br>1.00 | 7.8&nbsp;µs<br>$\color{#d97706}{1.02}$<br>2.9&nbsp;KB<br>$\color{#ea580c}{1.16}$ | 8.1&nbsp;µs<br>1.06<br>2.8&nbsp;KB<br>1.11 | 9.6&nbsp;µs<br>1.26<br>4.1&nbsp;KB<br>1.62 | 85.6&nbsp;µs<br>**11.26**<br>52.5&nbsp;KB<br>**20.85** | 8.6&nbsp;µs<br>1.13<br>3.3&nbsp;KB<br>1.30 | 21.0&nbsp;µs<br>**2.76**<br>7.4&nbsp;KB<br>**2.94** | 14.4&nbsp;µs<br>1.90<br>19.7&nbsp;KB<br>**7.84** | 35.6&nbsp;µs<br>**4.69**<br>14.6&nbsp;KB<br>**5.78** |
| Update | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;8.7&nbsp;µs<br>1.00<br>2.5&nbsp;KB<br>1.00 | 🏆&nbsp;8.0&nbsp;µs<br>$\color{#2563eb}{0.91}$<br>2.9&nbsp;KB<br>$\color{#ea580c}{1.16}$ | 8.4&nbsp;µs<br>0.97<br>2.8&nbsp;KB<br>1.11 | 9.8&nbsp;µs<br>1.12<br>4.1&nbsp;KB<br>1.62 | 93.0&nbsp;µs<br>**10.68**<br>52.6&nbsp;KB<br>**20.82** | 8.6&nbsp;µs<br>0.98<br>3.3&nbsp;KB<br>1.30 | 23.9&nbsp;µs<br>**2.75**<br>7.4&nbsp;KB<br>**2.94** | 17.6&nbsp;µs<br>**2.02**<br>19.8&nbsp;KB<br>**7.83** | 38.0&nbsp;µs<br>**4.37**<br>14.6&nbsp;KB<br>**5.77** |
| BulkInsert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;linq2db | 2.74&nbsp;ms<br>1.00<br>1758&nbsp;KB<br>1.00 | 🏆&nbsp;2.68&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>1758&nbsp;KB<br>$\color{#2563eb}{1.00}$ | 4.52&nbsp;ms<br>1.65<br>4353&nbsp;KB<br>**2.48** | 18.11&nbsp;ms<br>**6.61**<br>2709&nbsp;KB<br>1.54 | 23.42&nbsp;ms<br>**8.55**<br>17.4&nbsp;MB<br>**10.12** | 4.54&nbsp;ms<br>1.66<br>4354&nbsp;KB<br>**2.48** | 10.78&nbsp;ms<br>**3.94**<br>6647&nbsp;KB<br>**3.78** | 💾&nbsp;5.05&nbsp;ms<br>1.84<br>1278&nbsp;KB<br>0.73 | 7.56&nbsp;ms<br>**2.76**<br>4509&nbsp;KB<br>**2.57** |
| BulkInsert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;linq2db | 🏆&nbsp;29.16&nbsp;ms<br>1.00<br>17.2&nbsp;MB<br>1.00 | 29.55&nbsp;ms<br>$\color{#d97706}{1.01}$<br>17.2&nbsp;MB<br>$\color{#2563eb}{1.00}$ | 48.28&nbsp;ms<br>1.66<br>42.5&nbsp;MB<br>**2.48** | 172.80&nbsp;ms<br>**5.92**<br>26.1&nbsp;MB<br>1.52 | 255.50&nbsp;ms<br>**8.76**<br>173.3&nbsp;MB<br>**10.10** | 50.07&nbsp;ms<br>1.72<br>42.5&nbsp;MB<br>**2.48** | 118.80&nbsp;ms<br>**4.07**<br>66.1&nbsp;MB<br>**3.85** | 💾&nbsp;53.80&nbsp;ms<br>1.84<br>10.9&nbsp;MB<br>0.63 | 82.14&nbsp;ms<br>**2.82**<br>44.9&nbsp;MB<br>**2.62** |
| BulkUpdate | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;2.44&nbsp;ms<br>1.00<br>1735&nbsp;KB<br>1.00 | 2.46&nbsp;ms<br>$\color{#d97706}{1.01}$<br>1736&nbsp;KB<br>$\color{#d97706}{1.00}$ | 8.95&nbsp;ms<br>**3.67**<br>5642&nbsp;KB<br>**3.25** | 7.33&nbsp;ms<br>**3.00**<br>5308&nbsp;KB<br>**3.06** | 27.95&nbsp;ms<br>**11.45**<br>18.0&nbsp;MB<br>**10.62** | 9.24&nbsp;ms<br>**3.79**<br>6502&nbsp;KB<br>**3.75** | 14.00&nbsp;ms<br>**5.74**<br>7202&nbsp;KB<br>**4.15** | 23.39&nbsp;ms<br>**9.58**<br>38.5&nbsp;MB<br>**22.74** | 5.96&nbsp;ms<br>**2.44**<br>4503&nbsp;KB<br>**2.60** |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;27.03&nbsp;ms<br>1.00<br>17.0&nbsp;MB<br>1.00 | 27.42&nbsp;ms<br>$\color{#d97706}{1.01}$<br>17.0&nbsp;MB<br>$\color{#d97706}{1.00}$ | 95.63&nbsp;ms<br>**3.54**<br>55.2&nbsp;MB<br>**3.25** | 77.02&nbsp;ms<br>**2.85**<br>51.9&nbsp;MB<br>**3.05** | 337.24&nbsp;ms<br>**12.47**<br>179.5&nbsp;MB<br>**10.56** | 100.37&nbsp;ms<br>**3.71**<br>63.6&nbsp;MB<br>**3.74** | 146.32&nbsp;ms<br>**5.41**<br>73.4&nbsp;MB<br>**4.32** | 232.00&nbsp;ms<br>**8.58**<br>385.3&nbsp;MB<br>**22.67** | 75.08&nbsp;ms<br>**2.78**<br>44.8&nbsp;MB<br>**2.63** |
| BulkDelete | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;832.8&nbsp;µs<br>1.00<br>197&nbsp;KB<br>1.00 | 🏆&nbsp;831.0&nbsp;µs<br>$\color{#2563eb}{1.00}$<br>198&nbsp;KB<br>$\color{#d97706}{1.01}$ | 23.73&nbsp;ms<br>**28.49**<br>1605&nbsp;KB<br>**8.14** | 21.43&nbsp;ms<br>**25.73**<br>3207&nbsp;KB<br>**16.27** | 11.66&nbsp;ms<br>**14.00**<br>2520&nbsp;KB<br>**12.78** | 23.69&nbsp;ms<br>**28.45**<br>1605&nbsp;KB<br>**8.14** | 1.61&nbsp;ms<br>1.93<br>569&nbsp;KB<br>**2.89** | 2.55&nbsp;ms<br>**3.06**<br>271&nbsp;KB<br>1.37 | 2.43&nbsp;ms<br>**2.92**<br>353&nbsp;KB<br>1.79 |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;9.25&nbsp;ms<br>1.00<br>2077&nbsp;KB<br>1.00 | 🏆&nbsp;9.03&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>2078&nbsp;KB<br>$\color{#d97706}{1.00}$ | 235.14&nbsp;ms<br>**25.42**<br>15.6&nbsp;MB<br>**7.71** | 213.97&nbsp;ms<br>**23.13**<br>31.0&nbsp;MB<br>**15.27** | 122.99&nbsp;ms<br>**13.30**<br>24.6&nbsp;MB<br>**12.13** | 238.29&nbsp;ms<br>**25.76**<br>15.7&nbsp;MB<br>**7.72** | 20.51&nbsp;ms<br>**2.22**<br>6101&nbsp;KB<br>**2.94** | 16.93&nbsp;ms<br>1.83<br>2790&nbsp;KB<br>1.34 | 37.29&nbsp;ms<br>**4.03**<br>4341&nbsp;KB<br>**2.09** |
| UpsertBatch | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;2.52&nbsp;ms<br>1.00<br>1736&nbsp;KB<br>1.00 | 2.60&nbsp;ms<br>$\color{#d97706}{1.03}$<br>1753&nbsp;KB<br>$\color{#d97706}{1.01}$ | 114.97&nbsp;ms<br>**45.61**<br>8172&nbsp;KB<br>**4.71** | 19.62&nbsp;ms<br>**7.78**<br>11.6&nbsp;MB<br>**6.85** | 21.01&nbsp;ms<br>**8.34**<br>5506&nbsp;KB<br>**3.17** | 4.38&nbsp;ms<br>1.74<br>4334&nbsp;KB<br>**2.50** | 3.82&nbsp;ms<br>1.52<br>2556&nbsp;KB<br>1.47 | 23.36&nbsp;ms<br>**9.27**<br>42.8&nbsp;MB<br>**25.23** | 278.23&nbsp;ms<br>**110.37**<br>219.9&nbsp;MB<br>**129.73** |
| UpsertBatch | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;27.59&nbsp;ms<br>1.00<br>17.0&nbsp;MB<br>1.00 | 28.72&nbsp;ms<br>$\color{#d97706}{1.04}$<br>17.2&nbsp;MB<br>$\color{#d97706}{1.01}$ | 1.14&nbsp;s<br>**41.29**<br>79.8&nbsp;MB<br>**4.69** | 194.76&nbsp;ms<br>**7.06**<br>116.2&nbsp;MB<br>**6.83** | 1.59&nbsp;s<br>**57.60**<br>57.6&nbsp;MB<br>**3.39** | 47.86&nbsp;ms<br>1.73<br>42.4&nbsp;MB<br>**2.49** | 41.31&nbsp;ms<br>1.50<br>25.8&nbsp;MB<br>1.52 | 231.51&nbsp;ms<br>**8.39**<br>427.7&nbsp;MB<br>**25.16** | 2.80&nbsp;s<br>**101.35**<br>2365.1&nbsp;MB<br>**139.14** |
| KeysetPage | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;62.4&nbsp;µs<br>1.00<br>18.0&nbsp;KB<br>1.00 | 🏆&nbsp;62.3&nbsp;µs<br>$\color{#2563eb}{1.00}$<br>18.3&nbsp;KB<br>$\color{#d97706}{1.02}$ | 96.7&nbsp;µs<br>1.55<br>20.7&nbsp;KB<br>1.15 | 73.4&nbsp;µs<br>1.18<br>25.0&nbsp;KB<br>1.39 | 167.3&nbsp;µs<br>**2.68**<br>86.7&nbsp;KB<br>**4.83** | 85.3&nbsp;µs<br>1.37<br>29.3&nbsp;KB<br>1.63 | 112.8&nbsp;µs<br>1.81<br>28.0&nbsp;KB<br>1.56 | 89.1&nbsp;µs<br>1.43<br>23.3&nbsp;KB<br>1.30 | 248.7&nbsp;µs<br>**3.99**<br>63.2&nbsp;KB<br>**3.52** |
| KeysetPage | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;60.7&nbsp;µs<br>1.00<br>18.0&nbsp;KB<br>1.00 | 61.3&nbsp;µs<br>$\color{#d97706}{1.01}$<br>18.3&nbsp;KB<br>$\color{#d97706}{1.02}$ | 94.7&nbsp;µs<br>1.56<br>20.7&nbsp;KB<br>1.15 | 71.0&nbsp;µs<br>1.17<br>25.0&nbsp;KB<br>1.39 | 166.9&nbsp;µs<br>**2.75**<br>86.7&nbsp;KB<br>**4.83** | 84.0&nbsp;µs<br>1.38<br>29.3&nbsp;KB<br>1.63 | 112.3&nbsp;µs<br>1.85<br>28.0&nbsp;KB<br>1.56 | 89.1&nbsp;µs<br>1.47<br>23.3&nbsp;KB<br>1.30 | 251.5&nbsp;µs<br>**4.14**<br>63.3&nbsp;KB<br>**3.52** |
| WhereIn | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 140.9&nbsp;µs<br>1.00<br>47.4&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;137.9&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>41.7&nbsp;KB<br>$\color{#65a30d}{0.88}$ | 311.0&nbsp;µs<br>**2.21**<br>101&nbsp;KB<br>**2.13** | 275.6&nbsp;µs<br>1.95<br>90.9&nbsp;KB<br>1.92 | 292.3&nbsp;µs<br>**2.07**<br>203&nbsp;KB<br>**4.29** | 263.6&nbsp;µs<br>1.87<br>101&nbsp;KB<br>**2.14** | 175.6&nbsp;µs<br>1.25<br>45.3&nbsp;KB<br>0.96 | 152.2&nbsp;µs<br>1.08<br>42.0&nbsp;KB<br>0.89 | 371.4&nbsp;µs<br>**2.63**<br>108&nbsp;KB<br>**2.29** |
| WhereIn | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 136.4&nbsp;µs<br>1.00<br>48.0&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;133.3&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>42.3&nbsp;KB<br>$\color{#65a30d}{0.88}$ | 260.8&nbsp;µs<br>1.91<br>102&nbsp;KB<br>**2.12** | 272.5&nbsp;µs<br>2.00<br>91.7&nbsp;KB<br>1.91 | 294.4&nbsp;µs<br>**2.16**<br>204&nbsp;KB<br>**4.25** | 262.1&nbsp;µs<br>1.92<br>102&nbsp;KB<br>**2.13** | 172.3&nbsp;µs<br>1.26<br>49.4&nbsp;KB<br>1.03 | 152.8&nbsp;µs<br>1.12<br>43.3&nbsp;KB<br>0.90 | 376.8&nbsp;µs<br>**2.76**<br>111&nbsp;KB<br>**2.31** |
| Count | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;4.7&nbsp;µs<br>1.00<br>1.0&nbsp;KB<br>1.00 | 5.3&nbsp;µs<br>$\color{#ea580c}{1.13}$<br>1.8&nbsp;KB<br>$\color{#dc2626}{1.78}$ | 5.2&nbsp;µs<br>1.10<br>1.0&nbsp;KB<br>1.04 | 5.5&nbsp;µs<br>1.16<br>1.3&nbsp;KB<br>1.33 | 55.9&nbsp;µs<br>**11.89**<br>46.2&nbsp;KB<br>**46.20** | 5.0&nbsp;µs<br>1.06<br>1.1&nbsp;KB<br>1.07 | 35.3&nbsp;µs<br>**7.51**<br>5.8&nbsp;KB<br>**5.85** | 6.7&nbsp;µs<br>1.41<br>2.2&nbsp;KB<br>**2.24** | 64.4&nbsp;µs<br>**13.70**<br>25.5&nbsp;KB<br>**25.51** |
| Count | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;9.8&nbsp;µs<br>1.00<br>1.0&nbsp;KB<br>1.00 | 10.2&nbsp;µs<br>$\color{#d97706}{1.04}$<br>1.8&nbsp;KB<br>$\color{#dc2626}{1.77}$ | 🏆&nbsp;8.7&nbsp;µs<br>0.89<br>1.1&nbsp;KB<br>1.04 | 10.2&nbsp;µs<br>1.04<br>1.3&nbsp;KB<br>1.32 | 65.3&nbsp;µs<br>**6.70**<br>46.2&nbsp;KB<br>**45.52** | 9.7&nbsp;µs<br>0.99<br>1.1&nbsp;KB<br>1.07 | 248.9&nbsp;µs<br>**25.53**<br>6.4&nbsp;KB<br>**6.35** | 8.9&nbsp;µs<br>0.91<br>2.3&nbsp;KB<br>**2.22** | 272.3&nbsp;µs<br>**27.92**<br>26.1&nbsp;KB<br>**25.71** |

#### MySQL (2000 and 20000)

| Operation | Tier | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;209.2&nbsp;µs<br>1.00<br>3.9&nbsp;KB<br>1.00 | 🏆&nbsp;194.6&nbsp;µs<br>$\color{#2563eb}{0.93}$<br>4.3&nbsp;KB<br>$\color{#ea580c}{1.11}$ | 233.2&nbsp;µs<br>1.11<br>4.9&nbsp;KB<br>1.26 | 228.6&nbsp;µs<br>1.09<br>8.1&nbsp;KB<br>**2.07** | 739.8&nbsp;µs<br>**3.54**<br>94.8&nbsp;KB<br>**24.33** | 217.9&nbsp;µs<br>1.04<br>5.5&nbsp;KB<br>1.42 | 434.9&nbsp;µs<br>**2.08**<br>15.8&nbsp;KB<br>**4.04** | 267.4&nbsp;µs<br>1.28<br>25.7&nbsp;KB<br>**6.60** | 494.4&nbsp;µs<br>**2.36**<br>35.5&nbsp;KB<br>**9.11** |
| GetByKey | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;218.1&nbsp;µs<br>1.00<br>3.9&nbsp;KB<br>1.00 | 222.1&nbsp;µs<br>$\color{#d97706}{1.02}$<br>4.4&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 226.7&nbsp;µs<br>1.04<br>4.9&nbsp;KB<br>1.24 | 263.5&nbsp;µs<br>1.21<br>8.1&nbsp;KB<br>**2.08** | 776.3&nbsp;µs<br>**3.56**<br>95.0&nbsp;KB<br>**24.25** | 227.9&nbsp;µs<br>1.05<br>5.5&nbsp;KB<br>1.41 | 463.3&nbsp;µs<br>**2.12**<br>16.0&nbsp;KB<br>**4.08** | 284.6&nbsp;µs<br>1.30<br>25.9&nbsp;KB<br>**6.61** | 513.4&nbsp;µs<br>**2.35**<br>35.6&nbsp;KB<br>**9.10** |
| GetAllAsync | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;979.9&nbsp;µs<br>1.00<br>241&nbsp;KB<br>1.00 | 💾&nbsp;983.1&nbsp;µs<br>$\color{#d97706}{1.00}$<br>226&nbsp;KB<br>$\color{#2563eb}{0.94}$ | 981.7&nbsp;µs<br>1.00<br>243&nbsp;KB<br>1.01 | 1.03&nbsp;ms<br>1.06<br>244&nbsp;KB<br>1.01 | 3.46&nbsp;ms<br>**3.53**<br>4503&nbsp;KB<br>**18.65** | 1.10&nbsp;ms<br>1.12<br>446&nbsp;KB<br>1.85 | 1.29&nbsp;ms<br>1.32<br>249&nbsp;KB<br>1.03 | 2.86&nbsp;ms<br>**2.92**<br>4058&nbsp;KB<br>**16.81** | 1.44&nbsp;ms<br>1.47<br>260&nbsp;KB<br>1.08 |
| GetAllAsync | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 8.21&nbsp;ms<br>1.00<br>2572&nbsp;KB<br>1.00 | 💾&nbsp;8.23&nbsp;ms<br>$\color{#d97706}{1.00}$<br>2217&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 🏆&nbsp;8.18&nbsp;ms<br>1.00<br>2574&nbsp;KB<br>1.00 | 8.36&nbsp;ms<br>1.02<br>2576&nbsp;KB<br>1.00 | 35.79&nbsp;ms<br>**4.36**<br>46.3&nbsp;MB<br>**18.42** | 10.21&nbsp;ms<br>1.24<br>4609&nbsp;KB<br>1.79 | 9.21&nbsp;ms<br>1.12<br>2582&nbsp;KB<br>1.00 | 33.52&nbsp;ms<br>**4.08**<br>43.2&nbsp;MB<br>**17.21** | 10.78&nbsp;ms<br>1.31<br>2596&nbsp;KB<br>1.01 |
| QueryAll | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 997.3&nbsp;µs<br>1.00<br>242&nbsp;KB<br>1.00 | 💾&nbsp;978.1&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>227&nbsp;KB<br>$\color{#2563eb}{0.94}$ | 🏆&nbsp;975.5&nbsp;µs<br>0.98<br>243&nbsp;KB<br>1.01 | 1.01&nbsp;ms<br>1.01<br>244&nbsp;KB<br>1.01 | 3.57&nbsp;ms<br>**3.58**<br>4503&nbsp;KB<br>**18.64** | 1.08&nbsp;ms<br>1.08<br>446&nbsp;KB<br>1.85 | 1.27&nbsp;ms<br>1.27<br>249&nbsp;KB<br>1.03 | 2.88&nbsp;ms<br>**2.89**<br>4058&nbsp;KB<br>**16.80** | 1.43&nbsp;ms<br>1.43<br>260&nbsp;KB<br>1.08 |
| QueryAll | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 8.31&nbsp;ms<br>1.00<br>2573&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;8.12&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>2219&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 8.17&nbsp;ms<br>0.98<br>2574&nbsp;KB<br>1.00 | 8.20&nbsp;ms<br>0.99<br>2576&nbsp;KB<br>1.00 | 34.70&nbsp;ms<br>**4.18**<br>46.3&nbsp;MB<br>**18.42** | 9.02&nbsp;ms<br>1.09<br>4607&nbsp;KB<br>1.79 | 9.06&nbsp;ms<br>1.09<br>2582&nbsp;KB<br>1.00 | 31.64&nbsp;ms<br>**3.81**<br>43.2&nbsp;MB<br>**17.20** | 10.77&nbsp;ms<br>1.30<br>2596&nbsp;KB<br>1.01 |
| Insert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;3.85&nbsp;ms<br>1.00<br>17.2&nbsp;KB<br>1.00 | 3.90&nbsp;ms<br>$\color{#d97706}{1.01}$<br>16.4&nbsp;KB<br>$\color{#2563eb}{0.95}$ | 💾&nbsp;4.08&nbsp;ms<br>1.06<br>16.1&nbsp;KB<br>0.94 | 3.93&nbsp;ms<br>1.02<br>16.9&nbsp;KB<br>0.98 | 5.90&nbsp;ms<br>1.54<br>121&nbsp;KB<br>**7.07** | 3.86&nbsp;ms<br>1.00<br>16.2&nbsp;KB<br>0.94 | 4.28&nbsp;ms<br>1.11<br>21.7&nbsp;KB<br>1.26 | 4.04&nbsp;ms<br>1.05<br>36.7&nbsp;KB<br>**2.14** | 4.27&nbsp;ms<br>1.11<br>28.3&nbsp;KB<br>1.65 |
| Insert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;3.88&nbsp;ms<br>1.00<br>16.2&nbsp;KB<br>1.00 | 3.88&nbsp;ms<br>$\color{#d97706}{1.00}$<br>16.0&nbsp;KB<br>$\color{#2563eb}{0.99}$ | 💾&nbsp;4.14&nbsp;ms<br>1.07<br>15.6&nbsp;KB<br>0.97 | 3.90&nbsp;ms<br>1.01<br>16.6&nbsp;KB<br>1.02 | 5.46&nbsp;ms<br>1.41<br>121&nbsp;KB<br>**7.46** | 3.89&nbsp;ms<br>1.00<br>16.2&nbsp;KB<br>1.00 | 4.49&nbsp;ms<br>1.16<br>22.4&nbsp;KB<br>1.38 | 3.99&nbsp;ms<br>1.03<br>36.7&nbsp;KB<br>**2.27** | 4.30&nbsp;ms<br>1.11<br>28.5&nbsp;KB<br>1.76 |
| Update | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 200.6&nbsp;µs<br>1.00<br>4.7&nbsp;KB<br>1.00 | 🏆&nbsp;199.8&nbsp;µs<br>$\color{#2563eb}{1.00}$<br>5.5&nbsp;KB<br>$\color{#ea580c}{1.17}$ | 💾&nbsp;213.8&nbsp;µs<br>1.07<br>4.5&nbsp;KB<br>0.94 | 220.4&nbsp;µs<br>1.10<br>7.3&nbsp;KB<br>1.55 | 729.9&nbsp;µs<br>**3.64**<br>93.2&nbsp;KB<br>**19.64** | 204.7&nbsp;µs<br>1.02<br>4.9&nbsp;KB<br>1.04 | 445.4&nbsp;µs<br>**2.22**<br>12.6&nbsp;KB<br>**2.65** | 274.6&nbsp;µs<br>1.37<br>31.0&nbsp;KB<br>**6.54** | 463.3&nbsp;µs<br>**2.31**<br>19.7&nbsp;KB<br>**4.15** |
| Update | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;206.6&nbsp;µs<br>1.00<br>4.7&nbsp;KB<br>1.00 | 211.8&nbsp;µs<br>$\color{#d97706}{1.03}$<br>5.6&nbsp;KB<br>$\color{#ea580c}{1.17}$ | 💾&nbsp;219.6&nbsp;µs<br>1.06<br>4.5&nbsp;KB<br>0.94 | 235.2&nbsp;µs<br>1.14<br>7.3&nbsp;KB<br>1.54 | 724.0&nbsp;µs<br>**3.51**<br>93.1&nbsp;KB<br>**19.62** | 217.6&nbsp;µs<br>1.05<br>5.0&nbsp;KB<br>1.04 | 444.1&nbsp;µs<br>**2.15**<br>12.6&nbsp;KB<br>**2.65** | 281.9&nbsp;µs<br>1.36<br>31.1&nbsp;KB<br>**6.54** | 466.9&nbsp;µs<br>**2.26**<br>19.7&nbsp;KB<br>**4.16** |
| BulkInsert | 2,000 | 🏆&nbsp;RepoDb<br>💾&nbsp;RepoDb | 11.83&nbsp;ms<br>1.00<br>485&nbsp;KB<br>1.00 | 11.82&nbsp;ms<br>$\color{#2563eb}{1.00}$<br>478&nbsp;KB<br>$\color{#2563eb}{0.99}$ | 35.38&nbsp;ms<br>**2.99**<br>6828&nbsp;KB<br>**14.09** | 🏆&nbsp;💾&nbsp;11.37&nbsp;ms<br>0.96<br>474&nbsp;KB<br>0.98 | 88.82&nbsp;ms<br>**7.51**<br>20.1&nbsp;MB<br>**42.56** | 34.08&nbsp;ms<br>**2.88**<br>6826&nbsp;KB<br>**14.08** | 17.98&nbsp;ms<br>1.52<br>3308&nbsp;KB<br>**6.82** | 22.22&nbsp;ms<br>1.88<br>3344&nbsp;KB<br>**6.90** | 16.07&nbsp;ms<br>1.36<br>2394&nbsp;KB<br>**4.94** |
| BulkInsert | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 61.80&nbsp;ms<br>1.00<br>4581&nbsp;KB<br>1.00 | 🏆&nbsp;58.81&nbsp;ms<br>$\color{#2563eb}{0.95}$<br>4506&nbsp;KB<br>$\color{#2563eb}{0.98}$ | 251.50&nbsp;ms<br>**4.07**<br>66.4&nbsp;MB<br>**14.84** | 💾&nbsp;61.85&nbsp;ms<br>1.00<br>4499&nbsp;KB<br>0.98 | 600.39&nbsp;ms<br>**9.72**<br>199.8&nbsp;MB<br>**44.67** | 259.43&nbsp;ms<br>**4.20**<br>66.4&nbsp;MB<br>**14.85** | 128.28&nbsp;ms<br>**2.08**<br>33.2&nbsp;MB<br>**7.42** | 234.57&nbsp;ms<br>**3.80**<br>31.3&nbsp;MB<br>**6.99** | 99.08&nbsp;ms<br>1.60<br>24.4&nbsp;MB<br>**5.44** |
| BulkUpdate | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 10.15&nbsp;ms<br>1.00<br>4690&nbsp;KB<br>1.00 | 🏆&nbsp;9.62&nbsp;ms<br>$\color{#2563eb}{0.95}$<br>3738&nbsp;KB<br>$\color{#65a30d}{0.80}$ | 11.38&nbsp;ms<br>1.12<br>6610&nbsp;KB<br>1.41 | 💾&nbsp;162.63&nbsp;ms<br>**16.02**<br>906&nbsp;KB<br>**0.19** | 161.36&nbsp;ms<br>**15.89**<br>36.1&nbsp;MB<br>**7.89** | 11.45&nbsp;ms<br>1.13<br>6609&nbsp;KB<br>1.41 | 10.67&nbsp;ms<br>1.05<br>2135&nbsp;KB<br>**0.46** | 32.84&nbsp;ms<br>**3.23**<br>7746&nbsp;KB<br>1.65 | 12.64&nbsp;ms<br>1.24<br>2353&nbsp;KB<br>0.50 |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;RepoDb | 🏆&nbsp;86.73&nbsp;ms<br>1.00<br>14.6&nbsp;MB<br>1.00 | 87.87&nbsp;ms<br>$\color{#d97706}{1.01}$<br>13.7&nbsp;MB<br>$\color{#2563eb}{0.94}$ | 145.35&nbsp;ms<br>1.68<br>64.5&nbsp;MB<br>**4.42** | 💾&nbsp;401.75&nbsp;ms<br>**4.63**<br>5562&nbsp;KB<br>**0.37** | 1.60&nbsp;s<br>**18.49**<br>359.1&nbsp;MB<br>**24.59** | 145.77&nbsp;ms<br>1.68<br>64.5&nbsp;MB<br>**4.42** | 99.81&nbsp;ms<br>1.15<br>21.8&nbsp;MB<br>1.49 | 322.73&nbsp;ms<br>**3.72**<br>75.9&nbsp;MB<br>**5.20** | 106.65&nbsp;ms<br>1.23<br>24.0&nbsp;MB<br>1.64 |
| BulkDelete | 2,000 | 🏆&nbsp;SqlSugar<br>💾&nbsp;SqlSugar | 12.74&nbsp;ms<br>1.00<br>882&nbsp;KB<br>1.00 | 12.49&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>806&nbsp;KB<br>$\color{#2563eb}{0.91}$ | 12.96&nbsp;ms<br>1.02<br>1377&nbsp;KB<br>1.56 | 27.95&nbsp;ms<br>**2.19**<br>19.3&nbsp;MB<br>**22.41** | 24.89&nbsp;ms<br>1.95<br>3784&nbsp;KB<br>**4.29** | 12.85&nbsp;ms<br>1.01<br>1377&nbsp;KB<br>1.56 | 17.93&nbsp;ms<br>1.41<br>613&nbsp;KB<br>0.70 | 13.48&nbsp;ms<br>1.06<br>715&nbsp;KB<br>0.81 | 🏆&nbsp;💾&nbsp;12.17&nbsp;ms<br>0.96<br>374&nbsp;KB<br>**0.42** |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 86.14&nbsp;ms<br>1.00<br>8701&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;72.71&nbsp;ms<br>$\color{#65a30d}{0.84}$<br>3622&nbsp;KB<br>$\color{#16a34a}{0.42}$ | 81.38&nbsp;ms<br>0.94<br>13.2&nbsp;MB<br>1.56 | 235.61&nbsp;ms<br>**2.74**<br>195.2&nbsp;MB<br>**22.97** | 270.78&nbsp;ms<br>**3.14**<br>37.0&nbsp;MB<br>**4.36** | 177.36&nbsp;ms<br>**2.06**<br>13.6&nbsp;MB<br>1.60 | 190.00&nbsp;ms<br>**2.21**<br>6146&nbsp;KB<br>0.71 | 79.10&nbsp;ms<br>0.92<br>7068&nbsp;KB<br>0.81 | 170.20&nbsp;ms<br>1.98<br>4398&nbsp;KB<br>0.51 |
| UpsertBatch | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 8.48&nbsp;ms<br>1.00<br>2965&nbsp;KB<br>1.00 | 🏆&nbsp;8.13&nbsp;ms<br>$\color{#2563eb}{0.96}$<br>2518&nbsp;KB<br>$\color{#65a30d}{0.85}$ | 11.36&nbsp;ms<br>1.34<br>6778&nbsp;KB<br>**2.29** | 💾&nbsp;162.47&nbsp;ms<br>**19.17**<br>917&nbsp;KB<br>**0.31** | 16.16&nbsp;ms<br>1.91<br>10.2&nbsp;MB<br>**3.52** | 11.61&nbsp;ms<br>1.37<br>6779&nbsp;KB<br>**2.29** | 9.85&nbsp;ms<br>1.16<br>2554&nbsp;KB<br>0.86 | 32.79&nbsp;ms<br>**3.87**<br>8095&nbsp;KB<br>**2.73** | 289.55&nbsp;ms<br>**34.16**<br>219.3&nbsp;MB<br>**75.74** |
| UpsertBatch | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 78.18&nbsp;ms<br>1.00<br>12.9&nbsp;MB<br>1.00 | 🏆&nbsp;77.17&nbsp;ms<br>$\color{#2563eb}{0.99}$<br>12.6&nbsp;MB<br>$\color{#2563eb}{0.98}$ | 135.03&nbsp;ms<br>1.73<br>66.1&nbsp;MB<br>**5.15** | 💾&nbsp;419.83&nbsp;ms<br>**5.37**<br>5597&nbsp;KB<br>**0.43** | 169.89&nbsp;ms<br>**2.17**<br>103.4&nbsp;MB<br>**8.04** | 139.64&nbsp;ms<br>1.79<br>66.1&nbsp;MB<br>**5.15** | 134.05&nbsp;ms<br>1.71<br>25.8&nbsp;MB<br>**2.01** | 320.35&nbsp;ms<br>**4.10**<br>79.2&nbsp;MB<br>**6.16** | 2.85&nbsp;s<br>**36.50**<br>2358.3&nbsp;MB<br>**183.51** |
| KeysetPage | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;278.2&nbsp;µs<br>1.00<br>16.4&nbsp;KB<br>1.00 | 291.4&nbsp;µs<br>$\color{#d97706}{1.05}$<br>18.3&nbsp;KB<br>$\color{#ea580c}{1.11}$ | 279.4&nbsp;µs<br>1.00<br>17.5&nbsp;KB<br>1.07 | 299.6&nbsp;µs<br>1.08<br>22.1&nbsp;KB<br>1.35 | 948.6&nbsp;µs<br>**3.41**<br>318&nbsp;KB<br>**19.41** | 293.4&nbsp;µs<br>1.05<br>28.3&nbsp;KB<br>1.73 | 532.8&nbsp;µs<br>1.92<br>29.9&nbsp;KB<br>1.82 | 439.7&nbsp;µs<br>1.58<br>228&nbsp;KB<br>**13.91** | 572.3&nbsp;µs<br>**2.06**<br>52.8&nbsp;KB<br>**3.22** |
| KeysetPage | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;277.9&nbsp;µs<br>1.00<br>16.4&nbsp;KB<br>1.00 | 🏆&nbsp;269.3&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>18.2&nbsp;KB<br>$\color{#ea580c}{1.11}$ | 281.5&nbsp;µs<br>1.01<br>17.5&nbsp;KB<br>1.07 | 290.4&nbsp;µs<br>1.05<br>22.1&nbsp;KB<br>1.35 | 916.5&nbsp;µs<br>**3.30**<br>318&nbsp;KB<br>**19.40** | 286.8&nbsp;µs<br>1.03<br>28.3&nbsp;KB<br>1.72 | 543.1&nbsp;µs<br>1.95<br>29.9&nbsp;KB<br>1.82 | 414.4&nbsp;µs<br>1.49<br>228&nbsp;KB<br>**13.91** | 563.6&nbsp;µs<br>**2.03**<br>52.8&nbsp;KB<br>**3.22** |
| WhereIn | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;FreeSql | 🏆&nbsp;372.0&nbsp;µs<br>1.00<br>57.7&nbsp;KB<br>1.00 | 380.3&nbsp;µs<br>$\color{#d97706}{1.02}$<br>53.5&nbsp;KB<br>$\color{#2563eb}{0.93}$ | 395.1&nbsp;µs<br>1.06<br>90.2&nbsp;KB<br>1.56 | 488.9&nbsp;µs<br>1.31<br>101&nbsp;KB<br>1.75 | 1.28&nbsp;ms<br>**3.45**<br>491&nbsp;KB<br>**8.51** | 395.9&nbsp;µs<br>1.06<br>90.7&nbsp;KB<br>1.57 | 💾&nbsp;613.9&nbsp;µs<br>1.65<br>47.1&nbsp;KB<br>0.82 | 551.7&nbsp;µs<br>1.48<br>266&nbsp;KB<br>**4.61** | 728.3&nbsp;µs<br>1.96<br>97.8&nbsp;KB<br>1.70 |
| WhereIn | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;FreeSql | 387.8&nbsp;µs<br>1.00<br>57.7&nbsp;KB<br>1.00 | 🏆&nbsp;370.3&nbsp;µs<br>$\color{#2563eb}{0.95}$<br>53.5&nbsp;KB<br>$\color{#2563eb}{0.93}$ | 388.5&nbsp;µs<br>1.00<br>90.2&nbsp;KB<br>1.56 | 484.6&nbsp;µs<br>1.25<br>101&nbsp;KB<br>1.75 | 1.29&nbsp;ms<br>**3.33**<br>506&nbsp;KB<br>**8.77** | 398.6&nbsp;µs<br>1.03<br>90.7&nbsp;KB<br>1.57 | 💾&nbsp;623.0&nbsp;µs<br>1.61<br>50.5&nbsp;KB<br>0.88 | 567.9&nbsp;µs<br>1.46<br>285&nbsp;KB<br>**4.94** | 705.9&nbsp;µs<br>1.82<br>99.7&nbsp;KB<br>1.73 |
| Count | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;590.5&nbsp;µs<br>1.00<br>3.7&nbsp;KB<br>1.00 | 605.7&nbsp;µs<br>$\color{#d97706}{1.03}$<br>5.0&nbsp;KB<br>$\color{#dc2626}{1.33}$ | 596.4&nbsp;µs<br>1.01<br>4.1&nbsp;KB<br>1.10 | 601.4&nbsp;µs<br>1.02<br>4.6&nbsp;KB<br>1.24 | 1.15&nbsp;ms<br>1.95<br>82.0&nbsp;KB<br>**21.89** | 610.7&nbsp;µs<br>1.03<br>4.1&nbsp;KB<br>1.10 | 816.0&nbsp;µs<br>1.38<br>11.7&nbsp;KB<br>**3.11** | 615.0&nbsp;µs<br>1.04<br>6.9&nbsp;KB<br>1.84 | 883.9&nbsp;µs<br>1.50<br>30.4&nbsp;KB<br>**8.12** |
| Count | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;778.2&nbsp;µs<br>1.00<br>4.3&nbsp;KB<br>1.00 | 789.8&nbsp;µs<br>$\color{#d97706}{1.01}$<br>5.5&nbsp;KB<br>$\color{#ea580c}{1.28}$ | 790.5&nbsp;µs<br>1.02<br>4.6&nbsp;KB<br>1.08 | 799.8&nbsp;µs<br>1.03<br>5.2&nbsp;KB<br>1.22 | 1.32&nbsp;ms<br>1.69<br>82.5&nbsp;KB<br>**19.21** | 798.7&nbsp;µs<br>1.03<br>4.6&nbsp;KB<br>1.07 | 1.03&nbsp;ms<br>1.32<br>12.3&nbsp;KB<br>**2.85** | 794.3&nbsp;µs<br>1.02<br>7.4&nbsp;KB<br>1.73 | 1.08&nbsp;ms<br>1.39<br>31.0&nbsp;KB<br>**7.21** |

#### PostgreSQL (2000 and 20000)

| Operation | Tier | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;Dapper.AOT | 233.6&nbsp;µs<br>1.00<br>3.0&nbsp;KB<br>1.00 | 233.2&nbsp;µs<br>$\color{#2563eb}{1.00}$<br>3.4&nbsp;KB<br>$\color{#ea580c}{1.15}$ | 🏆&nbsp;💾&nbsp;220.8&nbsp;µs<br>0.95<br>2.6&nbsp;KB<br>0.88 | 234.3&nbsp;µs<br>1.00<br>5.7&nbsp;KB<br>1.91 | 335.9&nbsp;µs<br>1.44<br>43.9&nbsp;KB<br>**14.68** | 225.1&nbsp;µs<br>0.96<br>3.2&nbsp;KB<br>1.08 | 301.9&nbsp;µs<br>1.29<br>12.0&nbsp;KB<br>**4.00** | 250.6&nbsp;µs<br>1.07<br>7.7&nbsp;KB<br>**2.56** | 359.6&nbsp;µs<br>1.54<br>31.4&nbsp;KB<br>**10.48** |
| GetByKey | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;219.8&nbsp;µs<br>1.00<br>2.9&nbsp;KB<br>1.00 | 223.8&nbsp;µs<br>$\color{#d97706}{1.02}$<br>3.4&nbsp;KB<br>$\color{#ea580c}{1.15}$ | 💾&nbsp;224.3&nbsp;µs<br>1.02<br>2.6&nbsp;KB<br>0.90 | 237.5&nbsp;µs<br>1.08<br>5.7&nbsp;KB<br>1.93 | 352.3&nbsp;µs<br>1.60<br>43.9&nbsp;KB<br>**14.90** | 230.2&nbsp;µs<br>1.05<br>3.3&nbsp;KB<br>1.11 | 311.5&nbsp;µs<br>1.42<br>12.0&nbsp;KB<br>**4.08** | 254.7&nbsp;µs<br>1.16<br>7.7&nbsp;KB<br>**2.61** | 367.9&nbsp;µs<br>1.67<br>31.4&nbsp;KB<br>**10.64** |
| GetAllAsync | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 713.9&nbsp;µs<br>1.00<br>240&nbsp;KB<br>1.00 | 💾&nbsp;752.5&nbsp;µs<br>$\color{#d97706}{1.05}$<br>224&nbsp;KB<br>$\color{#2563eb}{0.93}$ | 🏆&nbsp;708.3&nbsp;µs<br>0.99<br>239&nbsp;KB<br>1.00 | 793.1&nbsp;µs<br>1.11<br>241&nbsp;KB<br>1.00 | 893.6&nbsp;µs<br>1.25<br>652&nbsp;KB<br>**2.72** | 824.9&nbsp;µs<br>1.16<br>443&nbsp;KB<br>1.84 | 777.5&nbsp;µs<br>1.09<br>244&nbsp;KB<br>1.02 | 729.3&nbsp;µs<br>1.02<br>241&nbsp;KB<br>1.01 | 934.6&nbsp;µs<br>1.31<br>254&nbsp;KB<br>1.06 |
| GetAllAsync | 20,000 | 🏆&nbsp;FreeSql<br>💾&nbsp;PalORM | 6.04&nbsp;ms<br>1.00<br>2575&nbsp;KB<br>1.00 | 💾&nbsp;5.91&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>2220&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 6.29&nbsp;ms<br>1.04<br>2565&nbsp;KB<br>1.00 | 5.88&nbsp;ms<br>0.97<br>2574&nbsp;KB<br>1.00 | 6.92&nbsp;ms<br>1.15<br>6356&nbsp;KB<br>**2.47** | 6.84&nbsp;ms<br>1.13<br>4599&nbsp;KB<br>1.79 | 🏆&nbsp;5.88&nbsp;ms<br>0.97<br>2575&nbsp;KB<br>1.00 | 6.40&nbsp;ms<br>1.06<br>2574&nbsp;KB<br>1.00 | 7.72&nbsp;ms<br>1.28<br>2585&nbsp;KB<br>1.00 |
| QueryAll | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;702.9&nbsp;µs<br>1.00<br>240&nbsp;KB<br>1.00 | 💾&nbsp;717.1&nbsp;µs<br>$\color{#d97706}{1.02}$<br>225&nbsp;KB<br>$\color{#2563eb}{0.94}$ | 725.0&nbsp;µs<br>1.03<br>239&nbsp;KB<br>1.00 | 795.5&nbsp;µs<br>1.13<br>241&nbsp;KB<br>1.01 | 904.2&nbsp;µs<br>1.29<br>652&nbsp;KB<br>**2.72** | 787.0&nbsp;µs<br>1.12<br>443&nbsp;KB<br>1.85 | 797.0&nbsp;µs<br>1.13<br>245&nbsp;KB<br>1.02 | 750.4&nbsp;µs<br>1.07<br>241&nbsp;KB<br>1.01 | 937.1&nbsp;µs<br>1.33<br>254&nbsp;KB<br>1.06 |
| QueryAll | 20,000 | 🏆&nbsp;linq2db<br>💾&nbsp;PalORM | 6.31&nbsp;ms<br>1.00<br>2576&nbsp;KB<br>1.00 | 💾&nbsp;6.34&nbsp;ms<br>$\color{#d97706}{1.01}$<br>2221&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 6.67&nbsp;ms<br>1.06<br>2567&nbsp;KB<br>1.00 | 6.02&nbsp;ms<br>0.96<br>2575&nbsp;KB<br>1.00 | 7.00&nbsp;ms<br>1.11<br>6356&nbsp;KB<br>**2.47** | 6.63&nbsp;ms<br>1.05<br>4599&nbsp;KB<br>1.79 | 6.00&nbsp;ms<br>0.95<br>2575&nbsp;KB<br>1.00 | 🏆&nbsp;5.95&nbsp;ms<br>0.94<br>2570&nbsp;KB<br>1.00 | 9.52&nbsp;ms<br>1.51<br>2586&nbsp;KB<br>1.00 |
| Insert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;1.38&nbsp;ms<br>1.00<br>7.6&nbsp;KB<br>1.00 | 1.44&nbsp;ms<br>$\color{#d97706}{1.05}$<br>7.8&nbsp;KB<br>$\color{#d97706}{1.03}$ | 💾&nbsp;1.43&nbsp;ms<br>1.04<br>6.7&nbsp;KB<br>0.88 | 1.44&nbsp;ms<br>1.04<br>7.9&nbsp;KB<br>1.05 | 1.52&nbsp;ms<br>1.10<br>52.0&nbsp;KB<br>**6.88** | 1.41&nbsp;ms<br>1.02<br>7.3&nbsp;KB<br>0.97 | 1.46&nbsp;ms<br>1.06<br>11.0&nbsp;KB<br>1.45 | 1.48&nbsp;ms<br>1.08<br>21.8&nbsp;KB<br>**2.89** | 1.50&nbsp;ms<br>1.09<br>18.8&nbsp;KB<br>**2.48** |
| Insert | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;Dapper.AOT | 1.41&nbsp;ms<br>1.00<br>7.6&nbsp;KB<br>1.00 | 1.39&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>7.6&nbsp;KB<br>$\color{#d97706}{1.01}$ | 🏆&nbsp;💾&nbsp;1.38&nbsp;ms<br>0.98<br>6.7&nbsp;KB<br>0.88 | 1.45&nbsp;ms<br>1.03<br>7.9&nbsp;KB<br>1.05 | 1.56&nbsp;ms<br>1.11<br>52.0&nbsp;KB<br>**6.89** | 1.41&nbsp;ms<br>1.00<br>7.3&nbsp;KB<br>0.97 | 1.48&nbsp;ms<br>1.05<br>11.0&nbsp;KB<br>1.45 | 1.46&nbsp;ms<br>1.03<br>21.8&nbsp;KB<br>**2.88** | 1.48&nbsp;ms<br>1.05<br>18.8&nbsp;KB<br>**2.48** |
| Update | 2,000 | 🏆&nbsp;Dapper<br>💾&nbsp;Dapper.AOT | 1.48&nbsp;ms<br>1.00<br>9.1&nbsp;KB<br>1.00 | 1.45&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>8.0&nbsp;KB<br>$\color{#65a30d}{0.89}$ | 💾&nbsp;1.42&nbsp;ms<br>0.96<br>6.6&nbsp;KB<br>0.73 | 1.47&nbsp;ms<br>0.99<br>9.9&nbsp;KB<br>1.09 | 1.56&nbsp;ms<br>1.05<br>52.1&nbsp;KB<br>**5.75** | 🏆&nbsp;1.42&nbsp;ms<br>0.96<br>7.6&nbsp;KB<br>0.84 | 1.50&nbsp;ms<br>1.01<br>13.7&nbsp;KB<br>1.51 | 1.46&nbsp;ms<br>0.99<br>25.1&nbsp;KB<br>**2.77** | 1.58&nbsp;ms<br>1.07<br>21.3&nbsp;KB<br>**2.35** |
| Update | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;1.40&nbsp;ms<br>1.00<br>7.4&nbsp;KB<br>1.00 | 1.45&nbsp;ms<br>$\color{#d97706}{1.03}$<br>8.1&nbsp;KB<br>$\color{#ea580c}{1.10}$ | 💾&nbsp;1.41&nbsp;ms<br>1.01<br>6.6&nbsp;KB<br>0.90 | 1.51&nbsp;ms<br>1.08<br>9.6&nbsp;KB<br>1.31 | 1.59&nbsp;ms<br>1.13<br>52.2&nbsp;KB<br>**7.09** | 1.43&nbsp;ms<br>1.02<br>7.2&nbsp;KB<br>0.98 | 1.44&nbsp;ms<br>1.03<br>13.2&nbsp;KB<br>1.79 | 1.51&nbsp;ms<br>1.08<br>25.2&nbsp;KB<br>**3.43** | 1.53&nbsp;ms<br>1.09<br>21.2&nbsp;KB<br>**2.89** |
| BulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;5.00&nbsp;ms<br>1.00<br>339&nbsp;KB<br>1.00 | 5.26&nbsp;ms<br>$\color{#d97706}{1.05}$<br>341&nbsp;KB<br>$\color{#d97706}{1.00}$ | 18.09&nbsp;ms<br>**3.62**<br>6849&nbsp;KB<br>**20.19** | 6.12&nbsp;ms<br>1.22<br>845&nbsp;KB<br>**2.49** | 47.00&nbsp;ms<br>**9.41**<br>15.9&nbsp;MB<br>**47.98** | 17.46&nbsp;ms<br>**3.49**<br>6851&nbsp;KB<br>**20.19** | 14.87&nbsp;ms<br>**2.98**<br>3937&nbsp;KB<br>**11.60** | 12.81&nbsp;ms<br>**2.56**<br>1172&nbsp;KB<br>**3.45** | 28.65&nbsp;ms<br>**5.74**<br>7576&nbsp;KB<br>**22.33** |
| BulkInsert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;22.34&nbsp;ms<br>1.00<br>3329&nbsp;KB<br>1.00 | 23.15&nbsp;ms<br>$\color{#d97706}{1.04}$<br>3333&nbsp;KB<br>$\color{#d97706}{1.00}$ | 204.54&nbsp;ms<br>**9.16**<br>66.9&nbsp;MB<br>**20.56** | 29.54&nbsp;ms<br>1.32<br>8205&nbsp;KB<br>**2.46** | 477.21&nbsp;ms<br>**21.36**<br>153.7&nbsp;MB<br>**47.26** | 211.77&nbsp;ms<br>**9.48**<br>66.9&nbsp;MB<br>**20.57** | 126.99&nbsp;ms<br>**5.69**<br>39.5&nbsp;MB<br>**12.16** | 132.78&nbsp;ms<br>**5.94**<br>10.0&nbsp;MB<br>**3.09** | 306.42&nbsp;ms<br>**13.72**<br>75.0&nbsp;MB<br>**23.06** |
| BulkUpdate | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;5.71&nbsp;ms<br>1.00<br>419&nbsp;KB<br>1.00 | 🏆&nbsp;5.40&nbsp;ms<br>$\color{#2563eb}{0.95}$<br>422&nbsp;KB<br>$\color{#d97706}{1.01}$ | 16.62&nbsp;ms<br>**2.91**<br>6688&nbsp;KB<br>**15.95** | 58.38&nbsp;ms<br>**10.22**<br>3883&nbsp;KB<br>**9.26** | 65.24&nbsp;ms<br>**11.42**<br>16.0&nbsp;MB<br>**39.13** | 16.61&nbsp;ms<br>**2.91**<br>6689&nbsp;KB<br>**15.95** | 14.66&nbsp;ms<br>**2.57**<br>2779&nbsp;KB<br>**6.63** | 15.22&nbsp;ms<br>**2.66**<br>2138&nbsp;KB<br>**5.10** | 40.76&nbsp;ms<br>**7.13**<br>11.8&nbsp;MB<br>**28.88** |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;72.18&nbsp;ms<br>1.00<br>4193&nbsp;KB<br>1.00 | 73.61&nbsp;ms<br>$\color{#d97706}{1.02}$<br>4200&nbsp;KB<br>$\color{#d97706}{1.00}$ | 227.57&nbsp;ms<br>**3.15**<br>65.4&nbsp;MB<br>**15.97** | 576.54&nbsp;ms<br>**7.99**<br>37.9&nbsp;MB<br>**9.25** | 686.41&nbsp;ms<br>**9.51**<br>155.0&nbsp;MB<br>**37.85** | 228.99&nbsp;ms<br>**3.17**<br>65.4&nbsp;MB<br>**15.97** | 142.37&nbsp;ms<br>1.97<br>28.3&nbsp;MB<br>**6.91** | 168.21&nbsp;ms<br>**2.33**<br>21.2&nbsp;MB<br>**5.17** | 475.63&nbsp;ms<br>**6.59**<br>120.7&nbsp;MB<br>**29.47** |
| BulkDelete | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 3.71&nbsp;ms<br>1.00<br>784&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;2.42&nbsp;ms<br>$\color{#16a34a}{0.65}$<br>89.5&nbsp;KB<br>$\color{#16a34a}{0.11}$ | 3.92&nbsp;ms<br>1.06<br>1338&nbsp;KB<br>1.71 | 20.17&nbsp;ms<br>**5.44**<br>19.2&nbsp;MB<br>**25.02** | 4.43&nbsp;ms<br>1.20<br>177&nbsp;KB<br>**0.23** | 3.87&nbsp;ms<br>1.04<br>1339&nbsp;KB<br>1.71 | 5.81&nbsp;ms<br>1.57<br>570&nbsp;KB<br>0.73 | 4.65&nbsp;ms<br>1.25<br>264&nbsp;KB<br>**0.34** | 4.09&nbsp;ms<br>1.10<br>346&nbsp;KB<br>**0.44** |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 27.73&nbsp;ms<br>1.00<br>7881&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;13.28&nbsp;ms<br>$\color{#16a34a}{0.48}$<br>865&nbsp;KB<br>$\color{#16a34a}{0.11}$ | 32.55&nbsp;ms<br>1.17<br>13.0&nbsp;MB<br>1.69 | 189.75&nbsp;ms<br>**6.84**<br>193.8&nbsp;MB<br>**25.19** | 43.01&nbsp;ms<br>1.55<br>1753&nbsp;KB<br>**0.22** | 30.61&nbsp;ms<br>1.10<br>13.1&nbsp;MB<br>1.70 | 60.72&nbsp;ms<br>**2.19**<br>6092&nbsp;KB<br>0.77 | 35.04&nbsp;ms<br>1.26<br>2705&nbsp;KB<br>**0.34** | 31.05&nbsp;ms<br>1.12<br>4454&nbsp;KB<br>0.57 |
| UpsertBatch | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 17.81&nbsp;ms<br>1.00<br>2785&nbsp;KB<br>1.00 | 💾&nbsp;8.94&nbsp;ms<br>$\color{#16a34a}{0.50}$<br>449&nbsp;KB<br>$\color{#16a34a}{0.16}$ | 21.52&nbsp;ms<br>1.21<br>6865&nbsp;KB<br>**2.47** | 27.93&nbsp;ms<br>1.57<br>5918&nbsp;KB<br>**2.13** | 🏆&nbsp;3.07&nbsp;ms<br>**0.17**<br>3040&nbsp;KB<br>1.09 | 21.55&nbsp;ms<br>1.21<br>6866&nbsp;KB<br>**2.47** | 18.72&nbsp;ms<br>1.05<br>2803&nbsp;KB<br>1.01 | 17.69&nbsp;ms<br>0.99<br>2151&nbsp;KB<br>0.77 | 291.86&nbsp;ms<br>**16.39**<br>219.2&nbsp;MB<br>**80.61** |
| UpsertBatch | 20,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 153.15&nbsp;ms<br>1.00<br>11.6&nbsp;MB<br>1.00 | 💾&nbsp;106.38&nbsp;ms<br>$\color{#16a34a}{0.69}$<br>4439&nbsp;KB<br>$\color{#16a34a}{0.37}$ | 246.42&nbsp;ms<br>1.61<br>67.0&nbsp;MB<br>**5.78** | 270.12&nbsp;ms<br>1.76<br>57.4&nbsp;MB<br>**4.95** | 🏆&nbsp;50.10&nbsp;ms<br>**0.33**<br>29.1&nbsp;MB<br>**2.51** | 242.61&nbsp;ms<br>1.58<br>67.0&nbsp;MB<br>**5.78** | 170.61&nbsp;ms<br>1.11<br>28.4&nbsp;MB<br>**2.45** | 188.92&nbsp;ms<br>1.23<br>21.2&nbsp;MB<br>1.83 | 3.12&nbsp;s<br>**20.38**<br>2358.6&nbsp;MB<br>**203.38** |
| KeysetPage | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;267.6&nbsp;µs<br>1.00<br>15.4&nbsp;KB<br>1.00 | 293.9&nbsp;µs<br>$\color{#ea580c}{1.10}$<br>17.3&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 💾&nbsp;274.7&nbsp;µs<br>1.03<br>15.4&nbsp;KB<br>1.00 | 267.8&nbsp;µs<br>1.00<br>19.7&nbsp;KB<br>1.28 | 407.6&nbsp;µs<br>1.52<br>78.3&nbsp;KB<br>**5.08** | 284.1&nbsp;µs<br>1.06<br>26.1&nbsp;KB<br>1.69 | 358.9&nbsp;µs<br>1.34<br>25.9&nbsp;KB<br>1.68 | 301.9&nbsp;µs<br>1.13<br>21.2&nbsp;KB<br>1.37 | 422.1&nbsp;µs<br>1.58<br>48.8&nbsp;KB<br>**3.16** |
| KeysetPage | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;268.2&nbsp;µs<br>1.00<br>15.4&nbsp;KB<br>1.00 | 274.6&nbsp;µs<br>$\color{#d97706}{1.02}$<br>17.3&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 💾&nbsp;269.6&nbsp;µs<br>1.01<br>15.4&nbsp;KB<br>1.00 | 286.3&nbsp;µs<br>1.07<br>19.8&nbsp;KB<br>1.28 | 440.6&nbsp;µs<br>1.64<br>78.4&nbsp;KB<br>**5.08** | 282.4&nbsp;µs<br>1.05<br>26.1&nbsp;KB<br>1.69 | 380.9&nbsp;µs<br>1.42<br>26.0&nbsp;KB<br>1.68 | 313.6&nbsp;µs<br>1.17<br>21.2&nbsp;KB<br>1.37 | 456.6&nbsp;µs<br>1.70<br>48.9&nbsp;KB<br>**3.17** |
| WhereIn | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 383.9&nbsp;µs<br>1.00<br>52.8&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;322.9&nbsp;µs<br>$\color{#65a30d}{0.84}$<br>17.8&nbsp;KB<br>$\color{#16a34a}{0.34}$ | 413.9&nbsp;µs<br>1.08<br>87.2&nbsp;KB<br>1.65 | 503.6&nbsp;µs<br>1.31<br>93.9&nbsp;KB<br>1.78 | 452.9&nbsp;µs<br>1.18<br>76.8&nbsp;KB<br>1.45 | 412.4&nbsp;µs<br>1.07<br>87.7&nbsp;KB<br>1.66 | 513.3&nbsp;µs<br>1.34<br>43.4&nbsp;KB<br>0.82 | 463.1&nbsp;µs<br>1.21<br>40.4&nbsp;KB<br>0.76 | 628.3&nbsp;µs<br>1.64<br>93.2&nbsp;KB<br>1.76 |
| WhereIn | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 427.6&nbsp;µs<br>1.00<br>53.0&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;365.4&nbsp;µs<br>$\color{#65a30d}{0.85}$<br>18.0&nbsp;KB<br>$\color{#16a34a}{0.34}$ | 443.0&nbsp;µs<br>1.04<br>87.3&nbsp;KB<br>1.65 | 545.5&nbsp;µs<br>1.28<br>94.4&nbsp;KB<br>1.78 | 500.1&nbsp;µs<br>1.17<br>76.9&nbsp;KB<br>1.45 | 444.6&nbsp;µs<br>1.04<br>87.8&nbsp;KB<br>1.66 | 569.1&nbsp;µs<br>1.33<br>47.0&nbsp;KB<br>0.89 | 493.6&nbsp;µs<br>1.15<br>41.1&nbsp;KB<br>0.78 | 664.4&nbsp;µs<br>1.55<br>95.3&nbsp;KB<br>1.80 |
| Count | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;255.2&nbsp;µs<br>1.00<br>1.9&nbsp;KB<br>1.00 | 258.8&nbsp;µs<br>$\color{#d97706}{1.01}$<br>3.1&nbsp;KB<br>$\color{#dc2626}{1.63}$ | 258.0&nbsp;µs<br>1.01<br>2.0&nbsp;KB<br>1.03 | 260.7&nbsp;µs<br>1.02<br>2.7&nbsp;KB<br>1.41 | 371.6&nbsp;µs<br>1.46<br>41.0&nbsp;KB<br>**21.54** | 258.3&nbsp;µs<br>1.01<br>2.0&nbsp;KB<br>1.05 | 333.7&nbsp;µs<br>1.31<br>7.6&nbsp;KB<br>**4.00** | 260.4&nbsp;µs<br>1.02<br>2.3&nbsp;KB<br>1.21 | 365.5&nbsp;µs<br>1.43<br>26.4&nbsp;KB<br>**13.90** |
| Count | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;711.6&nbsp;µs<br>1.00<br>3.2&nbsp;KB<br>1.00 | 749.2&nbsp;µs<br>$\color{#d97706}{1.05}$<br>4.5&nbsp;KB<br>$\color{#dc2626}{1.40}$ | 🏆&nbsp;705.7&nbsp;µs<br>0.99<br>3.2&nbsp;KB<br>1.01 | 711.1&nbsp;µs<br>1.00<br>4.0&nbsp;KB<br>1.24 | 907.6&nbsp;µs<br>1.28<br>42.5&nbsp;KB<br>**13.23** | 713.7&nbsp;µs<br>1.00<br>3.3&nbsp;KB<br>1.02 | 868.6&nbsp;µs<br>1.22<br>9.1&nbsp;KB<br>**2.83** | 712.4&nbsp;µs<br>1.00<br>3.6&nbsp;KB<br>1.12 | 931.5&nbsp;µs<br>1.31<br>28.1&nbsp;KB<br>**8.73** |

### Transaction comparison (2000 tier only)

> 🏆 = fastest latency group · 💾 = least memory group

#### SQLite

| Operation | Tier | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;20.8&nbsp;µs<br>1.00<br>3.9&nbsp;KB<br>1.00 | 21.0&nbsp;µs<br>$\color{#d97706}{1.01}$<br>4.4&nbsp;KB<br>$\color{#ea580c}{1.11}$ | 🏆&nbsp;20.6&nbsp;µs<br>0.99<br>4.0&nbsp;KB<br>1.03 | 22.4&nbsp;µs<br>1.08<br>4.2&nbsp;KB<br>1.07 | 1.19&nbsp;ms<br>**57.20**<br>58.6&nbsp;KB<br>**14.84** | 21.1&nbsp;µs<br>1.02<br>4.7&nbsp;KB<br>1.20 | 34.9&nbsp;µs<br>1.68<br>7.0&nbsp;KB<br>1.77 | 1.07&nbsp;ms<br>**51.75**<br>20.9&nbsp;KB<br>**5.28** | 1.14&nbsp;ms<br>**54.95**<br>16.3&nbsp;KB<br>**4.13** |
| TxHundredInserts | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 197.3&nbsp;µs<br>1.00<br>137&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;192.3&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>92.9&nbsp;KB<br>$\color{#16a34a}{0.68}$ | 467.8&nbsp;µs<br>**2.37**<br>280&nbsp;KB<br>**2.05** | 607.8&nbsp;µs<br>**3.08**<br>295&nbsp;KB<br>**2.16** | 2.04&nbsp;ms<br>**10.32**<br>934&nbsp;KB<br>**6.83** | 263.4&nbsp;µs<br>1.33<br>220&nbsp;KB<br>1.61 | 1.63&nbsp;ms<br>**8.26**<br>541&nbsp;KB<br>**3.96** | 2.00&nbsp;ms<br>**10.15**<br>1646&nbsp;KB<br>**12.03** | 3.48&nbsp;ms<br>**17.61**<br>1198&nbsp;KB<br>**8.76** |
| TxBulkInsert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;linq2db | 2.71&nbsp;ms<br>1.00<br>1758&nbsp;KB<br>1.00 | 🏆&nbsp;2.66&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>1758&nbsp;KB<br>$\color{#d97706}{1.00}$ | 4.45&nbsp;ms<br>1.64<br>4353&nbsp;KB<br>**2.48** | 17.99&nbsp;ms<br>**6.63**<br>2709&nbsp;KB<br>1.54 | 23.80&nbsp;ms<br>**8.77**<br>17.3&nbsp;MB<br>**10.10** | 4.56&nbsp;ms<br>1.68<br>4354&nbsp;KB<br>**2.48** | 8.14&nbsp;ms<br>**3.00**<br>3563&nbsp;KB<br>**2.03** | 💾&nbsp;4.13&nbsp;ms<br>1.52<br>1280&nbsp;KB<br>0.73 | 9.31&nbsp;ms<br>**3.43**<br>5569&nbsp;KB<br>**3.17** |
| TxRollback | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;606.0&nbsp;µs<br>1.00<br>435&nbsp;KB<br>1.00 | 674.4&nbsp;µs<br>$\color{#ea580c}{1.11}$<br>437&nbsp;KB<br>$\color{#d97706}{1.01}$ | 2.20&nbsp;ms<br>**3.63**<br>1410&nbsp;KB<br>**3.24** | 2.90&nbsp;ms<br>**4.78**<br>2025&nbsp;KB<br>**4.66** | 5.69&nbsp;ms<br>**9.39**<br>4617&nbsp;KB<br>**10.62** | 2.26&nbsp;ms<br>**3.73**<br>1625&nbsp;KB<br>**3.74** | 8.20&nbsp;ms<br>**13.53**<br>3549&nbsp;KB<br>**8.16** | 5.80&nbsp;ms<br>**9.57**<br>9862&nbsp;KB<br>**22.68** | 14.45&nbsp;ms<br>**23.85**<br>7231&nbsp;KB<br>**16.63** |

#### MySQL

| Operation | Tier | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;4.24&nbsp;ms<br>1.00<br>19.4&nbsp;KB<br>1.00 | 4.85&nbsp;ms<br>$\color{#ea580c}{1.14}$<br>21.7&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 4.64&nbsp;ms<br>1.09<br>19.6&nbsp;KB<br>1.01 | 4.61&nbsp;ms<br>1.09<br>21.5&nbsp;KB<br>1.10 | 6.05&nbsp;ms<br>1.43<br>134&nbsp;KB<br>**6.90** | 4.51&nbsp;ms<br>1.06<br>20.2&nbsp;KB<br>1.04 | 4.97&nbsp;ms<br>1.17<br>26.6&nbsp;KB<br>1.37 | 4.85&nbsp;ms<br>1.15<br>53.5&nbsp;KB<br>**2.75** | 4.85&nbsp;ms<br>1.14<br>32.8&nbsp;KB<br>1.69 |
| TxHundredInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 28.08&nbsp;ms<br>1.00<br>317&nbsp;KB<br>1.00 | 💾&nbsp;29.18&nbsp;ms<br>$\color{#d97706}{1.04}$<br>274&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 28.82&nbsp;ms<br>1.03<br>437&nbsp;KB<br>1.38 | 30.72&nbsp;ms<br>1.09<br>586&nbsp;KB<br>1.85 | 🏆&nbsp;10.62&nbsp;ms<br>**0.38**<br>1134&nbsp;KB<br>**3.58** | 30.45&nbsp;ms<br>1.08<br>339&nbsp;KB<br>1.07 | 33.10&nbsp;ms<br>1.18<br>794&nbsp;KB<br>**2.51** | 34.90&nbsp;ms<br>1.24<br>2539&nbsp;KB<br>**8.01** | 35.02&nbsp;ms<br>1.25<br>1485&nbsp;KB<br>**4.69** |
| TxBulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;11.75&nbsp;ms<br>1.00<br>475&nbsp;KB<br>1.00 | 12.10&nbsp;ms<br>$\color{#d97706}{1.03}$<br>481&nbsp;KB<br>$\color{#d97706}{1.01}$ | 26.05&nbsp;ms<br>**2.22**<br>6813&nbsp;KB<br>**14.33** | 12.42&nbsp;ms<br>1.06<br>479&nbsp;KB<br>1.01 | 95.31&nbsp;ms<br>**8.11**<br>20.1&nbsp;MB<br>**43.33** | 29.27&nbsp;ms<br>**2.49**<br>6813&nbsp;KB<br>**14.33** | 20.97&nbsp;ms<br>1.79<br>3498&nbsp;KB<br>**7.36** | 17.71&nbsp;ms<br>1.51<br>3203&nbsp;KB<br>**6.74** | 27.81&nbsp;ms<br>**2.37**<br>7011&nbsp;KB<br>**14.75** |
| TxRollback | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 💾&nbsp;103.81&nbsp;ms<br>1.00<br>1117&nbsp;KB<br>1.00 | 105.95&nbsp;ms<br>$\color{#d97706}{1.02}$<br>1264&nbsp;KB<br>$\color{#ea580c}{1.13}$ | 110.34&nbsp;ms<br>1.06<br>2125&nbsp;KB<br>1.90 | 120.05&nbsp;ms<br>1.16<br>3554&nbsp;KB<br>**3.18** | 🏆&nbsp;37.16&nbsp;ms<br>**0.36**<br>9205&nbsp;KB<br>**8.24** | 111.35&nbsp;ms<br>1.07<br>2339&nbsp;KB<br>**2.09** | 130.30&nbsp;ms<br>1.26<br>4880&nbsp;KB<br>**4.37** | 149.66&nbsp;ms<br>1.44<br>14.9&nbsp;MB<br>**13.64** | 143.18&nbsp;ms<br>1.38<br>8608&nbsp;KB<br>**7.70** |

#### PostgreSQL

| Operation | Tier | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;1.53&nbsp;ms<br>1.00<br>8.4&nbsp;KB<br>1.00 | 1.56&nbsp;ms<br>$\color{#d97706}{1.02}$<br>9.3&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 💾&nbsp;1.55&nbsp;ms<br>1.01<br>7.4&nbsp;KB<br>0.89 | 1.58&nbsp;ms<br>1.03<br>8.6&nbsp;KB<br>1.03 | 2.01&nbsp;ms<br>1.32<br>57.1&nbsp;KB<br>**6.83** | 1.57&nbsp;ms<br>1.02<br>8.1&nbsp;KB<br>0.97 | 1.63&nbsp;ms<br>1.06<br>12.1&nbsp;KB<br>1.45 | 1.61&nbsp;ms<br>1.05<br>23.4&nbsp;KB<br>**2.80** | 1.64&nbsp;ms<br>1.07<br>19.9&nbsp;KB<br>**2.38** |
| TxHundredInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 22.61&nbsp;ms<br>1.00<br>310&nbsp;KB<br>1.00 | 💾&nbsp;22.73&nbsp;ms<br>$\color{#d97706}{1.01}$<br>210&nbsp;KB<br>$\color{#16a34a}{0.68}$ | 24.15&nbsp;ms<br>1.07<br>311&nbsp;KB<br>1.00 | 25.00&nbsp;ms<br>1.11<br>431&nbsp;KB<br>1.39 | 🏆&nbsp;3.76&nbsp;ms<br>**0.17**<br>878&nbsp;KB<br>**2.83** | 23.28&nbsp;ms<br>1.03<br>282&nbsp;KB<br>0.91 | 26.30&nbsp;ms<br>1.16<br>694&nbsp;KB<br>**2.24** | 26.64&nbsp;ms<br>1.18<br>1817&nbsp;KB<br>**5.86** | 29.94&nbsp;ms<br>1.32<br>1511&nbsp;KB<br>**4.88** |
| TxBulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;5.19&nbsp;ms<br>1.00<br>340&nbsp;KB<br>1.00 | 5.33&nbsp;ms<br>$\color{#d97706}{1.03}$<br>342&nbsp;KB<br>$\color{#d97706}{1.00}$ | 17.08&nbsp;ms<br>**3.29**<br>6849&nbsp;KB<br>**20.14** | 6.38&nbsp;ms<br>1.23<br>846&nbsp;KB<br>**2.49** | 45.19&nbsp;ms<br>**8.71**<br>15.8&nbsp;MB<br>**47.71** | 17.11&nbsp;ms<br>**3.30**<br>6852&nbsp;KB<br>**20.15** | 24.64&nbsp;ms<br>**4.75**<br>3496&nbsp;KB<br>**10.28** | 11.83&nbsp;ms<br>**2.28**<br>1174&nbsp;KB<br>**3.45** | 28.52&nbsp;ms<br>**5.50**<br>7578&nbsp;KB<br>**22.29** |
| TxRollback | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 💾&nbsp;115.56&nbsp;ms<br>1.00<br>927&nbsp;KB<br>1.00 | 118.11&nbsp;ms<br>$\color{#d97706}{1.02}$<br>1068&nbsp;KB<br>$\color{#ea580c}{1.15}$ | 119.47&nbsp;ms<br>1.03<br>1531&nbsp;KB<br>1.65 | 118.21&nbsp;ms<br>1.02<br>2943&nbsp;KB<br>**3.18** | 🏆&nbsp;15.58&nbsp;ms<br>**0.13**<br>4240&nbsp;KB<br>**4.58** | 118.68&nbsp;ms<br>1.03<br>1746&nbsp;KB<br>1.88 | 137.67&nbsp;ms<br>1.19<br>4492&nbsp;KB<br>**4.85** | 137.51&nbsp;ms<br>1.19<br>10.5&nbsp;MB<br>**11.62** | 159.57&nbsp;ms<br>1.38<br>8740&nbsp;KB<br>**9.43** |

### Reproduce

```bash
# full cross-bench batch (~40 min, needs local PG / MySQL containers)
dotnet run --project tools/PalORM.PerfCli -c Release -- full
# cross-bench only
dotnet run --project tools/PalORM.PerfCli -c Release -- crossbench
```

Notes: all 9 groups come from a single batch, so ratios are only comparable within that batch; absolute times are not comparable across machines. The full 87-row detail (all 17 operations, two tiers, three dialects, with latency and memory percentages) is in [性能全量报告-2026-11-22.md](docs/性能全量报告-2026-11-22.md). PalORM self-regression (micro-benchmarks, concurrency, memory curves, stability) is measured separately by the PerfHub fixture, see [bench/perfhub/README.md](bench/perfhub/README.md).

## 🚀 Quick Start

> `.NET 11` (every package targets `net11.0` only) + SDK version pinned by `global.json` (currently `11.0.100`); the IDE must support Roslyn source generators. Native AOT publish additionally needs about 4 GB of memory (ILC compile time, not runtime).

```xml
<PackageReference Include="PalORM.PostgreSql" Version="6.9.0" />   <!-- or PalORM.MySql / PalORM.Sqlite -->
<PackageReference Include="PalORM.SourceGen" Version="6.9.0" PrivateAssets="all" />
```

The provider package brings `PalORM.Core` and the driver; the source generator must be referenced separately (`PalORM.SourceGen` with `PrivateAssets="all"` so it does not flow downstream). Six steps below, full patterns and decision tables in [Best practices](docs/最佳实践.md).

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

> A NULL column never yields null into a non-nullable property. With nullable reference types enabled and the property declared non-nullable, the driver throws `SqlNullValueException`; on an entity with `#nullable disable` the generated reader throws `InvalidOperationException` naming the column and property. Declare nullable (`string?`) for columns that may hold NULL.

### Create a session

```csharp
using var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = "Host=localhost;Username=user;Password=xxx;Database=mydb"
});
```

Reuse one `db` per scope (the third same-shape operation promotes a command reuse slot); `PreWarmAsync(options, count: 10)` warms the pool at startup.

For per-request sessions (web/API), prefer `DataSessionPool`: the session, connection, and command reuse slots survive across requests, and transient state is reset on return. In the local SQLite benchmark (PerfHub batch 2026-11-22), pooled single-key reads are about 40% lower latency and 61% lower allocation than creating a session per operation.

```csharp
var pool = DataSessionPool<PostgreSqlProvider>.Create(options, maxIdleSize: 16);

await using var db = await pool.RentAsync();   // DisposeAsync returns it to the pool
```

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

### Compiled queries (repeated shapes)

```csharp
using var plan = db.Compile<User>(
    """SELECT "Id", "Email" FROM "Users" WHERE "Id" = @p0""", "@p0");
var user = (await plan.ExecuteAsync([userId]))[0];
```

A plan fixes the SQL text and parameter template to a session-scoped object; repeated executions only write values and reuse the command. In the local SQLite BDN benchmark (2026-10-31), a single-row query is 22.6% faster and allocates 29.8% less than the optimized fluent path. SQL is executed verbatim, default filters do not apply; runtime parameter types must stay stable, and the plan must be disposed before the session.

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
| PostgreSQL | **18.6** (CI `postgres:18`) | Npgsql 11.0.0 | none; bulk writes need 9.3+, JSONB path queries 9.4+, advisory locks 9.5+ |
| MySQL | **8.4.11** (CI `mysql:8.4`) | MySqlConnector 3.0.0 | **8.0.19** (`UPDATE JOIN VALUES ROW`; older falls back to CASE WHEN); below 8.0 unsupported |
| SQLite | **3.54.1** (SQLite3MC 2.7.1) | Microsoft.Data.Sqlite.Core 11.0.0 | **3.35+** (`INSERT ... RETURNING`; bulk delete `json_each` needs 3.38+) |

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
