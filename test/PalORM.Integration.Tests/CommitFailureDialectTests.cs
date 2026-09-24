using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>提交失败后的回滚裁决契约（T1，v5.6.0）：PG 的 DEFERRABLE INITIALLY DEFERRED
/// 唯一约束把冲突推迟到 COMMIT 时检查——这是唯一能<b>确定性</b>触发「CommitAsync 失败」
/// 的手段。锁定三点：主异常是约束冲突本身（不被回滚噪音替换/掩盖）、Data 带
/// PalORM.RollbackSkipped 标记（而非 RollbackException——跳过的回滚不是失败的回滚）、
/// 事务确已终结（后续操作可正常执行）。
/// <para><b>SQLite 例外</b>：失败的 COMMIT（SQLITE_BUSY）保留活动事务、必须回滚——
/// Core.Tests 的常规回滚用例覆盖该分支；MySQL 无 DEFERRABLE 语义，无法确定性触发，
/// 与 PG 共享同一服务端终结行为，不单列用例。</para></summary>
[NotInParallel("ExtBulkTable")]
internal sealed class CommitFailureDialectTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task PostgreSql_CommitFailure_SkipsRollback_AndKeepsPrimaryException()
    {
        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS commit_failure_rows");
        await session.ExecuteAsync(
            $"CREATE TABLE commit_failure_rows (k INT NOT NULL, CONSTRAINT uq_commit_failure UNIQUE (k) DEFERRABLE INITIALLY DEFERRED)");
        try
        {
            Exception? thrown = await Assert.ThrowsAsync<Exception>(async () =>
                await session.WithTransaction(async ct =>
                {
                    await session.ExecuteAsync(
                        $"INSERT INTO commit_failure_rows (k) VALUES ({1})", ct);
                    await session.ExecuteAsync(
                        $"INSERT INTO commit_failure_rows (k) VALUES ({1})", ct);
                    return true;
                }));
            ArgumentNullException.ThrowIfNull(thrown);

            // 主异常不被掩盖；跳过以 RollbackSkipped 标记（RollbackException 缺席 =
            // 没有发起注定失败的回滚）。键名字面量即契约锁——internal const 不对集成
            // 测试可见，恰好让键名漂移在此处炸出
            await Assert.That(thrown.Data.Contains("PalORM.RollbackSkipped")).IsTrue();
            await Assert.That(thrown.Data.Contains("PalORM.RollbackException")).IsFalse();

            // 事务已终结：连接回到可用状态（自动提交成功插入）
            await session.ExecuteAsync($"INSERT INTO commit_failure_rows (k) VALUES ({2})");
            await Assert.That(
                await session.ScalarAsync<long>($"SELECT COUNT(*) FROM commit_failure_rows"))
                .IsEqualTo(1L);
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS commit_failure_rows");
        }
    }
}
