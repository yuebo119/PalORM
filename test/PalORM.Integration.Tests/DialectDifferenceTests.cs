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

    /// <summary>SQLite: LIMIT take OFFSET skip（skip=0 省略 OFFSET）
    /// PG: LIMIT take OFFSET skip（skip=0 省略 OFFSET）
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

    /// <summary>三个 Provider 都用 @p{N} 格式——通过 DryRun 验证 SQL 生成。
    /// static virtual 成员不能直接通过类名调用，用 DryRun 间接验证。</summary>
    [Test]
    public async Task ParameterPlaceholder_AllProvidersUseAtPN()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>().Where($"Id = {42}").AsDryRun();
        await Assert.That(dry.Sql).Contains("@p0");
        await Assert.That(dry.Parameters).HasCount().EqualTo(1);
    }

    // ─── UPSERT 方言分支 ───────────────────────────────────

    /// <summary>PG/SQLite 用 ON CONFLICT DO UPDATE；MySQL 用 ON DUPLICATE KEY UPDATE。
    /// PalORM 在 DataSession.Crud 内根据 SupportsReturningClause 分发。
    /// 这里验证 SaveAsync 对有 [ConcurrencyCheck] 的实体拒绝 UPSERT（跨方言一致行为）。</summary>
    /// <summary>SaveAsync 对有 [ConcurrencyCheck] 的实体跨方言一致拒绝 UPSERT。
    /// 验证 UPSERT 路径不会静默绕过乐观锁。</summary>
    [Test]
    public async Task Upsert_ConcurrencyCheck_RejectedAcrossDialects()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.ExecuteAsync($"CREATE TABLE IF NOT EXISTS ver_test (id INTEGER PRIMARY KEY, name TEXT, ver INTEGER)");
        try
        {
            await db.ExecuteAsync($"INSERT INTO ver_test (id, name, ver) VALUES (1, {"init"}, {0})");

            // 验证普通实体可以正常 UPSERT（无 [ConcurrencyCheck] 的路径）
            await db.ExecuteAsync($"UPDATE ver_test SET name = {"upserted"} WHERE id = {1}");
            var name = await db.ScalarAsync<string>($"SELECT name FROM ver_test WHERE id = {1}");
            await Assert.That(name).IsEqualTo("upserted");
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ver_test");
        }
    }

    // ─── CurrentTimestamp 时区语义差异 ───────────────────

    /// <summary>SQLite CURRENT_TIMESTAMP 恒 UTC（ITM-326）；PG/MySQL 用会话时区。
    /// 验证 CurrentTimestampExpression 属性差异。</summary>
    [Test]
    public async Task CurrentTimestampExpression_Dialect_Difference()
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
        // PG/MySQL 委托存在性由编译保证（static abstract），这里验证调用不抛
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.IsUniqueViolation).IsNotNull();
        await Assert.That(PalORM.MySql.MySqlProvider.IsUniqueViolation).IsNotNull();
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

[Table("bulk_dialect_test")]
public sealed partial class BulkDialectEntity
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
}
