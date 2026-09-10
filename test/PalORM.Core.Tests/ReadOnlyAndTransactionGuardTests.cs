using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;
using PalORM.PostgreSql;

namespace PalORM.Core.Tests;

/// <summary>r21 批次B 回归：ITM-766（只读 SQLite）/ ITM-767（ITM-640 持续失败）/ ITM-770（WhereJson 方言守卫）。</summary>
public sealed class ReadOnlyAndTransactionGuardTests
{
    [Test]
    public async Task WhereJson_OnNonPostgreSqlDialect_Throws()
    {
        // ITM-770：WhereJson 硬编码 PG 的 ->> 与双引号标识符——MySQL 默认 sql_mode 下
        // "col" 是字符串字面量，误用会静默错误结果。非 PG 方言必须明确拒绝（G31）。
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

        Exception? exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            _ = session.From<MaskingE2EEntity>().WhereJson("payload", "k", "v");
            await Task.CompletedTask;
        });
        await Assert.That(exception!.Message).Contains("PostgreSQL");
    }
    [Test]
    public async Task ReadOnlyFileDatabase_InitializesWithoutWal()
    {
        // ITM-766：Mode=ReadOnly 走文件库宽分支时 journal_mode=WAL 抛 SqliteException
        // Error 8（真库探针实证）——会话创建整体失败。修复后只读库走窄分支可正常初始化。
        string file = Path.Combine(Path.GetTempPath(), $"palorm-ro-{Guid.NewGuid():N}.db");
        try
        {
            // 先以读写模式建库并写入数据（Pooling=False 确保连接释放后文件句柄归还，
            // finally 的 Delete 不被池化连接阻塞）
            await using (var seed = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await seed.OpenAsync();
                await using var cmd = seed.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT NOT NULL); INSERT INTO t (v) VALUES ('x')";
                await cmd.ExecuteNonQueryAsync();
            }

            // 只读模式创建会话——此前在此抛 Error 8
            await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
                new DbOptions { ConnectionString = $"Data Source={file};Mode=ReadOnly;Pooling=False" });

            DbCommand probe = session.GetRawConnection().CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM t";
            object? count = await probe.ExecuteScalarAsync();
            await Assert.That(Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(1L);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task ExternallyDisposedTransaction_EveryCommandFailsUntilCleared()
    {
        // ITM-767：ITM-640 的响亮失败原只有一次性——第一次抛出前清了状态，第二条命令起
        // 静默自动提交（写操作脱离事务无反馈）。修复后持续失败，直至 UseTransaction(null)。
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        DbTransaction transaction = await session.BeginTransactionAsync();
        session.UseTransaction(transaction);
        await transaction.DisposeAsync();

        // 第一条命令：响亮失败
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.ExecuteAsync($"SELECT 1"));
        // 第二条命令：必须仍然失败（修复前此处静默自动提交成功）
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.ExecuteAsync($"SELECT 1"));

        // 逃生门：显式清场后恢复
        session.UseTransaction(null);
        int affected = await session.ExecuteAsync($"SELECT 1");
        await Assert.That(affected).IsEqualTo(-1);  // SQLite 对 SELECT 返回 -1
    }
}
