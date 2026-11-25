namespace PalORM.Core.Tests;

/// <summary>P0-1 批命令文本适配的契约锁定（实现见 <see cref="BatchCommandText"/>）。
/// <para>锁三件事：MySQL 只追加一个尾随空格；PostgreSQL 与 SQLite 原样返回——PG 的自动预编译
/// （<c>MaxAutoPrepare</c> / <c>AutoPrepareMinUsages</c>）按 SQL 文本作缓存键，给批文本加空格
/// 会让每批都表现为新形状，反而阻止自动预编译生效；适配后与原文本必然不同，这正是驱动
/// 预编译缓存精确匹配失配、批回到单包拼接的条件。</para></summary>
internal sealed class BatchCommandTextTests
{
    private const string Sql = "SELECT `Id` FROM `t` WHERE `Id` = @p0";

    [Test]
    public async Task MySql_AppendsExactlyOneTrailingSpace()
    {
        string adapted = BatchCommandText.For(SqlDialect.MySql, Sql);

        await Assert.That(adapted).IsEqualTo(Sql + " ");
        await Assert.That(adapted).IsNotEqualTo(Sql);
    }

    [Test]
    public async Task MySql_WhenOriginalAlreadySpaceTerminated_StillDiffersFromOriginal()
    {
        // 原文本恰好以空格结尾时，适配后仍与原文本不同——驱动缓存（键为原文本）不会命中。
        const string spaced = Sql + " ";
        string adapted = BatchCommandText.For(SqlDialect.MySql, spaced);

        await Assert.That(adapted).IsNotEqualTo(spaced);
        await Assert.That(adapted).IsEqualTo(spaced + " ");
    }

    [Test]
    public async Task PostgreSql_LeavesTextUnchanged()
    {
        // 关键约束：不得"顺手统一"。PG 批是协议级真批，加空格会破坏驱动自动预编译的文本键。
        await Assert.That(BatchCommandText.For(SqlDialect.PostgreSql, Sql)).IsEqualTo(Sql);
    }

    [Test]
    public async Task Sqlite_LeavesTextUnchanged()
    {
        await Assert.That(BatchCommandText.For(SqlDialect.Sqlite, Sql)).IsEqualTo(Sql);
    }
}
