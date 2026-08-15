using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>v5.0 阶段 5.2：SessionSetupSql 单元测试。
/// 用 SQLite 真实执行验证：用户 SQL 在连接首次激活后执行，影响后续查询。</summary>
public sealed class SessionSetupSqlTests
{
    private static DbOptions Opts(string? setupSql, string? readSetupSql = null) => new()
    {
        ConnectionString = "Data Source=:memory:",
        SessionSetupSql = setupSql,
        ReadSessionSetupSql = readSetupSql
    };

    [Test]
    public async Task SessionSetupSql_Null_Default_DoesNotExecute()
    {
        // 默认 null 不应抛异常（向后兼容）
        await using var session = await DataSession<SqliteProvider>.CreateAsync(Opts(null));
        await Assert.That(session).IsNotNull();
    }

    [Test]
    public async Task SessionSetupSql_EmptyOrWhitespace_TreatedAsNotSet()
    {
        // 空白字符串等价于 null（IsNullOrWhitespace 判断）
        await using var session = await DataSession<SqliteProvider>.CreateAsync(Opts("   "));
        await Assert.That(session).IsNotNull();
    }

    [Test]
    public async Task SessionSetupSql_ExecutedAfterProviderInit_AffectsQuery()
    {
        // 用 SQLite PRAGMA 验证：cache_size 改变后 PRAGMA cache_size 返回新值
        // 主连接初始化顺序：Provider InitializeConnectionAsync (foreign_keys/WAL) → SessionSetupSql
        await using var session = await DataSession<SqliteProvider>.CreateAsync(
            Opts("PRAGMA cache_size = -5000"));  // 自定义 cache_size（区别于阶段 3.3 的默认 -65536）

        long cacheSize = await session.ScalarAsync<long>($"PRAGMA cache_size");
        await Assert.That(cacheSize).IsEqualTo(-5000);
    }

    [Test]
    public async Task SessionSetupSql_MultipleStatements_ExecutedInOneRound()
    {
        // 多条 SQL 用分号分隔，一次执行
        await using var session = await DataSession<SqliteProvider>.CreateAsync(
            Opts("PRAGMA cache_size = -3000; PRAGMA temp_store = MEMORY;"));

        long cacheSize = await session.ScalarAsync<long>($"PRAGMA cache_size");
        await Assert.That(cacheSize).IsEqualTo(-3000);
        // SQLite temp_store 枚举：DEFAULT=0, FILE=1, MEMORY=2
        long tempStore = await session.ScalarAsync<long>($"PRAGMA temp_store");
        await Assert.That(tempStore).IsEqualTo(2);
    }

    [Test]
    public async Task SessionSetupSql_InvalidSyntax_ThrowsAndFailsSessionCreation()
    {
        // 无效 SQL 应该让 CreateAsync 失败（连接初始化失败语义）——SQLite 驱动原样传播
        // SqliteException（语法错误非瞬时异常，不重试），锁定具体类型而非裸 Exception。
        await Assert.That(async () =>
            await DataSession<SqliteProvider>.CreateAsync(Opts("NOT A VALID SQL STATEMENT")))
            .Throws<Microsoft.Data.Sqlite.SqliteException>();
    }
}
