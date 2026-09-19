using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// 独立审计 2026-09-19 M2-2（T3）——批量 UPDATE 参数化分支的<b>真库端到端</b>。
/// 此前该路径只有字符串层锁定（BatchUpdateSqlBuilderTests 自陈"PG/MySQL 批量 SQL 路径
/// 在 CI 中零执行覆盖"）；参数序错位是"静默写错行"型缺陷（每行 SET 值绑到相邻行的键），
/// 字符串比较无法发现执行期错绑。本组用例在 PG/MySQL 真库上逐行逐列断言<b>最终值</b>。
/// SQLite 按设计回退逐行路径（BatchUpdateParameterContractTests:9），不在此重复。
/// <para><b>当前状态（2026-09-19，根因排查矩阵见下——本红灯是缺陷存在的证据，不得跳过或删除）</b>：
/// PG 42601（at "$1", POSITION 22）/ MySQL 语法错，稳定复现于<b>多行</b>（4 行）；
/// <b>单行（1 实体）经 PalORM 真库成功</b>。已用 Npgsql 原生命令排除：SQL 文本（1/4 行
/// 均 dump 且与原生成功版逐字同构）、参数名/数量（@p0-@p11）、绑定方式（DBNull 初值后写
/// long/string）、事务（有/无均成功）、连接调优（MaxAutoPrepare=100 复刻亦成功）。
/// 剩余未排除差异：NpgsqlParameter(name, DBNull) 构造器路径 + 会话级语句状态（auto-prepare
/// 的跨执行计数假说：PalORM 语句在测试会话内重复出现触发 PREPARE，原生对照语句文本唯一
/// 永不达 MinUsages=2——待下一轮实验分离）。修复任务在整改账本 M2-2 跟进。</para></summary>
public sealed class BulkUpdateBatchDialectTests
{
    private static DbOptions PgOpts => new()
    {
        ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
    };

    private static DbOptions MySqlOpts => new()
    {
        ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
    };

    private const string TableName = "palorm_batch_upd";

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BulkUpdateBatch_WritesCorrectValuesPerRow()
        => await RunRoundTripAsync(await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts), "INT");

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkUpdateBatch_WritesCorrectValuesPerRow()
        => await RunRoundTripAsync(await DataSession<MySqlProvider>.CreateAsync(MySqlOpts), "INT");

    /// <summary>共享端到端：4 行 2 列批量更新后逐行逐列断言最终值（非仅行数）——
    /// 参数错位（第 i 行的值绑到第 j 行）即刻暴露为值断言失败。</summary>
    private static async Task RunRoundTripAsync<TProvider>(DataSession<TProvider> db, string intType)
        where TProvider : IDbProvider
    {
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS {TableName}");
            await db.ExecuteAsync(
                $"CREATE TABLE {TableName} (id {intType} PRIMARY KEY, qty {intType} NOT NULL, label VARCHAR(32) NOT NULL)");
            for (int i = 1; i <= 4; i++)
                await db.ExecuteAsync($"INSERT INTO {TableName} (id, qty, label) VALUES ({(long)i}, {0L}, {"seed" + i})");

            var rows = new List<BatchUpdEntity>();
            for (int i = 1; i <= 4; i++)
                rows.Add(new BatchUpdEntity { Id = i, Qty = i * 10, Label = "upd-" + i });

            long affected = await db.BulkUpdateBatchAsync(rows);

            await Assert.That(affected).IsEqualTo(4);
            var after = await db.From<BatchUpdEntity>().OrderBy(x => x.Id).ToListAsync();
            await Assert.That(after.Count).IsEqualTo(4);
            for (int i = 0; i < 4; i++)
            {
                // 逐行逐列最终值：id=i 行的 qty=i*10、label="upd-i"——错绑即失败
                await Assert.That(after[i].Qty).IsEqualTo((i + 1) * 10);
                await Assert.That(after[i].Label).IsEqualTo("upd-" + (i + 1));
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS {TableName}");
            await db.DisposeAsync();
        }
    }
}

[Table("palorm_batch_upd")]
internal sealed partial class BatchUpdEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }
    [Column("qty")]
    public long Qty { get; set; }
    [Column("label")]
    public string Label { get; set; } = "";
}
