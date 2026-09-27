<div align="center">

# PalORM

**A .NET 11 micro-ORM for Native AOT: everything generated at compile time, zero reflection at runtime**

[![.NET](https://img.shields.io/badge/.NET-11.0.0--preview.6-512BD4)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/PalORM.Core)](https://www.nuget.org/packages/PalORM.Core)
[![CI](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml/badge.svg)](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-AGPL--3.0--only-red)](LICENSE)

[简体中文](README.md) | English

</div>

A Roslyn source generator produces SQL construction, parameter binding, object mapping, and migration DDL at compile time — zero reflection, zero IL Emit at runtime. PostgreSQL / MySQL / SQLite across three dialects, with enterprise features out of the box: multi-tenancy, optimistic locking, soft delete, auditing, advisory locks, and more.

> Detailed docs (in Chinese) live under [docs/](docs/) — [API reference](docs/API参考.md), [architecture](docs/架构设计.md), [AOT deployment guide](docs/AOT部署指南.md), [benchmark methodology](docs/性能基准规范.md).

| Compile-time diagnostics | 20,000-row BulkInsert | MySQL single-row ops | SQLite Native AOT |
|:---:|:---:|:---:|:---:|
| **43 rules** | **1.00× ADO.NET floor** | **14–58% faster** | **4.5 MB exe** |

See [Performance](#-performance) for measurement details (2026-09-24 benchmark batch).

---

## Table of Contents

- [Features](#-features)
- [Installation](#-installation)
- [Quick Start](#-quick-start)
- [Usage](#-usage)
- [Configuration](#-configuration)
- [Performance](#-performance)
- [Comparison with mainstream ORMs](#-comparison-with-mainstream-orms)
- [Development](#-development)
- [Contributing](#-contributing)
- [License](#-license)

---

## ✨ Features

**Everything generated at compile time.** A Roslyn `IIncrementalGenerator` emits, for every `[Table]` entity, a RowFactory (materialization delegates), a CommandFactory (parameter binding), and Migration (DDL for all three dialects); DTOs marked `[Projection]` get a source-generated RowFactory too, so join/report results map straight onto non-table types (v6.0). 43 compile-time diagnostics (40 analyzers + 3 generator rules) move failures that would otherwise be runtime crashes or silently wrong data — a missing `[Key]`, a nullable tenant column bypassing isolation, an optimistic-lock baseline of 0 — to compile time. On the `FormattableString` path, values only ever become `@pN` placeholders (compile-time parameterization, injection-safe by default); explicit escape hatches are `Raw()` (verbatim literal fragments, control characters rejected) and `SessionSetupSql`, where the caller owns the content.

**Full-pipeline Native AOT.** The only ORM in the .NET ecosystem with full-pipeline Native AOT support: publish verification passes on all three dialects — SQLite / PostgreSQL / MySQL (output `PalORM AOT verification PASSED`) — with no reflection, no IL Emit, no runtime code generation. Deployment details: [AOT deployment guide](docs/AOT部署指南.md) (Chinese).

**Per-dialect bulk strategies.** The same API picks the fastest path per dialect: PG Binary COPY, MySQL BulkCopy (LOAD DATA, automatic fallback to multi-value INSERT when disabled server-side), SQLite multi-value INSERT. Single-statement bulk UPDATE (PG `UPDATE FROM VALUES` / MySQL `UPDATE JOIN VALUES ROW` on 8.0.19+, CASE WHEN fallback on older versions); optimistic-lock entities go through a packed DbBatch — measured 7.85× on a remote PG database (v5.9 probe 28: 15.1 ms vs 118.7 ms row-by-row, 200-command transaction).

**Enterprise features out of the box.** Multi-tenant column isolation (query cache keys automatically get a tenant prefix — cross-tenant hits are impossible), optimistic locking, soft delete, audit interceptors, read/write splitting (`ForRead`), advisory locks, resilient retry + circuit breaker (covering the read-only query pipeline automatically since v5.4). No boilerplate required.

**Zero-dependency core.** PalORM.Core references no third-party NuGet packages (BCL + ADO.NET abstractions only). SQLite supports at-rest AES-256 encryption via the SQLite3MC driver (enable with `Password=` in the connection string; a driver-level capability).

## 📦 Installation

Requirements: .NET SDK `11.0.100-preview.6` or later (`global.json` pins `rollForward: latestMinor`); your IDE must support Roslyn source generators (SourceGen builds against Microsoft.CodeAnalysis 5.9.0). Native AOT publishes additionally need about 4 GB of memory (ILC compiler) — see [Development](#-development).

```xml
<!-- PostgreSQL -->
<PackageReference Include="PalORM.PostgreSql" Version="5.9.0" />
<!-- MySQL -->
<PackageReference Include="PalORM.MySql" Version="5.9.0" />
<!-- SQLite -->
<PackageReference Include="PalORM.Sqlite" Version="5.9.0" />
```

Each provider package pulls in `PalORM.Core` (runtime) and `PalORM.SourceGen` (compile-time source generator). After installing, verify with the minimal Quick Start example below: if you can create a session and complete one insert, the install works.

### Database compatibility

| Database | Version | Driver | Encryption |
|----------|---------|--------|:---:|
| PostgreSQL | 14+ (18 recommended) | Npgsql 10.0.3 | SSL/TLS |
| MySQL | 8.0+ (8.4 LTS recommended) | MySqlConnector 2.6.2 | SSL/TLS |
| SQLite | 3.47+ (via SQLite3MC 2.4.0) | Microsoft.Data.Sqlite.Core 11.0.0-rc.1 | AES-256 (driver level, `Password=`) |

## 🚀 Quick Start

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

### CRUD

```csharp
// Insert
var user = await db.InsertAsync(new User { Email = "alice@example.com", CreatedAt = DateTime.UtcNow });

// Query
var alice = await db.GetAsync<User>(user.Id);
var all = await db.From<User>().Where($"email LIKE {"%@example.com%"}").ToListAsync();

// Update
alice.Email = "new@example.com";
await db.UpdateAsync(alice);

// Delete
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

// Bulk delete (single statement with an IN clause)
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
| `BulkDeleteAsync` | Single `IN` statement | Single `IN` statement | Single `IN` statement |
| `BulkMergeAsync` | Multi-row UPSERT (`ON CONFLICT DO UPDATE`) | Multi-row UPSERT (`ON DUPLICATE KEY UPDATE`) | Multi-row UPSERT (`ON CONFLICT DO UPDATE`) |

`BulkMergeAsync` (set-based since v5.6.0) partitions by key state: default-key rows (new) go row-by-row INSERT to preserve the ID-backfill contract; non-default-key rows are batched by dialect parameter limits into multi-row UPSERT (SQLite 999 / PG and MySQL 65535 parameter limit; MySQL ODKU batches of 1000 rows); `[ConcurrencyCheck]` entities stay row-by-row (UPSERT cannot honor optimistic locking).

## 📖 Usage

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

Multiple result sets (`GridReader`):

```csharp
using var grid = await db.QueryMultipleAsync($"SELECT * FROM users WHERE id = {userId}; SELECT * FROM orders WHERE user_id = {userId}");
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

Resilience policies (`WithRetry` exponential backoff + `WithCircuitBreaker`) have covered the read-only query pipeline automatically since v5.4:

```csharp
// Configure once; read-only queries get retry + circuit breaker automatically;
// transient SELECT failures (deadlocks/timeouts/connection drops) retry automatically
await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(
    DbOptions.Production(connectionString)
        .WithRetry(maxRetries: 3)
        .WithCircuitBreaker(failureThreshold: 5, resetAfter: TimeSpan.FromSeconds(30)));

// Non-idempotent writes are not retried automatically — declare resilience intent explicitly
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

Other cross-cutting capabilities: `[SoftDelete]` automatic WHERE filtering, `[TenantAware]` + `WithTenant(id)` single-database column isolation (cache keys automatically prefixed `__t:{tenantId}:`; `IgnoreFilters()` uses a separate `__all__:` namespace), `[ConcurrencyCheck]` optimistic locking, `ForRead` read/write splitting. All 22 annotations and execution-method details: [API reference](docs/API参考.md) (Chinese).

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

PG-specific: `WhereJson` (JSONB path queries), `PgNotificationListener` (async NOTIFY/LISTEN listener with automatic reconnection).

### Scaffold tool

Reverse-generate entities from an existing database:

```bash
dotnet run --project tools/PalORM.Scaffold -- <connection-string> --dialect sqlite|pg|mysql [--namespace NS] [--output DIR]
```

Schema-to-C#-entities for all three providers, 40+ type mappings (`uuid` → `Guid`, `jsonb` → `string`, `bytea` → `byte[]`, `date` → `DateOnly`, `time` → `TimeOnly`).

## 🔧 Configuration

### Presets

```csharp
var dev  = DbOptions.Development(connectionString);            // development
var prod = DbOptions.Production(connectionString, readConn);  // production (pooling + read/write splitting)
var test = DbOptions.Testing(connectionString);               // testing (zero retries + short timeouts)
var env  = DbOptions.FromEnvironment("PALORM_CONNECTION");    // environment variables (Docker/K8s friendly)
```

### Options

| Property | Type | Default | Notes |
|------|------|:---:|------|
| `ConnectionString` | `string` (required) | — | Primary connection string. Supports `$ENV:VAR_NAME` environment-variable references |
| `ReadConnectionString` | `string?` | null | Read replica connection string. When set, `ForRead()` routes automatically |
| `ConnectionTimeout` | `TimeSpan` | 15s | Connection establishment timeout (including retries); throws `TimeoutException` on expiry |
| `CommandTimeout` | `TimeSpan` | 30s | Command execution timeout; sub-second values round up to 1 second |
| `MaxRetries` | `int` | 3 | Max retries for transient failures, 0=disabled. Covers the read-only query pipeline; writes are not auto-retried |
| `RetryBackoff` | `Func<int, TimeSpan>?` | Exponential backoff | Custom retry intervals (parameter = retry count); negative values throw |
| `MaxPoolSize` | `int` | 100 | Pool upper bound. Ignored for SQLite (embedded database, no server-side pool) |
| `MinPoolSize` | `int` | 0 | v5.6.0. 0=keep driver default; positive values pass through — after idle trimming the pool keeps at least this many warm connections, eliminating latency spikes from rebuilding connections on bursts. Ignored for SQLite |
| `PoolIdleTimeoutSeconds` | `int` | 0 | Default 0 since v5.6.0 (30 earlier). 0=keep driver default (Npgsql 300s / MySqlConnector 180s); only positive values override, at the cost that the first query after an idle expiry rebuilds a physical connection (measured cross-subnet `SELECT 1`: 0.3 ms pooled vs 13.5 ms fresh) |
| `PoolLifetimeMinutes` | `int` | 60 | Maximum connection lifetime; connections are force-rebuilt on expiry |
| `PoolExplicitlyConfigured` | `bool` | false | true once `WithPool()` is set (internal marker) |
| `CircuitBreakerThreshold` | `int` | 5 | Consecutive-failure threshold, 0=disable the breaker |
| `CircuitBreakerResetAfter` | `TimeSpan` | 30s | Wait before the breaker moves to half-open |
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

**SQLite PRAGMA** (8 settings):

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

## 📊 Performance

> **Test environment**: AMD Ryzen 9 8945HX (32 logical cores) · Windows 10 22H2 · .NET 11 RC1 (SDK 11.0.100-rc.1) · BenchmarkDotNet fork (net11) · SQLite shared-memory 10K rows · PG 18.4 / MySQL 8.4.10 remote. Control-arm versions follow the 2026-09-24 dependency-upgrade round (Dapper 2.1.89).
> **Data batches**: SQLite CRUD tables = 2026-09-24 BDN gate-set (launch 1 / warmup 3 / iteration 5, means); bulk tables = same-day PerfHub full batch (`history-20260924-231214.json`, medians + exact allocation counts); remaining tables are raw batch results from the corresponding dedicated measurements. Full methodology and reproduction commands: [benchmark methodology](docs/性能基准规范.md) (Chinese) and `bench/perfhub/report.html`.

### SQLite CRUD (4-ORM comparison)

| Operation | ADO.NET | Dapper | PalORM | RepoDb |
|------|------:|------:|------:|------:|
| Full-table query, 10,000 rows | 4.28 ms | 3.69 ms | 4.85 ms (1.13x) | 3.53 ms |
| Single-row insert | 25.01 μs | 26.88 μs | 32.51 μs (1.30x) | 27.12 μs |
| Primary-key lookup | 22.96 μs | 25.28 μs | 27.66 μs (1.20x) | 27.43 μs |

### Bulk operations (PerfHub · SQLite · 20,000 rows · three-arm equivalent setup)

| Method | Mean | Allocated | vs Dapper |
|:-----|-----:|----------:|:---------:|
| ADO.NET floor | 160.4 ms | 14.9 MB | 0.15x |
| Dapper multi-value INSERT | 1,105.4 ms | 79.5 MB | 1.0x |
| **PalORM BulkInsert** | **161.0 ms** | **14.8 MB** | **0.15x (6.9× faster, 19% of allocations)** |

Under the three-arm contract PalORM matches the hand-written ADO.NET floor line by line (P/ADO 0.98–1.00); Dapper's multi-value INSERT at the 20,000-row mark is 6.9× slower with 5.3× allocations due to giant SQL string construction.

### GC boxing analysis

| Operation (10K rows) | Mean | Allocated | bytes/row |
|:-----|-----:|----------:|:---------:|
| Insert (row-by-row) | 103.7 ms | 25,930 KB | 2,654 B |
| **BulkInsert** | **62.3 ms** | **5,099 KB** | **522 B** |
| BulkUpdate (row-by-row) | 28.8 ms | 17,973 KB | 1,839 B |
| Query (control) | 0.089 ms | 5.41 KB | 0.55 B |

The PG COPY / MySQL BulkCopy paths bypass `DbParameter.Value` entirely — no boxing remains.

### PostgreSQL (remote PG 18.4)

| Operation | Mean | Allocated |
|:-----|-----:|----------:|
| QueryAll 10K | 15.04 ms | 1,140 KB |
| BulkInsert 10K (COPY) | 43.06 ms | 9,797 KB |
| **BulkUpdateBatch 1K (FROM VALUES)** | **4.85 ms** | 2,777 KB |
| GetByKey | 501.9 μs | 13.64 KB |

### MySQL (remote MySQL 8.4.10)

| Operation | Mean | vs ADO.NET | Allocated |
|:-----|-----:|:---------:|----------:|
| QueryAll 10K | 94.79 ms | 0.85x (15% faster) | 1,937 KB |
| BulkInsert 10K | 49.41 ms | — | 4,741 KB |
| **BulkUpdateBatch 1K** | **12.43 ms** | — | 2,405 KB |
| **GetByKey** | **518.1 μs** | **0.42x (58% faster)** | 12.02 KB |
| **Insert** | **1,597 μs** | **0.86x (14% faster)** | 12.45 KB |

Single-row operations are 14–58% faster than raw ADO.NET: the connection-string tuning gains (`AutoEnlist=false` / `ConnectionReset=false`) are amplified in remote scenarios. The BulkUpdateBatch figures above come from CASE WHEN batches; MySQL switched to `UPDATE JOIN VALUES ROW` in v5.9.0 (measured 8.75× vs CASE WHEN at 20,000 rows).

### SQL construction (nanoseconds)

| Method | Mean | Allocated |
|:-----|-----:|----------:|
| StringBuilder (baseline) | 61.07 ns | 1,496 B |
| PalORM Simple | 129.01 ns | **544 B (−64%)** |
| PalORM Complex | 161.01 ns | **696 B (−53%)** |

### Cross-dialect BulkUpdateBatch (1K rows)

| Dialect | SQL strategy | Mean | Speed ratio |
|------|---------|-----:|:------:|
| SQLite | CASE WHEN → row-by-row fallback | 28.3 ms | 1.0x |
| **PostgreSQL** | **UPDATE FROM VALUES** | **4.85 ms** | **5.8x** |
| MySQL | CASE WHEN (VALUES ROW since v5.9.0) | 12.43 ms | 2.3x |

### Native AOT publish size

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

## 🆚 Comparison with mainstream ORMs

> Version baseline: PalORM 5.9.0 / Dapper 2.1.89 / EF Core 10.0.10 / RepoDb 1.16.0 (versions used by this repo's benchmark suite). Cell evidence in the notes below.

| Feature | **PalORM** | Dapper | EF Core | RepoDb |
|------|:---:|:---:|:---:|:---:|
| **Full-pipeline Native AOT** | ✓ source-generated, verified | △ Dapper.Aot optional (experimental interceptors) | ❌ experimental, not production-ready | ❌ reflection + IL Emit |
| **Compile-time type diagnostics** | ✓ 43 rules (40 analyzers + 3 generator) | ❌ fails at runtime | △ migration checks (design time) | ❌ fails at runtime |
| **Compile-time SQL pre-building** | ✓ Roslyn source generation | ❌ runtime concatenation | △ precompiled queries (experimental) | ❌ runtime expression trees |
| **Runtime reflection** | Zero | △ first-use reflection + IL Emit cache | △ expression-tree compilation | ❌ reflection + IL Emit |
| **Per-dialect bulk strategies** | ✓ COPY / BulkCopy / multi-value | ❌ hand-written multi-value SQL | △ varies by provider | △ BulkInsert SQL Server only |
| **Single-statement multi-row UPDATE** | ✓ FROM VALUES / UPDATE JOIN VALUES ROW / CASE WHEN | ❌ | ❌ ExecuteUpdate is single-value per WHERE only | ❌ |
| **Optimistic locking** | ✓ `[ConcurrencyCheck]` automatic | ❌ hand-written | ✓ `RowVersion` automatic | ❌ hand-written |
| **Soft delete** | ✓ `[SoftDelete]` automatic filtering | ❌ | ✓ global query filters | ❌ |
| **Multi-tenant column isolation** | ✓ `[TenantAware]` compile time | ❌ | △ manual implementation | ❌ |
| **OwnedJson compile-time safety** | ✓ `[OwnedJson]` + source generation | ❌ hand-written STJ | △ Owned Types (runtime) | ❌ |
| **Audit interceptor** | ✓ | ❌ | ✓ Interceptors | ❌ |
| **Advisory locks** | ✓ `pg_advisory_xact_lock` | ❌ | ❌ | ❌ |
| **Session-level SET** | ✓ `SessionSetupSql` | ❌ | ❌ | ❌ |
| **SQL file embedding** | ✓ `[SqlFile]` compile-time validation | ❌ | ❌ | ❌ |
| **Circuit breaker + retry** | ✓ built in | ❌ needs Polly | △ execution strategies | ❌ |
| **CTE / window functions** | ✓ chained API | △ raw SQL strings | △ LINQ translation (partial) | △ raw SQL |
| **Multiple result sets** | ✓ `GridReader` | ✓ `QueryMultiple` | ❌ | ✓ `ExecuteQueryMultiple` |
| **Keyset paging** | ✓ `ToPageAsync` | ❌ | ❌ | ❌ |
| **Scaffold tool** | ✓ all three providers | ❌ | ✓ `dotnet ef dbContext scaffold` | ❌ |
| **Connection-string auto-tuning** | ✓ PG 6 / MySQL 5 / SQLite 8 settings | ❌ | ❌ | ❌ |
| **BulkInsert memory** | ≈ 19% of Dapper | baseline | highest (ChangeTracker) | medium |
| **Core NuGet dependencies** | zero | zero | high (multi-package) | medium |
| **Target frameworks** | net11.0 (single target) | multi-target (netstandard2.0+) | multi-target (net8+) | multi-target (netstandard2.0+) |
| **License** | AGPL-3.0-only | Apache-2.0 | MIT | Apache-2.0 |

Core differentiators: compile-time generation + full-pipeline AOT compatibility + per-dialect bulk strategies. Dapper is fast but reflects at runtime; EF Core is feature-complete but heavy at runtime with AOT still experimental; RepoDb is also a micro-ORM but has no source generation, and its bulk support is SQL Server only.

Comparison evidence:

- **Dapper**: `Dapper.AOT` (separate package, [aot.dapperlib.dev](https://aot.dapperlib.dev)) generates AOT interceptors via Roslyn interceptors — an experimental C# feature, not enabled by default.
- **EF Core 10**: LTS ([learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)). `ExecuteUpdateAsync` only supports a single value per WHERE clause and cannot set different values per row in one SQL statement; AOT is still experimental ([issue #35945](https://github.com/dotnet/efcore/issues/35945)).
- **RepoDb**: BulkOperation is SQL Server only ([repodb.net/operation/bulkinsert](https://repodb.net/operation/bulkinsert): *"It is only supporting the SQL Server RDBMS."*); other dialects use packed statements.

## 🧰 Development

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
