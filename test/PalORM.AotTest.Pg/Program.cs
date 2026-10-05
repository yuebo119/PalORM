using System.Text.Json.Serialization;
using PalORM.PostgreSql;

namespace PalORM.AotTest.Pg;

[Table("aot_pg_test")]
internal sealed partial class AotPgEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
    [Column("version")][ConcurrencyCheck] public long Version { get; set; }
}

[SoftDelete]
[Table("aot_pg_bulk_test")]
internal sealed partial class AotPgBulkEntity
{
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
    [Column("created_by")][IgnoreOnInsert] public string CreatedBy { get; set; } = "client";
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }
}

[Table("aot_pg_json_test")]
internal sealed partial class AotPgJsonEntity
{
    [Key] public long Id { get; set; }
    [Column("details")][OwnedJson(typeof(AotPgJsonContext))] public AotPgDetails Details { get; set; } = new();
}

// ITM-419：经 MigrateAsync（源生成 CreateTableSqlByDialect PG 产物）建表的实体——
// 此前 PG 宿主全部手写 DDL，源生成 PG DDL 在 AOT 原生路径零真库验证（E1 残留敞口）
[Table("aot_pg_migrated")]
[Index("ix_aot_pg_migrated_label", "label")]
internal sealed partial class AotPgMigratedEntity
{
    [Key] public long Id { get; set; }
    [Column("label")] public string Label { get; set; } = "";
    [Column("amount")] public decimal Amount { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class AotPgDetails
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

[JsonSerializable(typeof(AotPgDetails), TypeInfoPropertyName = "AotPgDetailsInfo")]
internal sealed partial class AotPgJsonContext : JsonSerializerContext;

// R1（v6.0）：[Projection] DTO 物化的 AOT 全链验证载体（顶层声明——嵌套类与实体同规被拒）
[Projection]
internal sealed class AotPgSummary
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

internal static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303",
        Justification = "固定英文文本是 Native AOT smoke test 的机器可读成功标记。")]
    internal static async Task Main()
    {
        string connectionString = Environment.GetEnvironmentVariable("PALORM_PG_CONNECTION")
            ?? throw new InvalidOperationException(
                "PALORM_PG_CONNECTION is required. "
                + "Run 'source scripts/set-test-env.sh' after creating .env.test, "
                + "or set PALORM_PG_* variables for appsettings.test.json template expansion.");
        var options = new DbOptions { ConnectionString = connectionString };
        DataSession<PostgreSqlProvider> db = await DataSession<PostgreSqlProvider>.CreateAsync(options).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_bulk_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_json_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_migrated").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_pg_test (\"Id\" BIGSERIAL PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL, version BIGINT NOT NULL)").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_pg_json_test (\"Id\" BIGSERIAL PRIMARY KEY, details TEXT NOT NULL)").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_pg_bulk_test (\"Id\" TEXT PRIMARY KEY, name TEXT NOT NULL, created_by TEXT NOT NULL DEFAULT 'database', deleted_at TIMESTAMPTZ)").ConfigureAwait(false);
            // r19/T-P3-10：建表后逻辑包 try/finally——断言失败也清理（T6）
            try
            {

            AotPgEntity inserted = await db.InsertAsync(new AotPgEntity
            {                Name = "AOT PG works!",
                Value = 42,
                Version = 0
            }).ConfigureAwait(false);
            if (inserted.Id <= 0)
                throw new InvalidOperationException("PostgreSQL INSERT failed");
            if (await db.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM aot_pg_test WHERE \"Id\" = {inserted.Id:N0}")
                    .ConfigureAwait(false) != 1)
                throw new InvalidOperationException("PostgreSQL composite format parameterization failed");

            AotPgEntity first = await db.GetAsync<AotPgEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL GET failed");
            AotPgEntity stale = await db.GetAsync<AotPgEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL stale GET failed");

            // R1（v6.0）：[Projection] DTO 物化（AOT 全链）
            await VerifyProjectionAsync(db, inserted.Id).ConfigureAwait(false);

            first.Name = "AOT PG updated";
            if (await db.UpdateAsync(first).ConfigureAwait(false) != 1 || first.Version != 1)
                throw new InvalidOperationException("PostgreSQL UPDATE failed");

            stale.Name = "stale";
            try
            {
                await db.UpdateAsync(stale).ConfigureAwait(false);
                throw new InvalidOperationException("PostgreSQL concurrency conflict was not detected");
            }
            catch (ConcurrencyConflictException)
            {
                // S108: 测试期望此异常——stale row 必须被并发控制拒绝。
            }

            AotPgJsonEntity json = await db.InsertAsync(new AotPgJsonEntity
            {
                Details = new AotPgDetails { Name = "source-generated", Count = 7 }
            }).ConfigureAwait(false);
            AotPgJsonEntity roundTrip = await db.GetAsync<AotPgJsonEntity>(json.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL OwnedJson GET failed");
            if (roundTrip.Details.Name != "source-generated" || roundTrip.Details.Count != 7)
                throw new InvalidOperationException("PostgreSQL OwnedJson round trip failed");

            var bulkEntities = new[]
            {
                new AotPgBulkEntity { Id = "first", Name = "first" },
                new AotPgBulkEntity { Id = "second", Name = "second" }
            };
            if (await db.BulkInsertAsync(bulkEntities).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("PostgreSQL bulk INSERT failed");
            AotPgBulkEntity? bulk = await db.GetAsync<AotPgBulkEntity>("first").ConfigureAwait(false);
            if (bulk?.CreatedBy != "database")
                throw new InvalidOperationException("PostgreSQL bulk INSERT metadata failed");
            if (await db.BulkDeleteAsync<AotPgBulkEntity>(["first", "second"]).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("PostgreSQL bulk soft DELETE failed");
            AotPgBulkEntity? softDeleted = await db.IgnoreFilters()
                .GetAsync<AotPgBulkEntity>("first").ConfigureAwait(false);
            if (softDeleted?.DeletedAt is null)
                throw new InvalidOperationException("PostgreSQL bulk soft DELETE metadata failed");

            if (await db.DeleteAsync<AotPgEntity>(inserted.Id).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("PostgreSQL DELETE failed");
            await db.DeleteAsync<AotPgJsonEntity>(json.Id).ConfigureAwait(false);

            // ITM-419：源生成 PG DDL（表 + 索引）经 MigrateAsync 真库执行 + CRUD 往返
            await db.MigrateAsync().ConfigureAwait(false);
            await db.MigrateAsync().ConfigureAwait(false); // 幂等：IF NOT EXISTS 第二次不抛
            AotPgMigratedEntity migrated = await db.InsertAsync(new AotPgMigratedEntity
            {
                Label = "migrated",
                Amount = 12.5m,
                CreatedAt = DateTimeOffset.UtcNow
            }).ConfigureAwait(false);
            AotPgMigratedEntity migratedBack = await db.GetAsync<AotPgMigratedEntity>(migrated.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL migrated-table GET failed");
            if (migratedBack.Label != "migrated" || migratedBack.Amount != 12.5m)
                throw new InvalidOperationException("PostgreSQL migrated-table round trip failed");

            // UNNEST 阶段 B（2026-10-02）：批量 UPDATE / Merge 的数组形态在 NativeAOT 下的
            // 原生实跑（生成物新增 AOT 面：泛型 new T[]、委托、类型表）。
            await VerifyUnnestArrayFormsAsync(db).ConfigureAwait(false);

            await VerifyPessimisticLocksAsync(db).ConfigureAwait(false);
            }
            finally
            {
                await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_bulk_test").ConfigureAwait(false);
                await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_json_test").ConfigureAwait(false);
                await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_test").ConfigureAwait(false);
                await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_migrated").ConfigureAwait(false);
            }
        }

        Console.WriteLine("PalORM AOT PG verification PASSED");
    }

    /// <summary>UNNEST 阶段 B（2026-10-02）：批量 UPDATE / Merge 的数组形态在 NativeAOT 下的
    /// 原生实跑——生成物的逐列数组填充器/分配器是新增 AOT 面（泛型 <c>new T[]</c>、委托、
    /// 类型表），必须原生二进制验证（P0 #3：编译通过不等于运行时安全）。
    /// <para>用 AotPgMigratedEntity：无并发令牌、无软删（两者都会把 BulkUpdateAsync 路由到
    /// 逐条路径，到不了数组形态）。</para></summary>
    private static async Task VerifyUnnestArrayFormsAsync(DataSession<PostgreSqlProvider> db)
    {
        var rows = new List<AotPgMigratedEntity>();
#pragma warning disable PALORM005 // 播种循环是有意逐行（本方法验证的是批量写路径，不是读取）
        for (long i = 1; i <= 50; i++)
            rows.Add(await db.InsertAsync(new AotPgMigratedEntity
            {
                Label = $"m{i}", Amount = i * 0.5m, CreatedAt = DateTimeOffset.UtcNow
            }).ConfigureAwait(false));
#pragma warning restore PALORM005

        foreach (AotPgMigratedEntity row in rows)
        {
            row.Label = $"mu{row.Id}";
            row.Amount = row.Id * 1.25m;
        }
        if (await db.BulkUpdateAsync(rows).ConfigureAwait(false) != rows.Count)
            throw new InvalidOperationException("PostgreSQL bulk UPDATE (UNNEST array form) failed");
        AotPgMigratedEntity updatedBack = await db
            .GetAsync<AotPgMigratedEntity>(rows[0].Id).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL bulk UPDATE GET failed");
        if (updatedBack.Label != $"mu{rows[0].Id}" || updatedBack.Amount != rows[0].Id * 1.25m)
            throw new InvalidOperationException("PostgreSQL bulk UPDATE (UNNEST array form) round trip failed");

        foreach (AotPgMigratedEntity row in rows) row.Label = $"mm{row.Id}";
        if (await db.BulkMergeAsync(rows).ConfigureAwait(false) != rows.Count)
            throw new InvalidOperationException("PostgreSQL bulk MERGE (UNNEST array form) failed");
        AotPgMigratedEntity mergedBack = await db
            .GetAsync<AotPgMigratedEntity>(rows[1].Id).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL bulk MERGE GET failed");
        if (mergedBack.Label != $"mm{rows[1].Id}")
            throw new InvalidOperationException("PostgreSQL bulk MERGE (UNNEST array form) round trip failed");
    }

    /// <summary>悲观锁子句的**原生执行**验证——SQLite 执行不了锁语句（ITM-639：只支持预览形态），
    /// JIT 侧由 PessimisticLockTests 覆盖；这里是 PG 上的原生二进制等价验证：
    /// 锁子句拼进语句的位置合法、PG 真库接受，且在 AOT 链路下照常工作。</summary>
    private static async Task VerifyPessimisticLocksAsync(DataSession<PostgreSqlProvider> db)
    {
        // 专用探针行：不依赖前序步骤插入的行（其 Name 与生命周期都不归本验证管）
        AotPgEntity probe = await db.InsertAsync(new AotPgEntity
        {
            Name = "lock probe",
            Value = 1,
            Version = 0
        }).ConfigureAwait(false);

        // 过滤用显式映射的 name 列（[Column("name")]）而非主键——Id 无 [Column] 特性，
        // PG 的实际列名是带引号的大小写敏感 "Id"，手写小写 id 会撞 42703（本程序首跑即踩）。
        await db.WithTransaction(async ct =>
        {
            List<AotPgEntity> forUpdate = await db.From<AotPgEntity>()
                .Where($"name = {"lock probe"}").ForUpdate().ToListAsync(ct).ConfigureAwait(false);
            List<AotPgEntity> forShare = await db.From<AotPgEntity>()
                .Where($"name = {"lock probe"}").ForShare().ToListAsync(ct).ConfigureAwait(false);
            List<AotPgEntity> skipLocked = await db.From<AotPgEntity>()
                .Where($"name = {"lock probe"}").ForUpdate(skipLocked: true).ToListAsync(ct).ConfigureAwait(false);
            if (forUpdate.Count != 1 || forShare.Count != 1 || skipLocked.Count != 1)
                throw new InvalidOperationException("PostgreSQL pessimistic lock execution failed");
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>R1（v6.0）：[Projection] DTO 物化的 AOT 全链验证——只读类型经源生成注册，
    /// QueryAsync 直接物化（ordinal 契约），不参与写命令与迁移 DDL。</summary>
    private static async Task VerifyProjectionAsync(
        DataSession<PostgreSqlProvider> db, long id)
    {
        List<AotPgSummary> summaries = await db.QueryAsync<AotPgSummary>(
                $"SELECT \"Id\", name AS \"Name\" FROM aot_pg_test WHERE \"Id\" = {id:N0}")
            .ConfigureAwait(false);
        if (summaries.Count != 1 || summaries[0].Id != id || summaries[0].Name != "AOT PG works!")
            throw new InvalidOperationException("PostgreSQL [Projection] materialization failed");
    }
}
