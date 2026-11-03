using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>方言差异专项断言测试——验证 PG/MySQL/SQLite 三方言在关键场景的行为一致性。
/// <para>这些测试在 SQLite 内存库上跑（无外部 DB 依赖），验证 SQL 生成和 ORM 层语义。
/// 真库 PG/MySQL 行为由 ExternalDatabaseBulkTests + AotTest 覆盖。</para></summary>
public sealed class DialectDifferenceTests
{
    // 注：Provider 三方言的 SupportsReturningClause / QuoteIdentifier（含内嵌引号转义）契约
    // 由 PalORM.Core.Tests/ProviderTests 的 SqliteProvider_KeyMembers_BehaveAsExpected、
    // PostgreSqlProvider_KeyMembers_Compiled、MySqlProvider_KeyMembers_Compiled 与
    // Providers_QuoteInternalDelimitersAndQualifiedNames 四条用例锁定（逐字节同断言）。
    // 本文件只保留"跨方言差异形态"类用例，不再重复 Provider 属性取值。

    // ─── LIMIT OFFSET 方言差异 ─────────────────────────────

    /// <summary>SQLite/PG: LIMIT @take OFFSET @skip（SHAPE-010 参数化根解：值经 @pN 绑定，
    /// skip 缺省绑 0）；MySQL: LIMIT @skip, @take 位置形态（真库契约由 PG/MySQL CI 矩阵验证）。
    /// DryRun 同时断言参数快照的值与序（take 先 skip 后），值丢失/错位即刻暴露。</summary>
    [Test]
    public async Task LimitOffset_DryRun_ProducesCorrectDialectSql()
    {
        // 用 SQLite 验证 SQL 生成（不依赖 PG/MySQL 连接）
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>().Take(10).Skip(20).AsDryRun();
        await Assert.That(dry.Sql).Contains("LIMIT @p");
        await Assert.That(dry.Sql).Contains("OFFSET @p");
        await Assert.That(dry.Parameters.Count).IsEqualTo(2);
        await Assert.That((int)dry.Parameters[0].Value!).IsEqualTo(10);
        await Assert.That((int)dry.Parameters[1].Value!).IsEqualTo(20);
    }

    [Test]
    public async Task LimitOffset_TakeOnly_BindsOffsetZeroParameter()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>().Take(5).AsDryRun();
        // SQLite/PG 生成 "LIMIT @pN OFFSET @pM"（skip 缺省绑 0——SHAPE-010 前是文本
        // OFFSET 0，语义等价迁移：占位符必须绑 0）
        await Assert.That(dry.Sql).Contains("LIMIT @p");
        await Assert.That(dry.Sql).Contains("OFFSET @p");
        await Assert.That(dry.Parameters.Count).IsEqualTo(2);
        await Assert.That((int)dry.Parameters[0].Value!).IsEqualTo(5);
        await Assert.That((int)dry.Parameters[1].Value!).IsEqualTo(0);
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
        // 本用例的目的就是验证该调用被拒绝，PALORM054 在此属刻意触发
#pragma warning disable PALORM054
        Exception? ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await db.SaveAsync(new VersionedEntity { Id = 1, Name = "init", Version = 0 }));
#pragma warning restore PALORM054

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
}
