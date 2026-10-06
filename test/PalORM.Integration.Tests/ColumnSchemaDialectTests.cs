using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// R3（v6.0）：[Column] 类型细化的外库真约束验证（PG/MySQL——外库离线时由 CI 全量集成执行；
/// SQLite 侧的 DDL 形态由三方言快照锁定 + 类型亲和性不截断契约文档化）。
/// 挂 ExtBulkTable 组与其余 PG 建表用例串行。
/// <para><b>P1-V6-01 登记（2026-09-28 实测）</b>：Pg_Varchar 的 ThrowsAsync(22001 value too long)
/// 之后，同进程 Npgsql 池的下一个会话 ExecuteScalar 偶发 "Received unexpected backend message
/// DataRow"（Bind 阶段协议错误后的连接归池状态残留）。对调用例声明序（ThrowsAsync 用例置尾）
/// 规避——现象留待专门诊断（触发面：同池跨会话 + 紧跟 ExecuteScalar 的收窄插入路径）。</para>
/// <para><b>P1-V6-01 缓解升级（2026-10-06）</b>：声明序缓解在 CI（PG17 + 跨类并行；同组
/// NotInParallel 只保证互斥不保证顺序）被击穿两次（release.yml 发布门禁实测拦截）——
/// ThrowsAsync 用例改池隔离（Pooling=false），异常连接随会话关闭不回流，与顺序解耦。</para>
/// </summary>
[NotInParallel("ExtBulkTable")]
public sealed class ColumnSchemaDialectTests
{
    [Test]
    public async Task Pg_DecimalPrecision_ScalesValue()
    {
        await using var db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();

        ColProbeEntity inserted = await db.InsertAsync(new ColProbeEntity { Name = "p", Amount = 1.239m });
        ColProbeEntity fetched = (await db.GetAsync<ColProbeEntity>(inserted.Id))!;
        // DECIMAL(10,2)：超出 scale 的位数四舍五入
        await Assert.That(fetched.Amount).IsEqualTo(1.24m);
    }

    [Test]
    public async Task Pg_VarcharLength_EnforcedByDatabase()
    {
        // 锚点：撤掉 GetDbType 的 Length 拦截 → 列回 TEXT → 插入 65 字符成功（红）
        // 池隔离（P1-V6-01，见类注释）：22001 异常后的连接归池存在跨会话协议状态残留。
        // 原缓解靠"声明序置尾"，但同组 NotInParallel 只保证互斥不保证执行顺序，CI
        // （PG17 + 跨类并行）两次被击穿（release.yml 发布门禁）。Pooling=false 让异常
        // 连接随会话关闭、不回流任何池，缓解与执行顺序解耦。
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(
            new DbOptions
            {
                ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString() + ";Pooling=false"
            });
        await db.MigrateAsync();

        await db.InsertAsync(new ColProbeEntity { Name = new string('a', 64), Amount = 1.23m });
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async ()
            => await db.InsertAsync(new ColProbeEntity { Name = new string('b', 65), Amount = 1m }));
    }

    [Test]
    public async Task MySql_VarcharLength_EnforcedByDatabase()
    {
        await using var db = await TestDb.MySqlAsync();
        await db.MigrateAsync();

        await db.InsertAsync(new ColProbeEntity { Name = new string('a', 64), Amount = 1.23m });
        await Assert.ThrowsAsync<MySqlConnector.MySqlException>(async ()
            => await db.InsertAsync(new ColProbeEntity { Name = new string('b', 65), Amount = 1m }));
    }
}

#region Test Entities
[Table("col_probe")]
public partial class ColProbeEntity
{
    [Key] public long Id { get; set; }
    [Column("name", Length = 64)] public string Name { get; set; } = "";
    [Column("amount", Precision = 10, Scale = 2)] public decimal Amount { get; set; }
}
#endregion
