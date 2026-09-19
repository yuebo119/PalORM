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
/// 排除矩阵（八项 Npgsql 原生对照全部成功）：SQL 文本 1/4 行、参数名数、两种绑定方式、
/// 事务有无、auto-prepare 两项复刻、同语句两次执行、NpgsqlParameter(name,value) 构造器、
/// <b>CreateConnection 全量调优复刻</b>（NoResetOnClose/16384 缓冲区/Enlist=false）——原生
/// 路径不可复现。PG 侧 InitializeConnectionAsync 未覆写（no-op）已核实。剩余假说：dump 的
/// CommandText 与实际发送文本存在<b>不可见差异</b>（sink 按字符串 dump，回车符与 U+00A0 等不可见字符不显示；
/// POSITION 22 与表名字节区吻合）——下一轮 dump 逐字符码点裁决。修复任务在账本 M2-2 跟进（十一项矩阵定格，下一招 cmd.Clone 换连接/Npgsql 网络日志）。</para></summary>
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

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BulkUpdateBatch_WritesCorrectValuesPerRow()
        => await RunRoundTripAsync(await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts));

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkUpdateBatch_WritesCorrectValuesPerRow()
        => await RunRoundTripAsync(await DataSession<MySqlProvider>.CreateAsync(MySqlOpts));

    /// <summary>共享端到端：4 行 2 列批量更新后逐行逐列断言最终值（非仅行数）——
    /// 参数错位（第 i 行的值绑到第 j 行）即刻暴露为值断言失败。
    /// <para><b>根因终章（2026-09-20）</b>：此前的"42601 真缺陷"是<b>本测试自身的 DDL 缺陷</b>——
    /// 表名曾写在 FormattableString 洞里（<c>$"DROP TABLE IF EXISTS {TableName}"</c> → 参数化
    /// <c>DROP TABLE IF EXISTS @p0</c>，PG 的 DDL 不接受参数化标识符；POSITION 22 恰是
    /// "$1" 在 <c>DROP TABLE IF EXISTS </c>（21 字符）之后的位置，MySqlConnector 客户端插值
    /// 则成单引号表名）。表名改字面量后产品路径全绿——BulkUpdateBatchAsync 本身无缺陷
    /// （十六项排除矩阵的全部弯路源于"失败点在 UPDATE"的错误公设）。教训：标识符必须
    /// 写字面量（全项目真库测试先例皆如此），只有值才进洞。</para></summary>
    private static async Task RunRoundTripAsync<TProvider>(DataSession<TProvider> db)
        where TProvider : IDbProvider
    {
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_batch_upd");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_batch_upd (id INT PRIMARY KEY, qty INT NOT NULL, label VARCHAR(32) NOT NULL)");
            for (int i = 1; i <= 4; i++)
                await db.ExecuteAsync($"INSERT INTO palorm_batch_upd (id, qty, label) VALUES ({(long)i}, {0L}, {"seed" + i})");

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
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_batch_upd");
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
