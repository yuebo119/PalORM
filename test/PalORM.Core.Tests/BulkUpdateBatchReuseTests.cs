using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>M1/L2（2026-09-20）：批量 UPDATE 的跨批复用与 SQL 文本复用契约。
/// <para><b>背景</b>：原实现是「调用方循环 + 每批一个 ExecuteBatchUpdateAsync」，每批新建
/// DbCommand、每批重建全部参数、每批重建一份逐位相同的 SQL 文本。现收敛为
/// <c>ExecuteBulkUpdateBatchesAsync</c>（命令/参数池/CommandText 三者跨批复用）。</para>
/// <para><b>为什么需要本组用例</b>：PG/MySQL 批量路径按设计在本地回退（SQLite 方言走逐条），
/// 真库端到端在 Integration.Tests 的 PG/MySQL 档；本地能锁的是「池按满批建、命令集合按
/// 实际批收敛」这条复用语义——它一旦漂移，表现是末批参数残留（给不存在的行传值）或
/// 命令集合与 SQL 占位符数量不符，均为静默错绑。</para>
/// <para><b>方言夹具</b>：<see cref="BatchedDialectProvider"/> 报 MySql 方言（走 CASE WHEN
/// 批量 SQL）而底层连接仍是 SQLite——SQLite 原生支持 CASE WHEN、反引号标识符与 @pN 命名
/// 参数，因此批量路径可在本地真实执行端到端。</para></summary>
public sealed class BulkUpdateBatchReuseTests
{
    // ─── 池 ↔ 命令集合的收敛契约 ────────────────────────────

    [Test]
    public async Task AttachParameters_FullBatch_AttachesEntirePool()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using DbCommand cmd = conn.CreateCommand();

        const int RowParamCount = 6;   // 满批 2 行 × 3 参数
        var pool = CreatePool(RowParamCount, hasTenant: false, out int tenantSlot);

        DataSession<BatchedDialectProvider>.AttachParameters(
            cmd, pool, RowParamCount, RowParamCount, tenantParams: 0);

        await Assert.That(cmd.Parameters.Count).IsEqualTo(RowParamCount);
        // 每个槽位就是池内同一对象——复用而非重建（M1 的核心断言）
        for (int i = 0; i < RowParamCount; i++)
            await Assert.That(cmd.Parameters[i]).IsSameReferenceAs(pool[i]);
    }

    [Test]
    public async Task AttachParameters_ShrunkBatch_KeepsPrefixAndDropsTail()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using DbCommand cmd = conn.CreateCommand();

        const int PoolRowParamCount = 6;   // 满批 2 行
        const int ShrunkRowParamCount = 3; // 末批 1 行
        var pool = CreatePool(PoolRowParamCount, hasTenant: false, out int tenantSlot);

        DataSession<BatchedDialectProvider>.AttachParameters(
            cmd, pool, PoolRowParamCount, PoolRowParamCount, tenantParams: 0);
        DataSession<BatchedDialectProvider>.AttachParameters(
            cmd, pool, ShrunkRowParamCount, PoolRowParamCount, tenantParams: 0);

        await Assert.That(cmd.Parameters.Count).IsEqualTo(ShrunkRowParamCount);
        for (int i = 0; i < ShrunkRowParamCount; i++)
            await Assert.That(cmd.Parameters[i]).IsSameReferenceAs(pool[i]);
    }

    [Test]
    public async Task AttachParameters_GrowsBack_ReattachesSameObjects()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using DbCommand cmd = conn.CreateCommand();

        const int RowParamCount = 6;
        var pool = CreatePool(RowParamCount, hasTenant: false, out int tenantSlot);

        DataSession<BatchedDialectProvider>.AttachParameters(cmd, pool, 3, RowParamCount, 0);
        DataSession<BatchedDialectProvider>.AttachParameters(cmd, pool, RowParamCount, RowParamCount, 0);

        await Assert.That(cmd.Parameters.Count).IsEqualTo(RowParamCount);
        for (int i = 0; i < RowParamCount; i++)
            await Assert.That(cmd.Parameters[i]).IsSameReferenceAs(pool[i]);
    }

    [Test]
    public async Task AttachParameters_WithTenant_KeepsTenantParameterLast()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using DbCommand cmd = conn.CreateCommand();

        const int PoolRowParamCount = 6;
        var pool = BatchUpdateSqlBuilder.CreateParameterArray(
            PoolRowParamCount, hasTenantFilter: true, "@__tenant0", 42L,
            static (name, value) => new SqliteParameter(name, value));

        DataSession<BatchedDialectProvider>.AttachParameters(
            cmd, pool, 3, PoolRowParamCount, tenantParams: 1);

        await Assert.That(cmd.Parameters.Count).IsEqualTo(4);
        await Assert.That(cmd.Parameters[3]).IsSameReferenceAs(pool[PoolRowParamCount]);
        await Assert.That(cmd.Parameters[3].ParameterName).IsEqualTo("@__tenant0");
    }

    [Test]
    public async Task AttachParameters_UnchangedSize_IsNoOp()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using DbCommand cmd = conn.CreateCommand();

        const int RowParamCount = 6;
        var pool = CreatePool(RowParamCount, hasTenant: false, out int tenantSlot);

        DataSession<BatchedDialectProvider>.AttachParameters(cmd, pool, RowParamCount, RowParamCount, 0);
        DbParameter first = cmd.Parameters[0];
        DataSession<BatchedDialectProvider>.AttachParameters(cmd, pool, RowParamCount, RowParamCount, 0);

        await Assert.That(cmd.Parameters.Count).IsEqualTo(RowParamCount);
        await Assert.That(cmd.Parameters[0]).IsSameReferenceAs(first);
    }

    // ─── 池构造契约（与 CreateParameterPool 同一实现） ────────────

    [Test]
    public async Task CreateParameterArray_MatchesCreateParameterPool_NamesAndOrder()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();

        const int RowParamCount = 8;
        await using DbCommand cmd = conn.CreateCommand();
        DbParameter[] pooled = BatchUpdateSqlBuilder.CreateParameterPool(
            cmd, RowParamCount, hasTenantFilter: false, "@__tenant0", null,
            static (name, value) => new SqliteParameter(name, value));

        DbParameter[] array = BatchUpdateSqlBuilder.CreateParameterArray(
            RowParamCount, hasTenantFilter: false, "@__tenant0", null,
            static (name, value) => new SqliteParameter(name, value));

        // 两条路径必须是同一命名/顺序契约：一行差异即意味着某侧会绑错列
        await Assert.That(array.Length).IsEqualTo(pooled.Length);
        for (int i = 0; i < RowParamCount; i++)
        {
            await Assert.That(array[i].ParameterName).IsEqualTo(pooled[i].ParameterName);
            await Assert.That(array[i].ParameterName).IsEqualTo(ParameterNameCache.GetName(i));
        }
        await Assert.That(cmd.Parameters.Count).IsEqualTo(RowParamCount);  // 池路径仍挂集合
    }

    // ─── Build 入口守卫（R9/R10） ────────────────────────────

    [Test]
    public async Task Build_ZeroRowCount_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        {
            _ = BatchUpdateSqlBuilder.Build(
                SqlDialect.MySql, "`t`", "`id`", ["`a`"], rowCount: 0,
                hasTenantFilter: false, tenantParameterName: "@__tenant0");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task Build_EmptySetColumns_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = BatchUpdateSqlBuilder.Build(
                SqlDialect.PostgreSql, "\"t\"", "\"id\"", [], rowCount: 1,
                hasTenantFilter: false, tenantParameterName: "@__tenant0");
            return Task.CompletedTask;
        });
    }

    [Test]
    [Arguments("@__tenant0; DROP TABLE t --")]
    [Arguments("tenant_id")]
    [Arguments("")]
    public async Task Build_UnsafeTenantParameterName_Throws(string unsafeName)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = BatchUpdateSqlBuilder.Build(
                SqlDialect.MySql, "`t`", "`id`", ["`a`"], rowCount: 1,
                hasTenantFilter: true, tenantParameterName: unsafeName);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task Build_TenantFilterDisabled_DoesNotValidateName()
    {
        // 无租户过滤时名字不进 SQL，不该被校验拦住（既有调用点恒传合法值，此处锁边界）
        string sql = BatchUpdateSqlBuilder.Build(
            SqlDialect.MySql, "`t`", "`id`", ["`a`"], rowCount: 1,
            hasTenantFilter: false, tenantParameterName: "unused-anystring");
        await Assert.That(sql).Contains("UPDATE `t` SET");
        await Assert.That(sql).DoesNotContain("tenant_id");
    }

    // ─── 批量路径端到端（方言夹具跑真 SQL） ────────────────────

    [Test]
    public async Task BulkUpdateBatch_ViaBatchedDialect_WritesCorrectValuesPerRow()
    {
        await using DataSession<BatchedDialectProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync(
            $"CREATE TABLE tb_reuse (id INTEGER PRIMARY KEY, qty INTEGER NOT NULL, label TEXT NOT NULL)");
        for (int i = 1; i <= 3; i++)
            await session.ExecuteAsync(
                $"INSERT INTO tb_reuse (id, qty, label) VALUES ({(long)i}, {0L}, {"seed" + i})");

        List<TbReuseEntity> rows = [];
        for (int i = 1; i <= 3; i++)
            rows.Add(new TbReuseEntity { Id = i, Qty = i * 10, Label = "upd-" + i });

        long affected = await session.BulkUpdateBatchAsync(rows);

        await Assert.That(affected).IsEqualTo(3L);
        var after = (await session.From<TbReuseEntity>().OrderBy(x => x.Id).ToListAsync())
            .OrderBy(static r => r.Id).ToList();
        await Assert.That(after.Count).IsEqualTo(3);
        for (int i = 0; i < 3; i++)
        {
            // 逐行最终值：参数错位（第 i 行 SET 值绑到第 j 行键）即刻暴露
            await Assert.That(after[i].Qty).IsEqualTo((i + 1) * 10L);
            await Assert.That(after[i].Label).IsEqualTo("upd-" + (i + 1));
        }
    }

    [Test]
    public async Task BulkUpdateBatch_TenantFiltered_WritesOnlyOwnTenantRows()
    {
        await using DataSession<BatchedDialectProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync(
            $"CREATE TABLE tb_reuse_t (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, qty INTEGER NOT NULL)");
        await session.ExecuteAsync(
            $"INSERT INTO tb_reuse_t (id, tenant_id, qty) VALUES ({(long)1}, {"t1"}, {0L})");
        await session.ExecuteAsync(
            $"INSERT INTO tb_reuse_t (id, tenant_id, qty) VALUES ({(long)2}, {"t2"}, {0L})");

        session.WithTenant("t1");
        List<TbReuseTenantEntity> rows = [new() { Id = 1, Qty = 99 }];

        long affected = await session.BulkUpdateBatchAsync(rows);

        await Assert.That(affected).IsEqualTo(1L);
        var after = (await session.From<TbReuseTenantEntity>().ToListAsync())
            .OrderBy(static r => r.Id).ToList();
        await Assert.That(after[0].Qty).IsEqualTo(99L);
        await Assert.That(after[1].Qty).IsEqualTo(0L);  // 他租户行未被触碰
    }

    private static DbParameter[] CreatePool(int rowParamCount, bool hasTenant, out int tenantSlot)
    {
        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterArray(
            rowParamCount, hasTenant, "@__tenant0", null,
            static (name, value) => new SqliteParameter(name, value));
        tenantSlot = hasTenant ? rowParamCount : -1;
        return pool;
    }

    private static async Task<DataSession<BatchedDialectProvider>> CreateSessionAsync()
    {
        DataSession<BatchedDialectProvider> session =
            await DataSession<BatchedDialectProvider>.CreateAsync(new DbOptions
            {
                ConnectionString = $"Data Source=tb_reuse_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            });
        return session;
    }
}

/// <summary>方言夹具：报 MySql 方言（走 CASE WHEN 批量 SQL）而底层连接仍是 SQLite。
/// <para><b>为什么可行</b>：<see cref="BatchUpdateSqlBuilder"/> 的 MySQL 形态（CASE WHEN +
/// 反引号标识符 + @pN 参数）是 SQLite 原生支持的语法子集；批量 UPDATE 路径不依赖
/// RETURNING/BulkInsert 等方言专属能力。</para>
/// <para><b>不用于</b>：INSERT/DDL/聚合等方言敏感路径——那些的三方言契约在
/// <c>DialectDifferenceTests</c> 与真库档。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Generic type argument only — static abstract interface members are never invoked on an instance.")]
internal sealed class BatchedDialectProvider : IDbProvider
{
    public static string Name => "BatchedDialect";
    public static SqlDialect Dialect => SqlDialect.MySql;
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
        => new SqliteConnection(connectionString);
    public static string QuoteIdentifier(string identifier) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema) ? QuoteIdentifier(identifier) : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";
    public static bool SupportsReturningClause => false;
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";
    public static DbParameter CreateParameter(string name, object? value)
        => new SqliteParameter(name, value ?? DBNull.Value);
    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
#pragma warning disable S2077, CA2100 // 表名经 QuoteIdentifier 转义（标识符面）；测试夹具的 schema 探测不需要用户输入
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
#pragma warning restore S2077, CA2100
        return 1;
    }
}

[Table("tb_reuse")]
internal sealed partial class TbReuseEntity
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("qty")]
    public long Qty { get; set; }

    [Column("label")]
    public string Label { get; set; } = "";
}

[Table("tb_reuse_t")]
internal sealed partial class TbReuseTenantEntity
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("tenant_id")]
    public string TenantId { get; set; } = "";

    [Column("qty")]
    public long Qty { get; set; }
}
