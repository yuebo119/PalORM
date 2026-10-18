using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

// ExtBulkTable 为 B63 守卫（ExternalDbIsolationInvariantTests 要求每个 ExternalDatabase 用例
// 在类或方法级携带该字面量）。local_infile 的实例级状态互斥由 LocalInfileTestGate 信号量
// 保证——TUnit 的 [NotInParallel] 类级只能挂一个键，且方法级会覆盖类级，无法同时表达两组。
[NotInParallel("ExtBulkTable")]
public sealed class MySqlIntegrationTests
{
    private static DbOptions Opts => new()
    {
        ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
    };

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_HealthCheck_Succeeds()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(Opts);
        await Assert.That((await db.HealthCheckAsync()).IsHealthy).IsTrue();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_Execute_DDL_Works()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(Opts);
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_test");
            await db.ExecuteAsync($"CREATE TABLE mysql_test (id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");
            await db.ExecuteAsync($"INSERT INTO mysql_test (name, value) VALUES ({"MySQL"}, {42})");
            var c = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM mysql_test WHERE name = {"MySQL"}");
            await Assert.That(c).IsEqualTo(1);
        }
        finally
        {
            // P0 修复：确保断言失败也清理表——避免残留导致下次测试冲突
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_test");
        }
    }

    // v5.0 阶段 4.2：local_infile=ON 时无阈值走 BulkCopy；本测试锁行数与内容往返，
    // 分流策略本身无 SQL 形状断言（r19/T-P3-16 按实际行为改名）。
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkInsert_LocalInfileOn_InsertsAllRows()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(Opts);
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_bulk_test");
            await db.ExecuteAsync($"CREATE TABLE mysql_bulk_test (id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");

            // 用 10 行验证（远小于原 2000 阈值，证明无阈值分流）
            var entities = Enumerable.Range(0, 10)
                .Select(i => new MySqlBulkEntity { Name = $"row-{i}", Value = i })
                .ToArray();
            long affected = await db.BulkInsertAsync(entities);
            await Assert.That(affected).IsEqualTo(10);

            long count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM mysql_bulk_test");
            await Assert.That(count).IsEqualTo(10);

            var firstRow = await db.ScalarAsync<int>($"SELECT value FROM mysql_bulk_test WHERE name = {"row-0"}");
            await Assert.That(firstRow).IsEqualTo(0);
            var lastRow = await db.ScalarAsync<int>($"SELECT value FROM mysql_bulk_test WHERE name = {"row-9"}");
            await Assert.That(lastRow).IsEqualTo(9);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_bulk_test");
        }
    }

    /// <summary>BulkCopy 分块路径的真库覆盖（2026-10-18 审计补，MySQL 专属）。
    /// <para><b>为什么需要</b>：既有 BulkCopy 用例只插 1 行/10 行，MySqlBulkCopyInserter 的
    /// 分块物化（ExecuteBatchAsync 的批循环、ColumnLayout.Build、PrepareRowBinder、
    /// EntityDataReader 取值）在集成面从未被走到，覆盖率实测为 0%。</para>
    /// <para><b>路径确定性三条件</b>：①连接串追加 <c>AllowLoadLocalInfile=true</c>，同时
    /// 拿独立的能力探测缓存键（缓存按连接串键控，避免吃到其它用例留下的负结果窗口）；
    /// ②插入前无条件 <c>SET GLOBAL local_infile=ON</c> 并在 finally 还原原值——类级
    /// <c>LocalInfileGlobal</c> 分组保证不与 OFF 用例重叠；③断言
    /// <c>SELECT @@local_infile = 1</c>，服务端拒绝时直接失败，避免"以为在测 BulkCopy
    /// 实际走多值回退"的假覆盖。</para></summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkCopy_MultiChunkBatch_InsertsEveryRowExactlyOnce()
    {
        const int rows = 350;
        const int batchSize = 100;   // 350 行 = 4 块（100/100/100/50），跨块边界
        var opts = new DbOptions
        {
            // 追加驱动开关：MySqlBulkCopy 要求客户端显式开启 LOAD DATA LOCAL
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString() + ";AllowLoadLocalInfile=true",
        };
        await using var db = await DataSession<MySqlProvider>.CreateAsync(opts);
        await LocalInfileTestGate.Gate.WaitAsync();
        long savedLocalInfile = 1;
        try
        {
            savedLocalInfile = await db.ScalarAsync<long>($"SELECT @@local_infile");
            await db.ExecuteAsync($"SET GLOBAL local_infile=ON");
            await Assert.That(await db.ScalarAsync<long>($"SELECT @@local_infile")).IsEqualTo(1L);

            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_bulk_chunks");
            await db.ExecuteAsync(
                $"CREATE TABLE mysql_bulk_chunks (id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(64) NOT NULL, value INT NOT NULL)");

            var entities = Enumerable.Range(0, rows)
                .Select(i => new MySqlBulkChunkEntity { Name = $"chunk-{i}", Value = i })
                .ToArray();
            long affected = await db.BulkInsertAsync(entities, batchSize);
            await Assert.That(affected).IsEqualTo(rows);

            // 每行恰好一次：计数与去重计数相等，且值域覆盖 0..rows-1
            long count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM mysql_bulk_chunks");
            long distinct = await db.ScalarAsync<long>($"SELECT COUNT(DISTINCT value) FROM mysql_bulk_chunks");
            long min = await db.ScalarAsync<long>($"SELECT MIN(value) FROM mysql_bulk_chunks");
            long max = await db.ScalarAsync<long>($"SELECT MAX(value) FROM mysql_bulk_chunks");
            await Assert.That(count).IsEqualTo(rows);
            await Assert.That(distinct).IsEqualTo(rows);
            await Assert.That(min).IsEqualTo(0L);
            await Assert.That(max).IsEqualTo(rows - 1L);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_bulk_chunks");
            await db.ExecuteAsync($"SET GLOBAL local_infile={(savedLocalInfile == 0 ? "OFF" : "ON")}");
            // 恢复全局值后清掉探测缓存：否则本用例设的 ON 或遗留 OFF 结论会按连接串键
            // 残留到 TTL 到期，后续用例静默走多值回退（2026-10-18 审计根因）
            MySqlProvider.ResetLocalInfileProbeCache();
            LocalInfileTestGate.Gate.Release();
        }
    }
}

#region Test Entities
[Table("mysql_test")]
public partial class MySqlEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}

[Table("mysql_bulk_test")]
public partial class MySqlBulkEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}

[Table("mysql_bulk_chunks")]
public partial class MySqlBulkChunkEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}
#endregion
