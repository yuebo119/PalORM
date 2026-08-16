using System.Data.Common;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

public sealed class PostgreSqlIntegrationTests
{
    private static DbOptions Opts => new()
    {
        ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
    };

    [Test]
    public async Task WhereJson_GeneratesQuotedColumnWithBoundPathAndValue()
    {
        // SQL 生成不依赖 PG 连接：扩展方法只操作 builder，DryRun 即可验证。
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>()
            .WhereJson("payload", "name", "Alice")
            .AsDryRun();

        await Assert.That(dry.Sql).Contains("\"payload\"->>@p0 = @p1");
        await Assert.That(dry.Parameters.Count).IsEqualTo(2);
        await Assert.That(dry.Parameters[0].Value).IsEqualTo("name");
        await Assert.That(dry.Parameters[1].Value).IsEqualTo("Alice");
    }

    [Test]
    public async Task WhereJson_QuotesColumnIdentifier_DoubleQuoteEscaped()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>()
            .WhereJson("payload\"x", "k", 1)
            .AsDryRun();

        // 双写转义：嵌入的 " 不能提前闭合标识符
        await Assert.That(dry.Sql).Contains("\"payload\"\"x\"->>@p0");
    }

    [Test]
    public async Task WhereJson_NonStringValue_NormalizedToInvariantString()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>()
            .WhereJson("payload", "count", 42)
            .AsDryRun();

        // ->> 返回 text：int 值归一为字符串绑定，避免 PG 端 text = integer 类型错误
        await Assert.That(dry.Parameters[1].Value).IsEqualTo("42");
    }

    [Test]
    public async Task WhereJson_BoolValue_NormalizedToLowercase_MatchesJsonbText()
    {
        // ITM-610：jsonb ->> 提取的布尔 text 恒为小写 "true"；Convert.ToString(bool) 产 "True"
        // 首字母大写——text 相等比较大小写敏感，恒不匹配 → 静默空结果。bool 必须特判小写。
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>()
            .WhereJson("payload", "active", true)
            .AsDryRun();

        await Assert.That(dry.Parameters[1].Value).IsEqualTo("true");
    }

    [Test]
    public async Task WhereJson_DateTimeValue_ThrowsExplicitly()
    {
        // ITM-641(r4)/662 锁定：DateTime 区域格式与 jsonb ISO text 恒不相等——
        // 显式拒绝优于静默空结果（格式对齐留真库实现窗口）
        await using var db = await TestDb.SqliteAsync();

        await Assert.That(() => db.From<Product>()
            .WhereJson("payload", "when", System.DateTime.Now))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task WhereJson_DateOnlyValue_ThrowsExplicitly()
    {
        // r19/ITM-683：DateOnly invariant 输出 MM/dd/yyyy 与 jsonb ISO text 恒不相等
        // （探针实证）——同 ITM-641 族显式拒绝，防静默空结果
        await using var db = await TestDb.SqliteAsync();

        await Assert.That(() => db.From<Product>()
            .WhereJson("payload", "day", new System.DateOnly(2026, 8, 16)))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task WhereJson_TimeOnlyValue_ThrowsExplicitly()
    {
        // r19/ITM-683：TimeOnly invariant 输出 H:mm 与 ISO HH:mm:ss 恒不相等——同族拒绝
        await using var db = await TestDb.SqliteAsync();

        await Assert.That(() => db.From<Product>()
            .WhereJson("payload", "at", new System.TimeOnly(10, 30, 0)))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task WhereJson_NulInValue_ThrowsArgumentException()
    {
        // r19/ITM-701：value 与 column/path 同口径 NUL 显式拒绝
        await using var db = await TestDb.SqliteAsync();

        await Assert.That(() => db.From<Product>().WhereJson("payload", "k", "a\0b"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task WhereJson_NulInColumn_ThrowsArgumentException()
    {
        await using var db = await TestDb.SqliteAsync();

        await Assert.That(() => db.From<Product>().WhereJson("pay\0load", "k", "v"))
            .Throws<ArgumentException>();
    }

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
            await db.ExecuteAsync($"DROP TABLE IF EXISTS pg_test");
            await db.ExecuteAsync($"CREATE TABLE pg_test (id BIGSERIAL PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");
            await db.ExecuteAsync($"INSERT INTO pg_test (name, value) VALUES ({"PG"}, {42})");
            var c = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM pg_test WHERE name = {"PG"}");
            await Assert.That(c).IsEqualTo(1);
        }
        finally
        {
            // T6：断言失败也清理表
            await db.ExecuteAsync($"DROP TABLE IF EXISTS pg_test");
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
