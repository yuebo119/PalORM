using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>方言差异专项断言测试——验证 PG/MySQL/SQLite 三方言在关键场景的行为一致性。
/// <para>这些测试在 SQLite 内存库上跑（无外部 DB 依赖），验证 SQL 生成和 ORM 层语义。
/// 真库 PG/MySQL 行为由 ExternalDatabaseBulkTests + AotTest 覆盖。</para></summary>
public sealed class DialectDifferenceTests
{
    // ─── INSERT 回填策略差异 ───────────────────────────────

    /// <summary>PG/SQLite 走 RETURNING 单次往返物化整行；MySQL 走 LAST_INSERT_ID 回填 ID。
    /// 验证 SupportsReturningClause 属性在三个 Provider 上的值正确。</summary>
    [Test]
    public async Task SupportsReturningClause_Dialect_Gate_IsCorrect()
    {
        await Assert.That(PalORM.Sqlite.SqliteProvider.SupportsReturningClause).IsTrue();
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.SupportsReturningClause).IsTrue();
        await Assert.That(PalORM.MySql.MySqlProvider.SupportsReturningClause).IsFalse();
    }

    // ─── 标识符引用差异 ─────────────────────────────────────

    /// <summary>SQLite/PG 用双引号，MySQL 用反引号。</summary>
    [Test]
    public async Task QuoteIdentifier_Dialect_Difference()
    {
        await Assert.That(PalORM.Sqlite.SqliteProvider.QuoteIdentifier("col"))
            .IsEqualTo("\"col\"");
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.QuoteIdentifier("col"))
            .IsEqualTo("\"col\"");
        await Assert.That(PalORM.MySql.MySqlProvider.QuoteIdentifier("col"))
            .IsEqualTo("`col`");
    }

    /// <summary>标识符内嵌 quote 字符的转义：双引号→"" / 反引号→``</summary>
    [Test]
    public async Task QuoteIdentifier_EscapesEmbeddedQuote()
    {
        await Assert.That(PalORM.Sqlite.SqliteProvider.QuoteIdentifier("a\"b"))
            .IsEqualTo("\"a\"\"b\"");
        await Assert.That(PalORM.MySql.MySqlProvider.QuoteIdentifier("a`b"))
            .IsEqualTo("`a``b`");
    }

    // ─── LIMIT OFFSET 方言差异 ─────────────────────────────

    /// <summary>SQLite/PG: LIMIT take OFFSET skip（当前实现 skip=0 也输出 OFFSET 0——见 TakeOnly 锁定）
    /// MySQL: LIMIT skip, take（skip=0 用 LIMIT 0, take）</summary>
    [Test]
    public async Task LimitOffset_DryRun_ProducesCorrectDialectSql()
    {
        // 用 SQLite 验证 SQL 生成（不依赖 PG/MySQL 连接）
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>().Take(10).Skip(20).AsDryRun();
        await Assert.That(dry.Sql).Contains("LIMIT 10");
        await Assert.That(dry.Sql).Contains("OFFSET 20");
    }

    [Test]
    public async Task LimitOffset_TakeOnly_GeneratesOffsetZero()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>().Take(5).AsDryRun();
        await Assert.That(dry.Sql).Contains("LIMIT 5");
        // SQLite/PG 生成 "LIMIT 5 OFFSET 0"（_skip ?? 0 默认 0）——OFFSET 0 合规
        await Assert.That(dry.Sql).Contains("OFFSET 0");
    }

    // ─── 参数占位符统一性 ─────────────────────────────────

    /// <summary>三个 Provider 都用 @p{N} 格式——IDbProvider.GetParameterPlaceholder 的
    /// static virtual 默认实现由三 Provider 共享（编译期保证），SQLite DryRun 验证生成侧。
    /// static virtual 成员不能直接通过类名调用，用 DryRun 间接验证。</summary>
    [Test]
    public async Task ParameterPlaceholder_SqliteDryRun_UsesAtPN()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>().Where($"Id = {42}").AsDryRun();
        await Assert.That(dry.Sql).Contains("@p0");
        // TUnit 1.x：HasCount() 已弃用，改用 Count() 提供完整数值断言链
        await Assert.That(dry.Parameters).Count().IsEqualTo(1);
    }

    // ─── UPSERT 方言分支 ───────────────────────────────────

    /// <summary>SaveAsync 对有 [ConcurrencyCheck] 的实体拒绝 UPSERT。
    /// 守卫在 Core 的 SaveCoreAsync 分发层（Provider 无关，三方言共用），故 SQLite 内存库
    /// 单方言执行即覆盖全方言语义；不涉及 DDL/连接差异。</summary>
    [Test]
    public async Task Upsert_ConcurrencyCheck_RejectedAcrossDialects()
    {
        await using var db = await TestDb.SqliteAsync();
        // 非默认主键 → SaveAsync 走 UPSERT 分支；UPSERT 无条件覆盖与乐观锁冲突 → 明确拒绝。
        // 异常在 CreateCommand 之前抛出，无需建表。
        Exception? ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await db.SaveAsync(new VersionedEntity { Id = 1, Name = "init", Version = 0 }));

        await Assert.That(ex!.Message).Contains("[ConcurrencyCheck]");
        await Assert.That(ex.Message).Contains("Use InsertAsync");
    }

    // ─── CurrentTimestamp 时区语义差异 ───────────────────

    /// <summary>SQLite CURRENT_TIMESTAMP 恒 UTC（ITM-326）；PG/MySQL 用会话时区。
    /// 三 Provider 的 CurrentTimestampExpression 属性文本相同（时区语义差异另见注释）。</summary>
    [Test]
    public async Task CurrentTimestampExpression_SameAcrossProviders()
    {
        await Assert.That(PalORM.Sqlite.SqliteProvider.CurrentTimestampExpression).IsEqualTo("CURRENT_TIMESTAMP");
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.CurrentTimestampExpression).IsEqualTo("CURRENT_TIMESTAMP");
        await Assert.That(PalORM.MySql.MySqlProvider.CurrentTimestampExpression).IsEqualTo("CURRENT_TIMESTAMP");
    }

    // ─── 唯一冲突判定差异 ───────────────────────────────

    /// <summary>SQLite: SqliteErrorCode 19/2067/1555；PG: SQLSTATE 23505；MySQL: ErrorCode DuplicateKeyEntry。
    /// 验证 IsUniqueViolation 各自识别自己的错误码。</summary>
    [Test]
    public async Task IsUniqueViolation_Dialect_ErrorCodes()
    {
        // 验证三 Provider 的 IsUniqueViolation 委托可调用且不抛异常
        bool sqliteResult = PalORM.Sqlite.SqliteProvider.IsUniqueViolation(
            new Microsoft.Data.Sqlite.SqliteException("test", 19, 2067));
        await Assert.That(sqliteResult).IsTrue();
        // T-P3-08：方法组 IsNotNull 是恒真断言（static abstract 存在性由编译保证）——改为行为断言：
        // 非数据库异常必须判 false（真阳路径由 ExternalDatabaseBulkTests 真库锁定）
        var neutral = new InvalidOperationException("not a database error");
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.IsUniqueViolation(neutral)).IsFalse();
        await Assert.That(PalORM.MySql.MySqlProvider.IsUniqueViolation(neutral)).IsFalse();
    }

    // ─── Bulk 策略差异 ───────────────────────────────────

    /// <summary>PG 走 Binary COPY；SQLite/MySQL 走多值 INSERT。
    /// 验证 BulkInsertAsync 在 SQLite 上正常工作（多值 INSERT 路径）。</summary>
    [Test]
    public async Task BulkInsert_Sqlite_UsesMultiValueInsert()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.ExecuteAsync($"CREATE TABLE IF NOT EXISTS bulk_dialect_test (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)");
        try
        {
            var entities = Enumerable.Range(0, 100)
                .Select(i => new BulkDialectEntity { name = $"row_{i}" }).ToArray();
            long inserted = await db.BulkInsertAsync(entities, batchSize: 50);
            await Assert.That(inserted).IsEqualTo(100);

            long count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM bulk_dialect_test");
            await Assert.That(count).IsEqualTo(100);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS bulk_dialect_test");
        }
    }
}

#region Test Entities
[Table("bulk_dialect_test")]
public sealed partial class BulkDialectEntity
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
}
#endregion
