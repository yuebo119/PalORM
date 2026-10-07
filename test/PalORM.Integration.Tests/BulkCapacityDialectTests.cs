using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// 批容量租户扣减与 local_infile 回退的 MySQL 真库收口（ITM-883/892，r24 待办收口）。
/// ITM-883：16 SET 列（SetColumnCount+1=17 整除 65535）租户实体 × 3855 行 BulkUpdate——
/// 修复前 batchSize=65535/17=3855 恰好满批，每语句 3855×17+1（租户）=65536 越协议上限
/// 整批失败；修复后 (65535-1)/17=3854，65519+1≤65535 分批成功。
/// 数据准备走 SET GLOBAL local_infile=OFF 强制多值插入（避开 BulkCopy 对显式主键的
/// WarningNullToNotNull 告警面，该告警属 ITM-709 防线的另一独立议题）。
/// ITM-892：探测缓存 TTL 窗口内服务端关闭 local_infile——修复前 MySqlBulkCopy 硬失败
/// 整批抛异常；修复后失败触发重探测，OFF 则回退多值重试整批成功（MySQL 8.4 真库实证：
/// OFF 失败形态为协议握手拒绝零行写入，回退无重复风险）。
/// 两测试同编组（SET GLOBAL 是实例级状态，CI 共享库下防窗口互扰；finally 恢复 ON）。
/// </summary>
public sealed class BulkCapacityDialectTests
{
    private static Itm883Row MakeRow(long id, long v) => new()
    {
        Id = id, TenantId = 7,
        F01 = v, F02 = v, F03 = v, F04 = v, F05 = v, F06 = v, F07 = v, F08 = v,
        F09 = v, F10 = v, F11 = v, F12 = v, F13 = v, F14 = v, F15 = v,
    };

    [Test]
    [NotInParallel("LocalInfileGlobal")]
    public async Task MySql_BulkUpdate_TenantEntity_FullBatch_StaysWithinPlaceholderLimit()
    {
        await using var session = await TestDb.MySqlAsync();
        // 数据准备强制多值插入（见类注释）；finally 恢复实例默认
        await session.ExecuteAsync($"SET GLOBAL local_infile=OFF");
        try
        {
            // 探针自建自清（B171 惯例）：DDL 硬编码（表名/列名进插值会成为绑定参数）
#pragma warning disable S2077, CA2100
            await session.ExecuteAsync($"DROP TABLE IF EXISTS itm883_batch_capacity_probe");
            await session.ExecuteAsync(
                $"CREATE TABLE itm883_batch_capacity_probe (`Id` BIGINT PRIMARY KEY, tenant_id BIGINT NOT NULL, f01 BIGINT NOT NULL, f02 BIGINT NOT NULL, f03 BIGINT NOT NULL, f04 BIGINT NOT NULL, f05 BIGINT NOT NULL, f06 BIGINT NOT NULL, f07 BIGINT NOT NULL, f08 BIGINT NOT NULL, f09 BIGINT NOT NULL, f10 BIGINT NOT NULL, f11 BIGINT NOT NULL, f12 BIGINT NOT NULL, f13 BIGINT NOT NULL, f14 BIGINT NOT NULL, f15 BIGINT NOT NULL)");
#pragma warning restore S2077, CA2100
            session.WithTenant(7);

            const int rowCount = 3855;  // 修复前恰好构成一批 3855 行（65535/17 的整除商）
            List<Itm883Row> rows = [.. Enumerable.Range(1, rowCount).Select(i => MakeRow(i, i))];
            await session.BulkInsertAsync(rows);

            // 修复前：单批 3855×17+1=65536 个占位符越 MySQL 协议上限，整批抛 MySqlException；
            // 修复后：3854 行/批分批成功（撤修复此调用变红）
            long updated = await session.BulkUpdateAsync(rows);

            await Assert.That(updated).IsEqualTo(rowCount);
            Itm883Row? sample = await session.GetAsync<Itm883Row>(1);
            await Assert.That(sample).IsNotNull();
            await Assert.That(sample!.F01).IsEqualTo(1);
            Itm883Row? tail = await session.GetAsync<Itm883Row>(rowCount);
            await Assert.That(tail).IsNotNull();
            await Assert.That(tail!.F15).IsEqualTo(rowCount);
        }
        finally
        {
            await session.ExecuteAsync($"SET GLOBAL local_infile=ON");
            await session.ExecuteAsync($"DROP TABLE IF EXISTS itm883_batch_capacity_probe");
        }
    }

    [Test]
    [NotInParallel("LocalInfileGlobal")]
    public async Task MySql_BulkInsert_ServerDisablesLocalInfile_FallsBackToMultiValue()
    {
        await using var session = await TestDb.MySqlAsync();
#pragma warning disable S2077, CA2100
        await session.ExecuteAsync($"DROP TABLE IF EXISTS itm892_fallback_probe");
        await session.ExecuteAsync(
            $"CREATE TABLE itm892_fallback_probe (id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");
#pragma warning restore S2077, CA2100
        try
        {
            await session.ExecuteAsync($"SET GLOBAL local_infile=ON");

            // 第一次插入：探测 ON → BulkCopy 路径成功（auto id + string/int 为已验证组合，
            // 对齐 MySql_BulkInsert_LocalInfileOn 的实体形态），探测结果进入 60s TTL 连接级缓存
            await session.BulkInsertAsync([
                new Itm892Row { Name = "first", Value = 10 },
            ]);

            // TTL 窗口内服务端关闭（模拟 DBA/托管环境配置变更）
            await session.ExecuteAsync($"SET GLOBAL local_infile=OFF");

            // 修复前：缓存仍 ON → MySqlBulkCopy 触碰已关闭的服务端能力，整批抛
            // MySqlException("Loading local data is disabled...")；
            // 修复后：失败失效缓存并重探测 → OFF → 回退多值重试整批成功（撤修复此调用变红）
            await session.BulkInsertAsync([
                new Itm892Row { Name = "second", Value = 20 },
            ]);

            long count = await session.ScalarAsync<long>($"SELECT COUNT(*) FROM itm892_fallback_probe");
            await Assert.That(count).IsEqualTo(2);
            long total = await session.ScalarAsync<long>($"SELECT COALESCE(SUM(value), 0) FROM itm892_fallback_probe");
            await Assert.That(total).IsEqualTo(30);
        }
        finally
        {
            await session.ExecuteAsync($"SET GLOBAL local_infile=ON");  // 恢复实例级默认（CI 共享库）
            await session.ExecuteAsync($"DROP TABLE IF EXISTS itm892_fallback_probe");
        }
    }
}

#region Test Entities
[TenantAware]
[Table("itm883_batch_capacity_probe")]
public partial class Itm883Row
{
    // 显式主键（应用侧赋值）——默认 AutoIncrement=true 会被排除出 INSERT 列集，
    // 多值插入在 MySQL 严格模式下报 "Field 'Id' doesn't have a default value"
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("f01")] public long F01 { get; set; }
    [Column("f02")] public long F02 { get; set; }
    [Column("f03")] public long F03 { get; set; }
    [Column("f04")] public long F04 { get; set; }
    [Column("f05")] public long F05 { get; set; }
    [Column("f06")] public long F06 { get; set; }
    [Column("f07")] public long F07 { get; set; }
    [Column("f08")] public long F08 { get; set; }
    [Column("f09")] public long F09 { get; set; }
    [Column("f10")] public long F10 { get; set; }
    [Column("f11")] public long F11 { get; set; }
    [Column("f12")] public long F12 { get; set; }
    [Column("f13")] public long F13 { get; set; }
    [Column("f14")] public long F14 { get; set; }
    [Column("f15")] public long F15 { get; set; }
}

[Table("itm892_fallback_probe")]
public partial class Itm892Row
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}
#endregion
