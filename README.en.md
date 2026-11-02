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
- **Compile-time safety net**: 46 Roslyn diagnostics catch missing keys, nullable tenant columns bypassing isolation, zero-value optimistic-lock baselines, and loop N+1 queries at build time, with errors jumping to the declaration site.
- **Full-pipeline Native AOT**: Publish verification and native execution pass on SQLite, PostgreSQL, and MySQL, and consumers need no TrimmerRootAssembly.
- **Floor-level performance**: Single-row CRUD matches hand-written ADO.NET, bulk writes automatically take the fastest per-dialect path (PG Binary COPY, MySQL LOAD DATA, SQLite row-prepared batches and json_each bulk delete), and a session pool is built in for request-per-session web services.
- **Enterprise cross-cutting**: Multi-tenant column isolation, optimistic locking, soft delete, audit interception, read/write splitting, advisory locks, resilient retry, and circuit breaking work out of the box.
- **SQL assets managed at compile time**: `[SqlFile]` embeds and validates .sql files, `[SqlTemplate]` turns interpolated SQL into constants, and AutoTagging injects "file:line member" locating comments automatically.
- **Operations and observability**: Health checks, a cross-dialect unified exception family, and OpenTelemetry tracing and metrics are built in.
- **Dialect-specific capabilities**: PostgreSQL gets UNNEST, advisory locks, LISTEN/NOTIFY, pgvector, and JSONB path queries; MySQL gets local_infile degradation and UPDATE JOIN; SQLite gets AES-256 encryption and automatic PRAGMA tuning.
- **Schema and seeding**: `MigrateAsync` runs three-dialect DDL in foreign-key topological order, `SeedAsync` seeds idempotently inside a transaction, and `ValidateSchemaAsync` reports missing columns against entity declarations.
- **Zero-dependency core**: PalORM.Core references no third-party NuGet packages, only BCL and ADO.NET abstractions.

> **Audit boundary**: `IQueryInterceptor` covers SELECT, QueryBuilder UPDATE, and `ExecuteAsync`. Entity Insert/Delete/Save, the Bulk family, stored procedures, and multi-result sets bypass interceptors. For write auditing, use database-level auditing or OpenTelemetry.

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

> **Test environment**: AMD Ryzen 9 8945HX · Windows 10 22H2 · .NET 11 RC2 · SQLite temp-file database (WAL + 64 MB cache + mmap) · PostgreSQL 18.6 / MySQL 8.4.11 local containers.
>
> **Data source**: 9 groups measured in the same run, 2026-11-02, batch `crossbench-20261102-110855.json`. The groups are PalORM / Dapper / Dapper.AOT / linq2db / EF Core / SqlSugar / FreeSql / RepoDb plus hand-written ADO.NET SQL as the baseline, each written in its ecosystem idiomatic form. Full report: [性能全量报告-2026-11-02.md](性能全量报告-2026-11-02.md).

### Overview

9 groups × three dialects × two tiers (2000 / 20000) × 17 operations (transactions on the smallest tier only) = **87 cells**, each operation averaged over two reverse-order passes. All ratios use hand-written ADO.NET SQL from the same batch as the [1.00] baseline; below 1 means faster.

| Group | Wins /87 | Median latency ratio | Median memory ratio |
|---|---:|---:|---:|
| ADO (baseline) | — | 1.00 | 1.00 |
| PalORM | 51 | 1.01 | 1.00 |
| Dapper.AOT | 11 | 1.08 | 1.15 |
| RepoDb | 6 | 1.14 | 1.46 |
| EF Core | 8 | 3.00 | 8.04 |
| Dapper | 8 | 1.08 | 1.61 |
| FreeSql | 2 | 1.43 | 2.01 |
| linq2db | 1 | 1.40 | 3.02 |
| SqlSugar | 0 | 2.18 | 3.52 |

- **PalORM**: wins 51/87 (59%), median latency ratio **1.01** (full range 0.46 ~ 1.16), median memory ratio **1.00**, no cell at 1.5x or worse.
- **Largest outliers**: SqlSugar UpsertBatch (SQLite 107.5x, MySQL 35.8x) and EF Core single-row Insert (66.5 ~ 67.2x, the per-operation DbContext shape).

### Operation comparison (latency [ratio] and memory [relative to baseline]; colors follow the six percentage bands)

#### SQLite (2000 and 20000)

| Operation | Tier | 🏆 latency / 💾 memory | ADO (baseline) | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆 PalORM<br>💾 Dapper.AOT | 8.4 µs [1.00]<br>2.1KB [1.00] | 6.0 µs [🟩0.71]<br>2.2KB [🔸1.03] | 6.8 µs [🟩0.82]<br>2.0KB [🔹0.97] | 10.4 µs [🟧1.24]<br>4.4KB **[🟥2.10]** | 135.1 µs **[🟥16.08]**<br>49.7KB **[🟥23.57]** | 10.9 µs **[🟥1.30]**<br>2.8KB **[🟥1.34]** | 44.1 µs **[🟥5.25]**<br>10.2KB **[🟥4.85]** | 15.8 µs **[🟥1.88]**<br>6.4KB **[🟥3.02]** | 84.0 µs **[🟥10.00]**<br>30.0KB **[🟥14.22]** |
| GetByKey | 20,000 | 🏆 ADO (baseline)<br>💾 Dapper.AOT | 6.5 µs [1.00]<br>2.1KB [1.00] | 6.5 µs [🔸1.01]<br>2.2KB [🔸1.03] | 7.1 µs [🟧1.10]<br>2.0KB [🔹0.97] | 8.9 µs **[🟥1.38]**<br>4.4KB **[🟥2.09]** | 80.5 µs **[🟥12.48]**<br>50.0KB **[🟥23.73]** | 7.7 µs [🟧1.19]<br>2.8KB **[🟥1.33]** | 34.7 µs **[🟥5.38]**<br>10.4KB **[🟥4.93]** | 16.6 µs **[🟥2.57]**<br>6.4KB **[🟥3.02]** | 75.9 µs **[🟥11.78]**<br>29.8KB **[🟥14.15]** |
| GetAllAsync | 2,000 | 🏆 ADO (baseline)<br>💾 PalORM | 991.9 µs [1.00]<br>308KB [1.00] | 1.02 ms [🔸1.02]<br>292KB [🔹0.95] | 1.15 ms [🟧1.16]<br>356KB [🟧1.15] | 1.17 ms [🟧1.18]<br>372KB [🟧1.21] | 1.39 ms **[🟥1.40]**<br>729KB **[🟥2.36]** | 1.40 ms **[🟥1.41]**<br>513KB **[🟥1.66]** | 1.39 ms **[🟥1.40]**<br>314KB [🔸1.02] | 1.38 ms **[🟥1.39]**<br>311KB [🔸1.01] | 3.19 ms **[🟥3.22]**<br>580KB **[🟥1.88]** |
| GetAllAsync | 20,000 | 🏆 ADO (baseline)<br>💾 PalORM | 10.42 ms [1.00]<br>3.3MB [1.00] | 10.46 ms [⚪1.00]<br>2.9MB [🟩0.89] | 12.07 ms [🟧1.16]<br>3.7MB [🟧1.14] | 12.13 ms [🟧1.16]<br>3.9MB [🟧1.19] | 14.53 ms **[🟥1.39]**<br>7.0MB **[🟥2.14]** | 14.41 ms **[🟥1.38]**<br>5.3MB **[🟥1.61]** | 14.94 ms **[🟥1.43]**<br>3.3MB [⚪1.00] | 14.73 ms **[🟥1.41]**<br>3.3MB [⚪1.00] | 33.34 ms **[🟥3.20]**<br>5.8MB **[🟥1.77]** |
| Insert | 2,000 | 🏆 ADO (baseline)<br>💾 PalORM | 17.5 µs [1.00]<br>2.8KB [1.00] | 17.6 µs [🔸1.01]<br>2.8KB [🔹0.98] | 19.1 µs [🔸1.09]<br>2.8KB [⚪1.00] | 19.6 µs [🟧1.13]<br>3.0KB [🔸1.05] | 1.16 ms **[🟥66.54]**<br>56.3KB **[🟥19.86]** | 18.6 µs [🔸1.06]<br>3.6KB [🟧1.27] | 32.2 µs **[🟥1.84]**<br>5.7KB **[🟥2.01]** | 1.14 ms **[🟥65.17]**<br>19.9KB **[🟥7.02]** | 1.10 ms **[🟥63.23]**<br>14.9KB **[🟥5.27]** |
| Insert | 20,000 | 🏆 ADO (baseline)<br>💾 PalORM | 18.0 µs [1.00]<br>2.8KB [1.00] | 18.4 µs [🔸1.02]<br>2.7KB [🔹0.98] | 18.9 µs [🔸1.05]<br>2.9KB [🔸1.04] | 20.5 µs [🟧1.14]<br>3.0KB [🔸1.08] | 1.21 ms **[🟥67.24]**<br>56.5KB **[🟥20.33]** | 19.6 µs [🔸1.09]<br>3.6KB **[🟥1.30]** | 32.7 µs **[🟥1.81]**<br>5.7KB **[🟥2.06]** | 1.10 ms **[🟥61.14]**<br>19.5KB **[🟥7.01]** | 1.16 ms **[🟥64.50]**<br>15.1KB **[🟥5.42]** |
| Update | 2,000 | 🏆 PalORM<br>💾 ADO (baseline) | 7.4 µs [1.00]<br>2.5KB [1.00] | 7.2 µs [🔹0.97]<br>2.9KB [🟧1.16] | 7.7 µs [🔸1.04]<br>2.8KB [🟧1.11] | 9.4 µs [🟧1.26]<br>4.0KB **[🟥1.60]** | 82.8 µs **[🟥11.18]**<br>53.6KB **[🟥21.31]** | 8.1 µs [🔸1.09]<br>3.3KB **[🟥1.30]** | 22.9 µs **[🟥3.10]**<br>7.4KB **[🟥2.94]** | 14.8 µs **[🟥2.01]**<br>19.7KB **[🟥7.85]** | 33.8 µs **[🟥4.57]**<br>14.6KB **[🟥5.79]** |
| Update | 20,000 | 🏆 PalORM<br>💾 ADO (baseline) | 8.6 µs [1.00]<br>2.5KB [1.00] | 7.9 µs [🔹0.92]<br>2.9KB [🟧1.16] | 8.0 µs [🔹0.94]<br>2.8KB [🟧1.11] | 9.9 µs [🟧1.15]<br>4.0KB **[🟥1.60]** | 87.5 µs **[🟥10.23]**<br>53.6KB **[🟥21.27]** | 10.6 µs [🟧1.23]<br>3.3KB **[🟥1.30]** | 23.4 µs **[🟥2.74]**<br>7.4KB **[🟥2.94]** | 16.6 µs **[🟥1.95]**<br>19.8KB **[🟥7.84]** | 36.3 µs **[🟥4.25]**<br>14.6KB **[🟥5.78]** |
| BulkInsert | 2,000 | 🏆 PalORM<br>💾 linq2db | 2.64 ms [1.00]<br>1.7MB [1.00] | 2.63 ms [⚪1.00]<br>1.7MB [⚪1.00] | 4.88 ms **[🟥1.85]**<br>4.3MB **[🟥2.48]** | 16.27 ms **[🟥6.17]**<br>2.6MB **[🟥1.54]** | 22.13 ms **[🟥8.39]**<br>17.3MB **[🟥10.07]** | 5.34 ms **[🟥2.03]**<br>4.3MB **[🟥2.48]** | 10.84 ms **[🟥4.11]**<br>6.5MB **[🟥3.79]** | 4.95 ms **[🟥1.88]**<br>1.3MB [🟩0.73] | 7.49 ms **[🟥2.84]**<br>4.4MB **[🟥2.57]** |
| BulkInsert | 20,000 | 🏆 ADO (baseline)<br>💾 linq2db | 32.08 ms [1.00]<br>17.2MB [1.00] | 36.14 ms [🟧1.13]<br>17.2MB [⚪1.00] | 58.03 ms **[🟥1.81]**<br>42.6MB **[🟥2.48]** | 180.66 ms **[🟥5.63]**<br>26.2MB **[🟥1.53]** | 281.84 ms **[🟥8.78]**<br>172.2MB **[🟥10.03]** | 55.08 ms **[🟥1.72]**<br>42.6MB **[🟥2.48]** | 141.77 ms **[🟥4.42]**<br>66.2MB **[🟥3.85]** | 54.07 ms **[🟥1.69]**<br>11.0MB **[🟢0.64]** | 90.32 ms **[🟥2.82]**<br>44.9MB **[🟥2.62]** |
| BulkUpdate | 2,000 | 🏆 PalORM<br>💾 ADO (baseline) | 2.46 ms [1.00]<br>1.7MB [1.00] | 2.37 ms [🔹0.96]<br>1.7MB [⚪1.00] | 8.60 ms **[🟥3.49]**<br>5.5MB **[🟥3.25]** | 7.61 ms **[🟥3.09]**<br>5.2MB **[🟥3.06]** | 27.41 ms **[🟥11.14]**<br>17.9MB **[🟥10.59]** | 9.37 ms **[🟥3.81]**<br>6.3MB **[🟥3.75]** | 13.48 ms **[🟥5.48]**<br>7.0MB **[🟥4.15]** | 23.37 ms **[🟥9.50]**<br>38.5MB **[🟥22.73]** | 6.25 ms **[🟥2.54]**<br>4.4MB **[🟥2.59]** |
| BulkUpdate | 20,000 | 🏆 PalORM<br>💾 PalORM | 28.39 ms [1.00]<br>17.0MB [1.00] | 27.66 ms [🔹0.97]<br>17.0MB [⚪1.00] | 99.50 ms **[🟥3.50]**<br>55.2MB **[🟥3.25]** | 83.54 ms **[🟥2.94]**<br>51.9MB **[🟥3.05]** | 357.82 ms **[🟥12.60]**<br>178.8MB **[🟥10.52]** | 102.56 ms **[🟥3.61]**<br>63.6MB **[🟥3.74]** | 150.44 ms **[🟥5.30]**<br>73.4MB **[🟥4.32]** | 246.77 ms **[🟥8.69]**<br>385.3MB **[🟥22.67]** | 81.36 ms **[🟥2.87]**<br>44.8MB **[🟥2.63]** |
| BulkDelete | 2,000 | 🏆 PalORM<br>💾 ADO (baseline) | 848.1 µs [1.00]<br>197KB [1.00] | 832.0 µs [🔹0.98]<br>199KB [🔸1.01] | 22.52 ms **[🟥26.55]**<br>1.6MB **[🟥8.13]** | 19.46 ms **[🟥22.94]**<br>3.0MB **[🟥15.69]** | 12.00 ms **[🟥14.15]**<br>2.5MB **[🟥12.81]** | 22.23 ms **[🟥26.22]**<br>1.6MB **[🟥8.12]** | 1.60 ms **[🟥1.88]**<br>569KB **[🟥2.89]** | 2.56 ms **[🟥3.01]**<br>271KB **[🟥1.37]** | 2.61 ms **[🟥3.07]**<br>354KB **[🟥1.80]** |
| BulkDelete | 20,000 | 🏆 PalORM<br>💾 ADO (baseline) | 10.21 ms [1.00]<br>2.0MB [1.00] | 9.51 ms [🔹0.93]<br>2.0MB [⚪1.00] | 239.71 ms **[🟥23.48]**<br>15.7MB **[🟥7.72]** | 215.71 ms **[🟥21.13]**<br>31.6MB **[🟥15.58]** | 130.08 ms **[🟥12.74]**<br>24.6MB **[🟥12.14]** | 245.81 ms **[🟥24.08]**<br>15.7MB **[🟥7.73]** | 19.65 ms **[🟥1.92]**<br>6.0MB **[🟥2.94]** | 18.08 ms **[🟥1.77]**<br>2.7MB **[🟥1.34]** | 41.26 ms **[🟥4.04]**<br>4.2MB **[🟥2.09]** |
| UpsertBatch | 2,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 2.44 ms [1.00]<br>1.7MB [1.00] | 2.51 ms [🔸1.03]<br>1.7MB [🔸1.01] | 111.43 ms **[🟥45.70]**<br>8.0MB **[🟥4.70]** | 18.67 ms **[🟥7.66]**<br>11.5MB **[🟥6.79]** | 19.31 ms **[🟥7.92]**<br>5.4MB **[🟥3.17]** | 4.18 ms **[🟥1.71]**<br>4.2MB **[🟥2.50]** | 3.68 ms **[🟥1.51]**<br>2.5MB **[🟥1.47]** | 22.90 ms **[🟥9.39]**<br>42.8MB **[🟥25.23]** | 262.15 ms **[🟥107.53]**<br>219.9MB **[🟥129.74]** |
| UpsertBatch | 20,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 28.14 ms [1.00]<br>17.0MB [1.00] | 29.04 ms [🔸1.03]<br>17.2MB [🔸1.01] | 1.17 s **[🟥41.74]**<br>79.9MB **[🟥4.70]** | 198.99 ms **[🟥7.07]**<br>115.1MB **[🟥6.77]** | 1.60 s **[🟥56.72]**<br>57.0MB **[🟥3.35]** | 50.39 ms **[🟥1.79]**<br>42.4MB **[🟥2.49]** | 41.87 ms **[🟥1.49]**<br>25.8MB **[🟥1.52]** | 247.59 ms **[🟥8.80]**<br>427.7MB **[🟥25.16]** | 2.78 s **[🟥98.72]**<br>2365.1MB **[🟥139.13]** |

#### MySQL (2000 and 20000)

| Operation | Tier | 🏆 latency / 💾 memory | ADO (baseline) | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆 Dapper.AOT<br>💾 ADO (baseline) | 205.5 µs [1.00]<br>4.6KB [1.00] | 220.2 µs [🔸1.07]<br>5.1KB [🟧1.11] | 204.9 µs [⚪1.00]<br>5.3KB [🟧1.16] | 246.9 µs [🟧1.20]<br>8.6KB **[🟥1.90]** | 742.7 µs **[🟥3.61]**<br>94.9KB **[🟥20.85]** | 216.3 µs [🔸1.05]<br>5.9KB **[🟥1.30]** | 456.1 µs **[🟥2.22]**<br>16.8KB **[🟥3.68]** | 272.3 µs **[🟥1.32]**<br>25.7KB **[🟥5.65]** | 496.1 µs **[🟥2.41]**<br>36.4KB **[🟥8.00]** |
| GetByKey | 20,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 183.6 µs [1.00]<br>4.5KB [1.00] | 196.7 µs [🔸1.07]<br>5.0KB [🟧1.11] | 198.1 µs [🔸1.08]<br>5.2KB [🟧1.17] | 204.3 µs [🟧1.11]<br>8.5KB **[🟥1.90]** | 662.5 µs **[🟥3.61]**<br>94.7KB **[🟥21.13]** | 188.2 µs [🔸1.03]<br>5.8KB [🟧1.29] | 420.9 µs **[🟥2.29]**<br>16.7KB **[🟥3.73]** | 251.1 µs **[🟥1.37]**<br>25.8KB **[🟥5.76]** | 444.5 µs **[🟥2.42]**<br>36.2KB **[🟥8.07]** |
| GetAllAsync | 2,000 | 🏆 ADO (baseline)<br>💾 PalORM | 1.16 ms [1.00]<br>398KB [1.00] | 1.18 ms [🔸1.02]<br>383KB [🔹0.96] | 1.18 ms [🔸1.02]<br>400KB [⚪1.00] | 1.28 ms [🟧1.10]<br>401KB [🔸1.01] | 3.48 ms **[🟥3.01]**<br>4.4MB **[🟥11.30]** | 1.24 ms [🔸1.07]<br>447KB [🟧1.12] | 1.55 ms **[🟥1.34]**<br>407KB [🔸1.02] | 2.89 ms **[🟥2.49]**<br>4.0MB **[🟥10.19]** | 1.75 ms **[🟥1.51]**<br>419KB [🔸1.05] |
| GetAllAsync | 20,000 | 🏆 Dapper.AOT<br>💾 PalORM | 9.64 ms [1.00]<br>4.0MB [1.00] | 9.78 ms [🔸1.02]<br>3.7MB [🔹0.91] | 9.41 ms [🔹0.98]<br>4.0MB [⚪1.00] | 10.11 ms [🔸1.05]<br>4.0MB [⚪1.00] | 32.49 ms **[🟥3.37]**<br>46.3MB **[🟥11.44]** | 10.26 ms [🔸1.06]<br>4.5MB [🟧1.11] | 10.75 ms [🟧1.12]<br>4.1MB [⚪1.00] | 29.78 ms **[🟥3.09]**<br>43.2MB **[🟥10.68]** | 12.88 ms **[🟥1.34]**<br>4.1MB [🔸1.01] |
| Insert | 2,000 | 🏆 RepoDb<br>💾 Dapper.AOT | 4.43 ms [1.00]<br>17.9KB [1.00] | 4.22 ms [🔹0.95]<br>17.3KB [🔹0.97] | 4.07 ms [🔹0.92]<br>16.4KB [🔹0.92] | 4.06 ms [🔹0.92]<br>17.7KB [🔹0.99] | 6.02 ms **[🟥1.36]**<br>122KB **[🟥6.84]** | 4.20 ms [🔹0.95]<br>17.0KB [🔹0.95] | 4.43 ms [⚪1.00]<br>23.2KB **[🟥1.30]** | 4.12 ms [🔹0.93]<br>37.2KB **[🟥2.08]** | 4.42 ms [⚪1.00]<br>32.1KB **[🟥1.79]** |
| Insert | 20,000 | 🏆 RepoDb<br>💾 Dapper.AOT | 3.87 ms [1.00]<br>16.2KB [1.00] | 3.97 ms [🔸1.03]<br>18.4KB [🟧1.13] | 3.77 ms [🔹0.97]<br>15.3KB [🔹0.95] | 3.73 ms [🔹0.96]<br>16.5KB [🔸1.02] | 5.44 ms **[🟥1.40]**<br>121KB **[🟥7.43]** | 3.87 ms [⚪1.00]<br>15.9KB [🔹0.98] | 4.16 ms [🔸1.08]<br>22.8KB **[🟥1.41]** | 4.00 ms [🔸1.03]<br>36.3KB **[🟥2.24]** | 4.17 ms [🔸1.08]<br>29.0KB **[🟥1.79]** |
| Update | 2,000 | 🏆 ADO (baseline)<br>💾 Dapper.AOT | 201.3 µs [1.00]<br>5.2KB [1.00] | 230.5 µs [🟧1.15]<br>5.8KB [🟧1.12] | 211.5 µs [🔸1.05]<br>4.7KB [🔹0.91] | 243.8 µs [🟧1.21]<br>7.7KB **[🟥1.47]** | 745.3 µs **[🟥3.70]**<br>93.2KB **[🟥17.85]** | 216.7 µs [🔸1.08]<br>5.3KB [🔸1.01] | 457.3 µs **[🟥2.27]**<br>13.3KB **[🟥2.55]** | 272.9 µs **[🟥1.36]**<br>31.0KB **[🟥5.94]** | 439.4 µs **[🟥2.18]**<br>20.4KB **[🟥3.90]** |
| Update | 20,000 | 🏆 ADO (baseline)<br>💾 Dapper.AOT | 188.9 µs [1.00]<br>5.2KB [1.00] | 189.7 µs [⚪1.00]<br>5.7KB [🟧1.10] | 198.1 µs [🔸1.05]<br>4.7KB [🔹0.91] | 209.3 µs [🟧1.11]<br>7.6KB **[🟥1.46]** | 685.4 µs **[🟥3.63]**<br>93.0KB **[🟥17.93]** | 196.6 µs [🔸1.04]<br>5.2KB [⚪1.00] | 400.6 µs **[🟥2.12]**<br>13.2KB **[🟥2.54]** | 255.0 µs **[🟥1.35]**<br>31.0KB **[🟥5.97]** | 423.3 µs **[🟥2.24]**<br>20.3KB **[🟥3.92]** |
| BulkInsert | 2,000 | 🏆 PalORM<br>💾 PalORM | 14.57 ms [1.00]<br>504KB [1.00] | 12.30 ms [🟩0.84]<br>486KB [🔹0.96] | 26.00 ms **[🟥1.78]**<br>6.7MB **[🟥13.51]** | 13.20 ms [🔹0.91]<br>488KB [🔹0.97] | 65.92 ms **[🟥4.52]**<br>20.0MB **[🟥40.60]** | 30.22 ms **[🟥2.07]**<br>6.7MB **[🟥13.53]** | 18.63 ms [🟧1.28]<br>3.2MB **[🟥6.58]** | 22.96 ms **[🟥1.58]**<br>3.3MB **[🟥6.66]** | 15.02 ms [🔸1.03]<br>2.3MB **[🟥4.73]** |
| BulkInsert | 20,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 57.08 ms [1.00]<br>4.4MB [1.00] | 58.60 ms [🔸1.03]<br>4.4MB [⚪1.00] | 255.84 ms **[🟥4.48]**<br>66.5MB **[🟥15.01]** | 57.66 ms [🔸1.01]<br>4.4MB [⚪1.00] | 560.12 ms **[🟥9.81]**<br>198.5MB **[🟥44.78]** | 258.43 ms **[🟥4.53]**<br>66.5MB **[🟥15.00]** | 120.52 ms **[🟥2.11]**<br>33.2MB **[🟥7.49]** | 261.31 ms **[🟥4.58]**<br>31.9MB **[🟥7.19]** | 101.89 ms **[🟥1.79]**<br>24.6MB **[🟥5.55]** |
| BulkUpdate | 2,000 | 🏆 FreeSql<br>💾 RepoDb | 10.82 ms [1.00]<br>4.6MB [1.00] | 10.45 ms [🔹0.97]<br>3.7MB [🟩0.80] | 11.72 ms [🔸1.08]<br>6.5MB **[🟥1.41]** | 175.45 ms **[🟥16.22]**<br>950KB **[🟢0.20]** | 130.18 ms **[🟥12.04]**<br>35.8MB **[🟥7.81]** | 12.35 ms [🟧1.14]<br>6.5MB **[🟥1.41]** | 10.31 ms [🔹0.95]<br>2.1MB **[🟢0.45]** | 30.51 ms **[🟥2.82]**<br>7.6MB **[🟥1.65]** | 11.98 ms [🟧1.11]<br>2.3MB **[🟢0.50]** |
| BulkUpdate | 20,000 | 🏆 ADO (baseline)<br>💾 RepoDb | 82.60 ms [1.00]<br>14.6MB [1.00] | 83.49 ms [🔸1.01]<br>13.7MB [🔹0.94] | 144.20 ms **[🟥1.75]**<br>64.5MB **[🟥4.42]** | 376.72 ms **[🟥4.56]**<br>5.2MB **[🟢0.36]** | 1.29 s **[🟥15.64]**<br>358.5MB **[🟥24.56]** | 142.38 ms **[🟥1.72]**<br>64.5MB **[🟥4.42]** | 92.41 ms [🟧1.12]<br>21.8MB **[🟥1.49]** | 296.07 ms **[🟥3.58]**<br>75.8MB **[🟥5.19]** | 102.99 ms [🟧1.25]<br>23.9MB **[🟥1.63]** |
| BulkDelete | 2,000 | 🏆 PalORM<br>💾 SqlSugar | 12.81 ms [1.00]<br>884KB [1.00] | 12.71 ms [🔹0.99]<br>810KB [🔹0.92] | 13.00 ms [🔸1.01]<br>1.3MB **[🟥1.56]** | 27.85 ms **[🟥2.17]**<br>19.3MB **[🟥22.34]** | 25.15 ms **[🟥1.96]**<br>3.7MB **[🟥4.28]** | 12.94 ms [🔸1.01]<br>1.3MB **[🟥1.56]** | 18.31 ms **[🟥1.43]**<br>616KB **[🟢0.70]** | 14.23 ms [🟧1.11]<br>717KB [🟩0.81] | 13.03 ms [🔸1.02]<br>379KB **[🟢0.43]** |
| BulkDelete | 20,000 | 🏆 PalORM<br>💾 PalORM | 73.08 ms [1.00]<br>8.4MB [1.00] | 68.18 ms [🔹0.93]<br>3.5MB **[🟢0.41]** | 75.55 ms [🔸1.03]<br>13.2MB **[🟥1.57]** | 228.94 ms **[🟥3.13]**<br>193.5MB **[🟥22.94]** | 282.26 ms **[🟥3.86]**<br>37.0MB **[🟥4.39]** | 77.97 ms [🔸1.07]<br>13.2MB **[🟥1.57]** | 187.25 ms **[🟥2.56]**<br>6.3MB [🟩0.74] | 76.04 ms [🔸1.04]<br>6.9MB [🟩0.82] | 167.45 ms **[🟥2.29]**<br>4.6MB **[🟢0.54]** |
| UpsertBatch | 2,000 | 🏆 PalORM<br>💾 RepoDb | 9.06 ms [1.00]<br>2.9MB [1.00] | 8.59 ms [🔹0.95]<br>2.5MB [🟩0.85] | 12.32 ms **[🟥1.36]**<br>6.6MB **[🟥2.28]** | 167.88 ms **[🟥18.53]**<br>995KB **[🟢0.34]** | 16.39 ms **[🟥1.81]**<br>10.2MB **[🟥3.51]** | 12.02 ms **[🟥1.33]**<br>6.6MB **[🟥2.28]** | 9.16 ms [🔸1.01]<br>2.5MB [🟩0.86] | 32.36 ms **[🟥3.57]**<br>7.9MB **[🟥2.73]** | 264.23 ms **[🟥29.17]**<br>219.5MB **[🟥75.72]** |
| UpsertBatch | 20,000 | 🏆 ADO (baseline)<br>💾 RepoDb | 74.50 ms [1.00]<br>12.9MB [1.00] | 81.44 ms [🔸1.09]<br>12.6MB [🔹0.98] | 140.02 ms **[🟥1.88]**<br>66.2MB **[🟥5.15]** | 391.70 ms **[🟥5.26]**<br>5.5MB **[🟢0.43]** | 182.47 ms **[🟥2.45]**<br>103.4MB **[🟥8.04]** | 145.07 ms **[🟥1.95]**<br>66.2MB **[🟥5.15]** | 128.93 ms **[🟥1.73]**<br>25.8MB **[🟥2.01]** | 313.72 ms **[🟥4.21]**<br>79.1MB **[🟥6.16]** | 2.67 s **[🟥35.77]**<br>2359.3MB **[🟥183.57]** |

#### PostgreSQL (2000 and 20000)

| Operation | Tier | 🏆 latency / 💾 memory | ADO (baseline) | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆 Dapper<br>💾 Dapper.AOT | 202.6 µs [1.00]<br>3.0KB [1.00] | 201.9 µs [⚪1.00]<br>3.4KB [🟧1.15] | 200.8 µs [🔹0.99]<br>2.7KB [🟩0.89] | 212.4 µs [🔸1.05]<br>5.8KB **[🟥1.95]** | 304.5 µs **[🟥1.50]**<br>44.0KB **[🟥14.73]** | 190.9 µs [🔹0.94]<br>3.2KB [🔸1.08] | 267.9 µs **[🟥1.32]**<br>12.0KB **[🟥4.03]** | 221.4 µs [🔸1.09]<br>7.7KB **[🟥2.57]** | 321.8 µs **[🟥1.59]**<br>31.4KB **[🟥10.51]** |
| GetByKey | 20,000 | 🏆 ADO (baseline)<br>💾 Dapper.AOT | 200.3 µs [1.00]<br>3.0KB [1.00] | 203.1 µs [🔸1.01]<br>3.4KB [🟧1.16] | 202.3 µs [🔸1.01]<br>2.7KB [🟩0.89] | 217.8 µs [🔸1.09]<br>5.8KB **[🟥1.96]** | 304.7 µs **[🟥1.52]**<br>44.0KB **[🟥14.79]** | 207.9 µs [🔸1.04]<br>3.3KB [🟧1.10] | 264.9 µs **[🟥1.32]**<br>12.1KB **[🟥4.05]** | 217.9 µs [🔸1.09]<br>7.7KB **[🟥2.58]** | 323.5 µs **[🟥1.62]**<br>31.4KB **[🟥10.56]** |
| GetAllAsync | 2,000 | 🏆 ADO (baseline)<br>💾 PalORM | 629.3 µs [1.00]<br>239KB [1.00] | 633.8 µs [🔸1.01]<br>224KB [🔹0.94] | 673.6 µs [🔸1.07]<br>239KB [⚪1.00] | 634.2 µs [🔸1.01]<br>240KB [🔸1.01] | 958.5 µs **[🟥1.52]**<br>652KB **[🟥2.73]** | 635.7 µs [🔸1.01]<br>442KB **[🟥1.85]** | 736.5 µs [🟧1.17]<br>244KB [🔸1.02] | 697.8 µs [🟧1.11]<br>241KB [🔸1.01] | 915.2 µs **[🟥1.45]**<br>255KB [🔸1.06] |
| GetAllAsync | 20,000 | 🏆 ADO (baseline)<br>💾 PalORM | 5.04 ms [1.00]<br>2.5MB [1.00] | 5.09 ms [🔸1.01]<br>2.2MB [🟩0.86] | 5.11 ms [🔸1.01]<br>2.5MB [⚪1.00] | 5.22 ms [🔸1.04]<br>2.5MB [⚪1.00] | 7.96 ms **[🟥1.58]**<br>6.2MB **[🟥2.48]** | 5.24 ms [🔸1.04]<br>4.5MB **[🟥1.79]** | 5.90 ms [🟧1.17]<br>2.5MB [⚪1.00] | 5.60 ms [🟧1.11]<br>2.5MB [⚪1.00] | 7.31 ms **[🟥1.45]**<br>2.5MB [🔸1.01] |
| Insert | 2,000 | 🏆 Dapper<br>💾 Dapper.AOT | 1.35 ms [1.00]<br>7.4KB [1.00] | 1.37 ms [🔸1.01]<br>7.4KB [⚪1.00] | 1.40 ms [🔸1.04]<br>7.0KB [🔹0.94] | 1.34 ms [🔹0.99]<br>7.7KB [🔸1.04] | 1.49 ms [🟧1.10]<br>51.9KB **[🟥6.97]** | 1.34 ms [🔹0.99]<br>7.1KB [🔹0.96] | 1.42 ms [🔸1.05]<br>10.9KB **[🟥1.46]** | 1.39 ms [🔸1.03]<br>21.5KB **[🟥2.89]** | 1.45 ms [🔸1.07]<br>18.6KB **[🟥2.50]** |
| Insert | 20,000 | 🏆 PalORM<br>💾 Dapper.AOT | 1.47 ms [1.00]<br>7.8KB [1.00] | 1.36 ms [🔹0.93]<br>7.5KB [🔹0.96] | 1.38 ms [🔹0.94]<br>6.6KB [🟩0.85] | 1.51 ms [🔸1.03]<br>8.2KB [🔸1.05] | 1.54 ms [🔸1.05]<br>54.1KB **[🟥6.94]** | 1.48 ms [🔸1.01]<br>7.5KB [🔹0.96] | 1.52 ms [🔸1.04]<br>11.2KB **[🟥1.43]** | 1.45 ms [🔹0.99]<br>21.8KB **[🟥2.79]** | 1.65 ms [🟧1.12]<br>19.3KB **[🟥2.48]** |
| Update | 2,000 | 🏆 PalORM<br>💾 Dapper.AOT | 1.38 ms [1.00]<br>7.2KB [1.00] | 1.34 ms [🔹0.98]<br>7.7KB [🔸1.07] | 1.37 ms [⚪1.00]<br>6.5KB [🔹0.91] | 1.38 ms [⚪1.00]<br>9.4KB **[🟥1.30]** | 1.53 ms [🟧1.11]<br>52.8KB **[🟥7.32]** | 1.40 ms [🔸1.02]<br>7.1KB [🔹0.99] | 1.44 ms [🔸1.05]<br>13.0KB **[🟥1.81]** | 1.37 ms [⚪1.00]<br>25.8KB **[🟥3.58]** | 1.51 ms [🟧1.10]<br>21.2KB **[🟥2.93]** |
| Update | 20,000 | 🏆 Dapper.AOT<br>💾 Dapper.AOT | 1.53 ms [1.00]<br>7.7KB [1.00] | 1.43 ms [🔹0.94]<br>8.0KB [🔸1.05] | 1.43 ms [🔹0.94]<br>6.8KB [🟩0.88] | 1.46 ms [🔹0.96]<br>9.6KB [🟧1.25] | 1.63 ms [🔸1.07]<br>52.5KB **[🟥6.83]** | 1.45 ms [🔹0.95]<br>7.2KB [🔹0.94] | 1.47 ms [🔹0.96]<br>13.3KB **[🟥1.73]** | 1.54 ms [🔸1.01]<br>25.4KB **[🟥3.30]** | 1.64 ms [🔸1.08]<br>21.7KB **[🟥2.82]** |
| BulkInsert | 2,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 4.83 ms [1.00]<br>341KB [1.00] | 4.99 ms [🔸1.03]<br>344KB [🔸1.01] | 19.11 ms **[🟥3.96]**<br>6.7MB **[🟥20.10]** | 5.41 ms [🟧1.12]<br>847KB **[🟥2.48]** | 46.54 ms **[🟥9.64]**<br>15.8MB **[🟥47.33]** | 17.84 ms **[🟥3.69]**<br>6.7MB **[🟥20.10]** | 13.33 ms **[🟥2.76]**<br>3.8MB **[🟥11.55]** | 13.20 ms **[🟥2.73]**<br>1.2MB **[🟥3.56]** | 27.29 ms **[🟥5.65]**<br>7.4MB **[🟥22.30]** |
| BulkInsert | 20,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 20.56 ms [1.00]<br>3.3MB [1.00] | 21.23 ms [🔸1.03]<br>3.3MB [⚪1.00] | 203.70 ms **[🟥9.91]**<br>66.9MB **[🟥20.54]** | 23.02 ms [🟧1.12]<br>8.0MB **[🟥2.46]** | 484.26 ms **[🟥23.55]**<br>152.4MB **[🟥46.79]** | 219.39 ms **[🟥10.67]**<br>67.0MB **[🟥20.56]** | 113.97 ms **[🟥5.54]**<br>39.6MB **[🟥12.15]** | 123.02 ms **[🟥5.98]**<br>10.3MB **[🟥3.16]** | 298.59 ms **[🟥14.52]**<br>75.5MB **[🟥23.17]** |
| BulkUpdate | 2,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 5.54 ms [1.00]<br>419KB [1.00] | 6.35 ms [🟧1.15]<br>425KB [🔸1.01] | 17.76 ms **[🟥3.21]**<br>6.5MB **[🟥15.96]** | 54.52 ms **[🟥9.85]**<br>3.8MB **[🟥9.28]** | 61.45 ms **[🟥11.10]**<br>16.0MB **[🟥38.98]** | 17.33 ms **[🟥3.13]**<br>6.5MB **[🟥15.97]** | 15.23 ms **[🟥2.75]**<br>2.7MB **[🟥6.64]** | 13.04 ms **[🟥2.35]**<br>2.1MB **[🟥5.09]** | 37.61 ms **[🟥6.79]**<br>11.8MB **[🟥28.86]** |
| BulkUpdate | 20,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 69.89 ms [1.00]<br>4.1MB [1.00] | 71.32 ms [🔸1.02]<br>4.1MB [⚪1.00] | 237.65 ms **[🟥3.40]**<br>65.4MB **[🟥15.99]** | 517.93 ms **[🟥7.41]**<br>37.9MB **[🟥9.27]** | 692.38 ms **[🟥9.91]**<br>154.4MB **[🟥37.75]** | 230.11 ms **[🟥3.29]**<br>65.4MB **[🟥15.99]** | 134.12 ms **[🟥1.92]**<br>28.3MB **[🟥6.92]** | 174.71 ms **[🟥2.50]**<br>21.2MB **[🟥5.18]** | 393.38 ms **[🟥5.63]**<br>120.4MB **[🟥29.44]** |
| BulkDelete | 2,000 | 🏆 PalORM<br>💾 PalORM | 3.65 ms [1.00]<br>784KB [1.00] | 2.47 ms **[🟢0.68]**<br>89.7KB **[🟢0.11]** | 3.94 ms [🔸1.08]<br>1.3MB **[🟥1.71]** | 19.67 ms **[🟥5.39]**<br>19.2MB **[🟥25.02]** | 4.05 ms [🟧1.11]<br>177KB **[🟢0.23]** | 4.38 ms [🟧1.20]<br>1.3MB **[🟥1.71]** | 5.70 ms **[🟥1.56]**<br>571KB [🟩0.73] | 4.67 ms [🟧1.28]<br>264KB **[🟢0.34]** | 4.28 ms [🟧1.17]<br>347KB **[🟢0.44]** |
| BulkDelete | 20,000 | 🏆 PalORM<br>💾 PalORM | 27.82 ms [1.00]<br>7.6MB [1.00] | 12.71 ms **[🟢0.46]**<br>820KB **[🟢0.11]** | 31.28 ms [🟧1.12]<br>13.0MB **[🟥1.71]** | 193.38 ms **[🟥6.95]**<br>192.5MB **[🟥25.27]** | 44.31 ms **[🟥1.59]**<br>1.7MB **[🟢0.23]** | 36.00 ms [🟧1.29]<br>13.0MB **[🟥1.71]** | 61.58 ms **[🟥2.21]**<br>6.0MB [🟩0.78] | 46.82 ms **[🟥1.68]**<br>2.7MB **[🟢0.35]** | 29.30 ms [🔸1.05]<br>4.3MB **[🟢0.57]** |
| UpsertBatch | 2,000 | 🏆 EF Core<br>💾 PalORM | 15.49 ms [1.00]<br>2.7MB [1.00] | 7.75 ms **[🟢0.50]**<br>446KB **[🟢0.16]** | 19.62 ms [🟧1.27]<br>6.7MB **[🟥2.47]** | 24.88 ms **[🟥1.61]**<br>5.8MB **[🟥2.12]** | 3.02 ms **[🟢0.20]**<br>3.0MB [🔸1.09] | 19.63 ms [🟧1.27]<br>6.7MB **[🟥2.47]** | 16.29 ms [🔸1.05]<br>2.7MB [🔸1.01] | 12.72 ms [🟩0.82]<br>2.1MB [🟩0.77] | 271.03 ms **[🟥17.50]**<br>219.2MB **[🟥80.74]** |
| UpsertBatch | 20,000 | 🏆 EF Core<br>💾 PalORM | 143.81 ms [1.00]<br>11.6MB [1.00] | 95.36 ms **[🟢0.66]**<br>4.3MB **[🟢0.37]** | 250.37 ms **[🟥1.74]**<br>67.0MB **[🟥5.78]** | 267.79 ms **[🟥1.86]**<br>57.4MB **[🟥4.95]** | 55.78 ms **[🟢0.39]**<br>29.1MB **[🟥2.51]** | 231.10 ms **[🟥1.61]**<br>67.0MB **[🟥5.78]** | 164.07 ms [🟧1.14]<br>28.4MB **[🟥2.45]** | 195.67 ms **[🟥1.36]**<br>21.2MB **[🟥1.83]** | 4.01 s **[🟥27.90]**<br>2360.7MB **[🟥203.64]** |

### Transaction comparison (2000 tier only)

#### SQLite

| Operation | Tier | 🏆 latency / 💾 memory | ADO (baseline) | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆 Dapper.AOT<br>💾 ADO (baseline) | 20.4 µs [1.00]<br>3.9KB [1.00] | 20.3 µs [⚪1.00]<br>4.4KB [🟧1.11] | 19.8 µs [🔹0.97]<br>4.1KB [🔸1.03] | 21.6 µs [🔸1.06]<br>4.2KB [🔸1.07] | 1.17 ms **[🟥57.30]**<br>59.6KB **[🟥15.12]** | 21.4 µs [🔸1.05]<br>4.7KB [🟧1.20] | 33.9 µs **[🟥1.67]**<br>7.0KB **[🟥1.78]** | 1.11 ms **[🟥54.60]**<br>20.9KB **[🟥5.31]** | 1.14 ms **[🟥55.99]**<br>16.3KB **[🟥4.15]** |
| TxHundredInserts | 2,000 | 🏆 PalORM<br>💾 PalORM | 195.0 µs [1.00]<br>137KB [1.00] | 188.9 µs [🔹0.97]<br>92.8KB **[🟢0.68]** | 438.0 µs **[🟥2.25]**<br>280KB **[🟥2.04]** | 582.3 µs **[🟥2.99]**<br>295KB **[🟥2.16]** | 1.99 ms **[🟥10.20]**<br>932KB **[🟥6.81]** | 283.9 µs **[🟥1.46]**<br>220KB **[🟥1.61]** | 1.58 ms **[🟥8.09]**<br>541KB **[🟥3.95]** | 1.95 ms **[🟥10.00]**<br>1.6MB **[🟥12.02]** | 3.30 ms **[🟥16.93]**<br>1.2MB **[🟥8.75]** |
| TxBulkInsert | 2,000 | 🏆 PalORM<br>💾 linq2db | 2.73 ms [1.00]<br>1.7MB [1.00] | 2.62 ms [🔹0.96]<br>1.7MB [⚪1.00] | 4.87 ms **[🟥1.78]**<br>4.3MB **[🟥2.48]** | 16.31 ms **[🟥5.97]**<br>2.6MB **[🟥1.54]** | 23.17 ms **[🟥8.48]**<br>17.3MB **[🟥10.07]** | 4.88 ms **[🟥1.79]**<br>4.3MB **[🟥2.48]** | 7.80 ms **[🟥2.85]**<br>3.5MB **[🟥2.02]** | 4.04 ms **[🟥1.48]**<br>1.2MB [🟩0.73] | 8.58 ms **[🟥3.14]**<br>5.4MB **[🟥3.16]** |
| TxRollback | 2,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 606.7 µs [1.00]<br>435KB [1.00] | 654.4 µs [🔸1.08]<br>437KB [🔸1.01] | 2.07 ms **[🟥3.40]**<br>1.4MB **[🟥3.24]** | 2.72 ms **[🟥4.48]**<br>2.0MB **[🟥4.60]** | 5.45 ms **[🟥8.98]**<br>4.5MB **[🟥10.58]** | 2.27 ms **[🟥3.74]**<br>1.6MB **[🟥3.74]** | 7.82 ms **[🟥12.88]**<br>3.5MB **[🟥8.16]** | 5.57 ms **[🟥9.18]**<br>9.6MB **[🟥22.68]** | 13.98 ms **[🟥23.04]**<br>7.1MB **[🟥16.63]** |
| EnumInserts | 2,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 134.8 µs [1.00]<br>59.1KB [1.00] | 155.9 µs [🟧1.16]<br>59.2KB [⚪1.00] | 351.7 µs **[🟥2.61]**<br>190KB **[🟥3.21]** | 508.2 µs **[🟥3.77]**<br>232KB **[🟥3.92]** | 1.86 ms **[🟥13.79]**<br>756KB **[🟥12.80]** | 354.4 µs **[🟥2.63]**<br>223KB **[🟥3.77]** | 1.31 ms **[🟥9.75]**<br>497KB **[🟥8.42]** | 1.74 ms **[🟥12.94]**<br>1.2MB **[🟥21.22]** | 2.71 ms **[🟥20.10]**<br>940KB **[🟥15.91]** |

#### MySQL

| Operation | Tier | 🏆 latency / 💾 memory | ADO (baseline) | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆 RepoDb<br>💾 Dapper.AOT | 4.71 ms [1.00]<br>21.8KB [1.00] | 4.93 ms [🔸1.05]<br>24.1KB [🟧1.10] | 4.82 ms [🔸1.02]<br>21.2KB [🔹0.97] | 4.62 ms [🔹0.98]<br>22.7KB [🔸1.04] | 5.86 ms [🟧1.24]<br>133KB **[🟥6.11]** | 4.76 ms [🔸1.01]<br>21.9KB [⚪1.00] | 4.99 ms [🔸1.06]<br>28.4KB **[🟥1.30]** | 4.72 ms [⚪1.00]<br>53.2KB **[🟥2.44]** | 5.13 ms [🔸1.09]<br>35.4KB **[🟥1.63]** |
| TxHundredInserts | 2,000 | 🏆 EF Core<br>💾 PalORM | 28.30 ms [1.00]<br>371KB [1.00] | 27.93 ms [🔹0.99]<br>311KB [🟩0.84] | 27.51 ms [🔹0.97]<br>462KB [🟧1.24] | 30.48 ms [🔸1.08]<br>623KB **[🟥1.68]** | 10.43 ms **[🟢0.37]**<br>1.1MB **[🟥3.05]** | 27.99 ms [🔹0.99]<br>357KB [🔹0.96] | 31.04 ms [🟧1.10]<br>820KB **[🟥2.21]** | 34.97 ms [🟧1.24]<br>2.5MB **[🟥6.81]** | 33.29 ms [🟧1.18]<br>1.5MB **[🟥4.07]** |
| TxBulkInsert | 2,000 | 🏆 RepoDb<br>💾 RepoDb | 11.88 ms [1.00]<br>489KB [1.00] | 11.65 ms [🔹0.98]<br>490KB [⚪1.00] | 31.41 ms **[🟥2.64]**<br>6.7MB **[🟥13.94]** | 10.97 ms [🔹0.92]<br>488KB [⚪1.00] | 89.27 ms **[🟥7.51]**<br>20.0MB **[🟥41.94]** | 22.65 ms **[🟥1.91]**<br>6.7MB **[🟥13.93]** | 21.66 ms **[🟥1.82]**<br>3.4MB **[🟥7.16]** | 19.79 ms **[🟥1.67]**<br>3.1MB **[🟥6.53]** | 41.63 ms **[🟥3.50]**<br>6.9MB **[🟥14.35]** |
| TxRollback | 2,000 | 🏆 EF Core<br>💾 ADO (baseline) | 100.18 ms [1.00]<br>1.3MB [1.00] | 105.48 ms [🔸1.05]<br>1.4MB [🔸1.09] | 109.57 ms [🔸1.09]<br>2.2MB **[🟥1.71]** | 112.66 ms [🟧1.12]<br>3.6MB **[🟥2.76]** | 38.15 ms **[🟢0.38]**<br>9.0MB **[🟥6.91]** | 116.51 ms [🟧1.16]<br>2.4MB **[🟥1.89]** | 129.54 ms [🟧1.29]<br>4.9MB **[🟥3.79]** | 136.02 ms **[🟥1.36]**<br>14.8MB **[🟥11.46]** | 126.38 ms [🟧1.26]<br>8.5MB **[🟥6.57]** |
| EnumInserts | 2,000 | 🏆 EF Core<br>💾 ADO (baseline) | 25.08 ms [1.00]<br>265KB [1.00] | 26.81 ms [🔸1.07]<br>291KB [🟧1.10] | 27.20 ms [🔸1.08]<br>396KB **[🟥1.49]** | 28.88 ms [🟧1.15]<br>528KB **[🟥1.99]** | 9.73 ms **[🟢0.39]**<br>839KB **[🟥3.16]** | 26.09 ms [🔸1.04]<br>424KB **[🟥1.60]** | 31.71 ms [🟧1.26]<br>779KB **[🟥2.93]** | 33.33 ms **[🟥1.33]**<br>2.0MB **[🟥7.71]** | 34.37 ms **[🟥1.37]**<br>1.2MB **[🟥4.63]** |

#### PostgreSQL

| Operation | Tier | 🏆 latency / 💾 memory | ADO (baseline) | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆 ADO (baseline)<br>💾 Dapper.AOT | 1.46 ms [1.00]<br>8.3KB [1.00] | 1.52 ms [🔸1.04]<br>9.3KB [🟧1.12] | 1.53 ms [🔸1.05]<br>7.5KB [🟩0.90] | 1.50 ms [🔸1.03]<br>8.7KB [🔸1.04] | 1.93 ms **[🟥1.33]**<br>57.3KB **[🟥6.90]** | 1.51 ms [🔸1.04]<br>8.1KB [🔹0.97] | 1.48 ms [🔸1.02]<br>12.1KB **[🟥1.46]** | 1.50 ms [🔸1.03]<br>23.3KB **[🟥2.81]** | 1.58 ms [🔸1.08]<br>19.9KB **[🟥2.40]** |
| TxHundredInserts | 2,000 | 🏆 EF Core<br>💾 PalORM | 20.62 ms [1.00]<br>314KB [1.00] | 20.55 ms [⚪1.00]<br>212KB **[🟢0.68]** | 20.65 ms [⚪1.00]<br>310KB [🔹0.99] | 21.21 ms [🔸1.03]<br>430KB **[🟥1.37]** | 3.47 ms **[🟢0.17]**<br>875KB **[🟥2.79]** | 20.90 ms [🔸1.01]<br>283KB [🟩0.90] | 23.93 ms [🟧1.16]<br>703KB **[🟥2.24]** | 23.13 ms [🟧1.12]<br>1.8MB **[🟥5.80]** | 26.78 ms **[🟥1.30]**<br>1.5MB **[🟥4.84]** |
| TxBulkInsert | 2,000 | 🏆 ADO (baseline)<br>💾 ADO (baseline) | 4.69 ms [1.00]<br>340KB [1.00] | 4.74 ms [🔸1.01]<br>341KB [⚪1.00] | 15.30 ms **[🟥3.26]**<br>6.7MB **[🟥20.12]** | 5.29 ms [🟧1.13]<br>843KB **[🟥2.48]** | 41.93 ms **[🟥8.95]**<br>15.8MB **[🟥47.48]** | 15.91 ms **[🟥3.39]**<br>6.7MB **[🟥20.12]** | 21.57 ms **[🟥4.60]**<br>3.4MB **[🟥10.26]** | 10.78 ms **[🟥2.30]**<br>1.1MB **[🟥3.46]** | 26.51 ms **[🟥5.66]**<br>7.4MB **[🟥22.27]** |
| TxRollback | 2,000 | 🏆 EF Core<br>💾 ADO (baseline) | 100.58 ms [1.00]<br>928KB [1.00] | 105.03 ms [🔸1.04]<br>1.0MB [🟧1.12] | 105.15 ms [🔸1.05]<br>1.5MB **[🟥1.66]** | 109.78 ms [🔸1.09]<br>2.9MB **[🟥3.17]** | 13.81 ms **[🟢0.14]**<br>4.1MB **[🟥4.55]** | 104.20 ms [🔸1.04]<br>1.7MB **[🟥1.89]** | 125.77 ms [🟧1.25]<br>4.4MB **[🟥4.89]** | 114.19 ms [🟧1.14]<br>10.5MB **[🟥11.58]** | 138.58 ms **[🟥1.38]**<br>8.6MB **[🟥9.44]** |
| EnumInserts | 2,000 | 🏆 EF Core<br>💾 ADO (baseline) | 21.42 ms [1.00]<br>166KB [1.00] | 18.99 ms [🟩0.89]<br>181KB [🔸1.09] | 20.03 ms [🔹0.94]<br>251KB **[🟥1.51]** | 22.05 ms [🔸1.03]<br>353KB **[🟥2.12]** | 3.16 ms **[🟢0.15]**<br>657KB **[🟥3.94]** | 20.46 ms [🔹0.95]<br>281KB **[🟥1.69]** | 23.11 ms [🔸1.08]<br>659KB **[🟥3.96]** | 20.20 ms [🔹0.94]<br>1.4MB **[🟥8.61]** | 24.05 ms [🟧1.12]<br>1.1MB **[🟥6.99]** |

### Reproduce

```bash
# full cross-bench batch (~40 min, needs local PG / MySQL containers)
dotnet run --project tools/PalORM.PerfCli -c Release -- full
# cross-bench only
dotnet run --project tools/PalORM.PerfCli -c Release -- crossbench
```

Notes: all 9 groups come from a single batch, so ratios are only comparable within that batch; absolute times are not comparable across machines. The full 87-row detail (all 17 operations, two tiers, three dialects, with latency and memory percentages) is in [性能全量报告-2026-11-02.md](性能全量报告-2026-11-02.md). PalORM self-regression (micro-benchmarks, concurrency, memory curves, stability) is measured separately by the PerfHub fixture, see [bench/perfhub/README.md](../bench/perfhub/README.md).

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

For per-request sessions (web/API), prefer `DataSessionPool`: the session, connection, and command reuse slots survive across requests, and transient state is reset on return. In the local SQLite benchmark, `GetAsync` is about 3.8x faster and allocates about 45% less than creating a session per operation.

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

A plan fixes the SQL text and parameter template to a session-scoped object; repeated executions only write values and reuse the command. In the local SQLite benchmark, a single-row query is 22.6% faster and allocates 29.8% less than the optimized fluent path. SQL is executed verbatim, default filters do not apply; runtime parameter types must stay stable, and the plan must be disposed before the session.

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
