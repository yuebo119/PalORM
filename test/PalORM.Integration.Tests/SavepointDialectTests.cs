using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>保存点跨方言契约（R4，v5.7）——与 AdvancedFeatureTests 的 SQLite 基础用例互补：
/// PG/MySQL 真库锁定「QuoteIdentifier 转义在 SAVEPOINT/ROLLBACK TO 语法中有效」，
/// 含各方言自己的引号字符（PG 双引号 / MySQL 反引号）内嵌于名字的往返；
/// 并锁定非法名（空/空白/NUL）在库内统一拒绝、不触 SQL。</summary>
internal sealed class SavepointDialectTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task PostgreSql_Savepoint_NameWithDialectQuote_RoundTrips()
    {
        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
        });
        await SavepointRoundTripAsync(session, @"sp""quoted");
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task MySql_Savepoint_NameWithDialectQuote_RoundTrips()
    {
        await using var session = await DataSession<MySqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
        });
        await SavepointRoundTripAsync(session, "sp`quoted");
    }

    [Test]
    public async Task Savepoint_IllegalNames_RejectedBeforeSql()
    {
        // 非法名三形态（空/空白/NUL）双方法统一拒绝——ArgumentException 指向参数。
        // 用真实事务让校验路径可区分：tran 守卫不触发，异常只能来自名称校验
        await using var db = await TestDb.SqliteAsync();
        await using DbTransaction tran = await db.BeginTransactionAsync();
        await Assert.That(async () => await db.SavepointAsync(tran, "")).Throws<ArgumentException>();
        await Assert.That(async () => await db.SavepointAsync(tran, "   ")).Throws<ArgumentException>();
        await Assert.That(async () => await db.SavepointAsync(tran, "a\0b")).Throws<ArgumentException>();
        await Assert.That(async () => await db.RollbackToAsync(tran, "")).Throws<ArgumentException>();
        await Assert.That(async () => await db.RollbackToAsync(tran, "a\0b")).Throws<ArgumentException>();
    }

    /// <summary>方言内引号字符嵌入保存点名：SAVEPOINT 与 ROLLBACK TO 两侧都经
    /// QuoteIdentifier 转义（PG "sp""quoted" / MySQL `sp``quoted`），两侧转义不一致
    /// 即「保存点不存在」失败——本用例由此锁定转义正确性而非仅"名字能用"。
    /// 注意 DDL 用无插值洞的字面量：ExecuteAsync 的插值洞会被参数化，标识符不能是参数。</summary>
    private static async Task SavepointRoundTripAsync<TProvider>(
        DataSession<TProvider> session, string savepointName)
        where TProvider : IDbProvider
    {
        // 表名内嵌双下划线避开与 SessionBatchDialectTests 的并发建表冲突（同 NotInParallel 组内串行，双保险）
        await session.ExecuteAsync($"DROP TABLE IF EXISTS savepoint__dialect__rows");
        await session.ExecuteAsync(
            $"CREATE TABLE savepoint__dialect__rows (id BIGINT PRIMARY KEY, tag TEXT NOT NULL)");
        await session.ExecuteAsync(
            $"INSERT INTO savepoint__dialect__rows (id, tag) VALUES ({1L}, {"before"})");
        try
        {
            await SavepointRollbackCoreAsync(session, savepointName);

            // 保存点后的插入被回滚，保存点前的保留（此时事务已释放，内部残留走静默清理；
            // 已 Commit 未 Dispose 的事务附着命令是未定义形态——文档契约「Commit 后需
            // 再次设置或清空」，Npgsql 在 set_DbTransaction 即拒绝，故断言放释放后）
            await Assert.That(
                await session.ScalarAsync<long>($"SELECT COUNT(*) FROM savepoint__dialect__rows"))
                .IsEqualTo(1L);
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS savepoint__dialect__rows");
        }
    }

    private static async Task SavepointRollbackCoreAsync<TProvider>(
        DataSession<TProvider> session, string savepointName)
        where TProvider : IDbProvider
    {
        await using DbTransaction tran = await session.BeginTransactionAsync();
        await session.SavepointAsync(tran, savepointName);
        await session.ExecuteAsync(
            $"INSERT INTO savepoint__dialect__rows (id, tag) VALUES ({2L}, {"after"})");
        await session.RollbackToAsync(tran, savepointName);
        await tran.CommitAsync();
    }
}
