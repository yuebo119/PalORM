<h1 align="center">PalORM</h1>
<p align="center"><strong>Native AOT Micro-ORM for .NET 11</strong></p>
<p align="center">
  <img src="https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet">
  <img src="https://img.shields.io/badge/tests-425%2F425-success">
  <img src="https://img.shields.io/badge/AOT-verified-success">
  <img src="https://img.shields.io/badge/IL%20suppressions-0-success">
  <img src="https://img.shields.io/badge/license-AGPL%20v3-blue">
</p>

Compile-time code generation via Roslyn source generators. No reflection, no IL/AOT warning suppression. Three providers (SQLite / PostgreSQL / MySQL) with full CRUD, OwnedJson, optimistic concurrency, cross-assembly, and NuGet consumer Native AOT verified.

---

## Installation

```xml
<PackageReference Include="PalORM.Core" Version="4.6.0" />
<PackageReference Include="PalORM.Sqlite" Version="4.6.0" />
<PackageReference Include="PalORM.SourceGen" Version="4.6.0" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

---

## Quick Start

```csharp
[Table("users")]
public partial class User
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

var db = await DataSession<SqliteProvider>.CreateAsync(
    DbOptions.Development("Data Source=app.db"));
await db.MigrateAsync();

var user = await db.InsertAsync(new User { Name = "Alice" });
var found = await db.GetAsync<User>(user.Id);
var users = await db.From<User>()
    .Where($"name = {"Alice"}")
    .OrderBy(u => u.Id, descending: true)
    .Take(10)
    .ToListAsync();
```

## Configuration

```csharp
// Presets
var dev = DbOptions.Development("Data Source=dev.db");       // relaxed
var prod = DbOptions.Production("$ENV:DATABASE_URL");        // strict + pooling
var test = DbOptions.Testing("Data Source=:memory:");        // short timeout

// Custom
var options = new DbOptions
{
    ConnectionString = "$ENV:DATABASE_URL",
    CommandTimeout = TimeSpan.FromSeconds(60),
    MaxRetries = 5,
    CircuitBreakerThreshold = 10
}.WithPool(maxSize: 200);

// Environment variables (Docker/K8s)
var env = DbOptions.FromEnvironment("DATABASE_URL");
```

---

## Core Features

### Architecture

| Feature | Description |
|---------|-------------|
| `FormattableString` parameters | Compile-time parameter extraction, zero SQL injection |
| `struct QueryBuilder<T>` | Value type, copy-on-write, zero heap allocation |
| Native AOT | `IsAotCompatible` + `IsTrimmable` + `PublishAot`, zero IL suppression |
| `static abstract IDbProvider` | Compile-time dispatch, zero virtual call overhead |
| Zero dependencies | Core: BCL + ADO.NET + `LoggerFactory.Abstractions` |

### Query

| API | Description |
|-----|-------------|
| `Where(FormattableString)` / `OrWhere` | Parameterized conditions |
| `OrderBy(expr)` / `ThenBy` / `Take` / `Skip` | Sorting and paging |
| `Select(expr[])` | Projection (DryRun only) |
| `InnerJoin` / `LeftJoin` / `RightJoin` | SQL JOIN |
| `Include<TChild>(fk, pk)` / `ThenInclude` | Multi-level navigation |
| `WhereIn` / `WhereNotIn` | Auto-batched (SQLite 999 / MySQL 65535) |
| `GroupBy` / `Having` | Aggregation |
| `With("cte", subquery)` | CTE |
| `UnsafeWindowOver` | Window functions |
| `ForUpdate()` / `ForShare()` | Pessimistic locks |
| `AsSplitQuery()` | Root-only query (no navigation assembly) |
| `ForRead()` / `ForWrite()` | Read/write routing |
| `WithCache(key, TTL)` | Bounded LRU cache |
| `AsPrepared()` | DbCommand.PrepareAsync |
| `AsDryRun()` | SQL preview without execution |
| `Tag("name")` / `TagWithCaller()` | SQL comment tags |

### Write

| API | Description |
|-----|-------------|
| `InsertAsync` / `UpdateAsync` / `DeleteAsync` | CRUD |
| `SaveAsync` (UPSERT) | Single round-trip: `ON CONFLICT DO UPDATE` / `ON DUPLICATE KEY UPDATE` |
| `GetAsync(key)` / `GetAllAsync` | Primary key / full table |
| `BulkInsertAsync` / `BulkUpdateAsync` / `BulkDeleteAsync` | Batch operations |
| `SeedAsync` | Idempotent upsert seed data |

### Resilience

| Feature | Description |
|---------|-------------|
| Retry | Configurable count + exponential backoff |
| Circuit breaker | Fast-fail after N consecutive failures, half-open probe on reset |
| Timeout | Per-command via `CancellationTokenSource.CancelAfter` |
| Transactions | `WithTransaction(callback)` auto commit/rollback + exception preservation |
| Savepoints | `SavepointAsync` / `RollbackToAsync` |

### Cross-Cutting

| Feature | Annotation |
|---------|-----------|
| Soft delete | `[SoftDelete]` + `deleted_at` column, auto-filtered |
| Multi-tenant | `[TenantAware]` + `tenant_id` column, auto-isolated |
| Optimistic lock | `[ConcurrencyCheck]` + `Version` column |
| Interceptors | `IQueryInterceptor` — OnBefore/OnAfter/OnError |
| Observability | `WithTracing()` / `WithMetrics()` — BCL ActivitySource + Meter |
| SQL file embedding | `[SqlFile("path.sql")]` — compile-time embed as `const string` |
| SQL templates | `[SqlTemplate("name")]` — extract `FormattableString` as constant |
| Value converter | `[Converter(typeof(T))]` — AOT-safe, source-generated |

### PostgreSQL

| Feature | Description |
|---------|-------------|
| NOTIFY/LISTEN | `PgNotificationListener` — auto-reconnect, half-open probe, subscriber isolation |
| JSONB | `WhereJson(column, jsonpath, value)` |
| Binary COPY | `NpgsqlBinaryImporter` — zero round-trip bulk write |

---

## Compile-Time Diagnostics (21 rules)

| Rule | Description |
|------|-------------|
| PALORM001 | [Table] entity missing [Key] |
| PALORM002 | Property missing [Column] |
| PALORM003 | [ForeignKey] references non-existent table |
| PALORM004 | [ForeignKey] missing OnDelete |
| PALORM005 | N+1 query detection |
| PALORM008-010 | OwnedJson context validation |
| PALORM011 | Qualified table name rejected |
| PALORM012-013 | Concurrency token type constraints |
| PALORM014 | [SoftDelete] requires deleted_at column |
| PALORM016 | Unknown type / invalid mapping |
| PALORM017 | Annotation declared but unused in DDL |
| PALORM018 | [TenantAware] requires tenant_id column |
| PALORM019-022 | OwnedJson context / Key legality |

> PALORM006/007 removed (006 handled by SqlFileEmitter Obsolete-error mechanism).

---

## Annotations

`[Table]` `[Column]` `[Key]` `[NotMapped]` `[ForeignKey]` `[ConcurrencyCheck]` `[IgnoreOnInsert]` `[Computed]` `[DefaultValue]` `[SensitiveData]` `[Converter]` `[SoftDelete]` `[TenantAware]` `[OwnedJson]` `[Index]` `[Unique]` `[SqlFile]` `[SqlTemplate]`

---

## Examples

### Transaction

```csharp
await db.WithTransaction(async ct =>
{
    await db.InsertAsync(new Order { Status = "pending" }, ct);
    await db.BulkInsertAsync(order.Items, ct);
    await db.ExecuteAsync(
        $"UPDATE inventory SET stock = stock - {qty} WHERE id = {itemId}", ct);
});
```

### Batch

```csharp
await db.BulkInsertAsync(tenThousandUsers);
await db.BulkUpdateAsync(modifiedUsers);
await db.BulkDeleteAsync<User>(new object[] { 1L, 2L, 3L });
```

### Multi-Result Set

```csharp
await using var grid = await db.From<Order>().QueryMultipleAsync(
    $"SELECT * FROM orders WHERE id = {orderId}; " +
    $"SELECT * FROM order_items WHERE order_id = {orderId}");
var order = await grid.ReadAsync<Order>();
var items = await grid.ReadItemsAsync<OrderItem>();
```

### Keyset Pagination

```csharp
var (rows, total) = await db.From<Order>()
    .OrderBy(o => o.CreatedAt, descending: true)
    .ToPageAsync(pageSize: 20, o => o.CreatedAt);
var (next, _) = await db.From<Order>()
    .OrderBy(o => o.CreatedAt, descending: true)
    .ToPageAsync(20, o => o.CreatedAt, rows[^1].CreatedAt.Ticks);
```

### OwnedJson

```csharp
[JsonSerializable(typeof(ProductDetails))]
internal sealed partial class ProductCtx : JsonSerializerContext;

[Table("products")]
public partial class Product
{
    [Key] public long Id { get; set; }
    [OwnedJson(typeof(ProductCtx))] public ProductDetails? Details { get; set; }
}
```

### Custom Converter

```csharp
public sealed class UlidConverter : IValueConverter<Ulid, string>
{
    public string ToProvider(Ulid m) => m.ToString();
    public Ulid FromProvider(string p) => Ulid.Parse(p);
}

[Table("docs")]
public partial class Document
{
    [Key] [Converter(typeof(UlidConverter))] public Ulid Id { get; set; }
}
```

### Stored Procedure

```csharp
var result = await db.StoredProc("GetUsersByAge")
    .WithParam("minAge", 18)
    .WithOutputParam<int>("total")
    .QueryAsync<User>();
```

### Aggregate

```csharp
long total = await db.CountAsync<Order>();
decimal revenue = await db.SumAsync<Order>($"total");
double avgPrice = await db.AvgAsync<Product>($"price");
```

### Raw SQL

```csharp
var users = await db.QueryAsync<User>($"SELECT * FROM users WHERE age > {18}");
var count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM users");
```

---

## Native AOT

```bash
dotnet publish test/PalORM.AotTest -c Release -r win-x64 \
  --self-contained -p:PublishAot=true -p:PublishTrimmed=true \
  -p:JsonSerializerIsReflectionEnabledByDefault=false \
  -o artifacts/aot
./artifacts/aot/PalORM.AotTest.exe
```

| Provider | Status | IL Suppressions |
|----------|--------|:--:|
| SQLite | Native verified | 0 |
| PostgreSQL | Native verified (Docker) | 0 |
| MySQL | Native verified (Docker) | 0 |

---

## Performance

v4.6 benchmark vs Dapper (SQLite in-memory, 10K rows):

| Operation | PalORM | Dapper | Comparison |
|-----------|-------:|-------:|:---------:|
| QueryAll 10K | 4.85ms / 1.47MB | 3.99ms / 1.32MB | +22% / +11% |
| GetByKey | 26.47μs / 3.98KB | 22.43μs / 2.34KB | +18% / +70% |
| Insert | 33.93μs / 4.97KB | 25.22μs / 3.73KB | +35% / +33% |
| BulkInsert 10K | 55.86ms / **4.97MB** | 34.32ms / 12.97MB | +63% / **-62%** |

> BulkInsert allocation is **62% lower than Dapper**. See [BENCHMARKS.md](bench/PalORM.Benchmarks/BENCHMARKS.md).

---

## Running Tests

Uses TUnit (Microsoft.Testing.Platform). Use `dotnet run`, not `dotnet test`:

```bash
dotnet run --project test/PalORM.Core.Tests
dotnet run --project test/PalORM.SourceGen.Tests
dotnet run --project test/PalORM.Integration.Tests -- \
  --treenode-filter "/*/*/*/*[Category!=ExternalDatabase]"
```

425 tests total (161 Core + 104 SourceGen + 160 Integration). External DB tests marked `Category=ExternalDatabase`.

---

## Documentation

| Document | Content |
|----------|---------|
| [CHANGELOG.md](CHANGELOG.md) | Release history |
| [docs/API参考.md](docs/API参考.md) | API reference |
| [docs/架构设计.md](docs/架构设计.md) | Architecture & design decisions |
| [docs/AOT部署指南.md](docs/AOT部署指南.md) | AOT deployment guide |
| [docs/编码规范.md](docs/编码规范.md) | Coding standards |
| [docs/踩坑目录.md](docs/踩坑目录.md) | 302 ORM pitfalls |
| [docs/变更日志.md](docs/变更日志.md) | v2.0.1 snapshot |

---

AGPL v3 · [PalDDD](https://github.com/PalDDD)
