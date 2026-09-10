using System.Data.Common;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

public sealed class PostgreSqlIntegrationTests
{
    // r21/ITM-770：WhereJson 的 DryRun 测试已迁 Core.Tests（WhereJsonSqlGenerationTests）——
    // 方言守卫使"SQLite 会话验证 PG SQL"的旧模式失效，新测试构造 PG 方言 builder（不触库）。

    private static DbOptions Opts => new()
    {
        ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
    };

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_HealthCheck_Succeeds()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(Opts);
        await Assert.That((await db.HealthCheckAsync()).IsHealthy).IsTrue();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_DDL_Insert_Query_RoundTripsData()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(Opts);
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS pg_test CASCADE");
            await db.ExecuteAsync($"CREATE TABLE pg_test (id BIGSERIAL PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");
            await db.ExecuteAsync($"INSERT INTO pg_test (name, value) VALUES ({"PG"}, {42})");
            var c = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM pg_test WHERE name = {"PG"}");
            await Assert.That(c).IsEqualTo(1);
        }
        finally
        {
            // T6：断言失败也清理表
            await db.ExecuteAsync($"DROP TABLE IF EXISTS pg_test CASCADE");
        }
    }

    // v5.0 阶段 5.5b：AdvisoryXactLock 集成测试。
    // 验证 pg_advisory_xact_lock 在事务内的获取与自动释放语义。
    // 用 WithTransaction 让 PalORM 框架管理事务生命周期（自动 commit/rollback），
    // 而非手动 BeginTransactionAsync/Commit/Dispose（会与 SessionOperationState 冲突）。
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_AdvisoryXactLock_AcquiredAndAutoReleasedByTransaction()
    {
        // 事务 1：获取锁（提交后自动释放）
        await using var db1 = await DataSession<PostgreSqlProvider>.CreateAsync(Opts);
        await db1.WithTransaction(async ct =>
        {
            await db1.AcquireXactLockAsync(987654321L, ct);

            // 锁已被 db1 持有，db2 的 TryAcquire 应返回 false
            await using var db2 = await DataSession<PostgreSqlProvider>.CreateAsync(Opts, CancellationToken.None);
            await db2.WithTransaction(async ct2 =>
            {
                bool acquired = await db2.TryAcquireXactLockAsync(987654321L, ct2);
                await Assert.That(acquired).IsFalse();
            }, ct: CancellationToken.None);  // 测试不取消（db2 是辅助连接）
        });

        // db1 事务结束后锁释放，db3 可重新获取
        await using var db3 = await DataSession<PostgreSqlProvider>.CreateAsync(Opts);
        bool reacquired = false;
        await db3.WithTransaction(async ct =>
        {
            reacquired = await db3.TryAcquireXactLockAsync(987654321L, ct);
        });
        await Assert.That(reacquired).IsTrue();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_AdvisoryXactLock_DualKey_IndependentFromSingleKey()
    {
        // 单 bigint key 和双 int key 是独立锁空间——同 key 不冲突
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(Opts);
        bool singleKeyAcquired = false;
        await db.WithTransaction(async ct =>
        {
            // 用双 int key (1,2) 对应的值作为单 bigint key，验证两者不冲突
            await db.AcquireXactLockAsync(1, 2, ct);
            // 单 bigint key 同值也能获取（独立锁空间）
            long mappedKey = ((long)1 << 32) | 2u;
            singleKeyAcquired = await db.TryAcquireXactLockAsync(mappedKey, ct);
        });
        await Assert.That(singleKeyAcquired).IsTrue();
    }
}

#region Test Entities
[Table("pg_test")]
public partial class PgEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}
#endregion
