using PalORM;

namespace PalORM.Core.Tests;

/// <summary>方言族载荷契约（B2：只发射会被读到的 SQL）。
/// <para>运行时按 <c>TProvider.SupportsReturningClause</c> 分发：PostgreSQL/SQLite 读
/// <c>InsertReturning</c>/<c>UpsertReturning</c>，MySQL 读 <c>InsertWithLastInsertId</c>/<c>UpsertMySql</c>；
/// <c>Insert</c> 全方言无消费者。生成器据此只发射本族载荷，另一族置空——
/// 实测省下注册文件 27% 的字符（SQL 载荷的 49%）。</para>
/// <para>本用例把"哪族该有值"钉成断言，使该优化不会退化成"某族 SQL 被误清空"：
/// 清空正确字段与误清空必需字段，在只跑功能用例时都表现为"通过"（后者只在使用该方言时才炸），
/// 逐字段断言才是可失败的仪器。</para></summary>
internal sealed class CommandSqlSetDialectContractTests
{
    [Test]
    public async Task CommandSqlSet_OnlyCarriesThePayloadItsDialectFamilyReads()
    {
        var sets = PalORM_Runtime.CommandSqlsByDialect[typeof(BulkCleanupEntity)];

        // PostgreSQL / SQLite 族：RETURNING 变体有值，MySQL 专有载荷为空
        foreach ((string dialect, CommandSqlSet set) in
            new[] { ("Sqlite", sets.Sqlite), ("PostgreSql", sets.PostgreSql) })
        {
            await Assert.That(set.Update).IsNotEmpty();
            await Assert.That(set.Delete).IsNotEmpty();
            await Assert.That(set.InsertReturning).IsNotEmpty();
            await Assert.That(set.UpsertReturning).IsNotEmpty();
            await Assert.That(set.UpsertMySql).IsEmpty();
            await Assert.That(set.InsertWithLastInsertId).IsEmpty();
            await Assert.That(set.Insert).IsEmpty(); // 全方言无消费者
            _ = dialect;
        }

        // MySQL 族：LAST_INSERT_ID 与 ON DUPLICATE KEY 有值，RETURNING 变体为空
        await Assert.That(sets.MySql.Update).IsNotEmpty();
        await Assert.That(sets.MySql.Delete).IsNotEmpty();
        await Assert.That(sets.MySql.UpsertMySql).IsNotEmpty();
        await Assert.That(sets.MySql.InsertWithLastInsertId).IsNotEmpty();
        await Assert.That(sets.MySql.InsertReturning).IsEmpty();
        await Assert.That(sets.MySql.UpsertReturning).IsEmpty();
        await Assert.That(sets.MySql.Insert).IsEmpty();
    }

    [Test]
    public async Task SqliteAndPostgreSqlPayloads_AreByteIdentical()
    {
        // 两族 SQL 逐位相同（同一套双引号引用 + ON CONFLICT 语法）——这是"SQLite 复用 PG 实例"
        // 那条优化被否掉的依据：C# 编译器本就把相同字面量在元数据串堆里存一份，
        // 共享实例只省一次构造调用的 IL（实测约 50 B/实体），不值得引入局部变量与发射分支。
        var sets = PalORM_Runtime.CommandSqlsByDialect[typeof(BulkCleanupEntity)];

        await Assert.That(sets.Sqlite.Update).IsEqualTo(sets.PostgreSql.Update);
        await Assert.That(sets.Sqlite.Delete).IsEqualTo(sets.PostgreSql.Delete);
        await Assert.That(sets.Sqlite.InsertReturning).IsEqualTo(sets.PostgreSql.InsertReturning);
        await Assert.That(sets.Sqlite.UpsertReturning).IsEqualTo(sets.PostgreSql.UpsertReturning);
    }
}
