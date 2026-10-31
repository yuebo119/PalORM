using System.Runtime.CompilerServices;

namespace PalORM.Core.Tests;

/// <summary>方言敏感的 FormattableString 格式化回归。
/// <para>MySQL 的 <c>#</c> 行注释与反斜杠转义在 SQLite 规则下会被误解析：注释内的
/// <c>{0}</c> 会被当成插值点，<c>\'</c> 会被当成闭合引号。调用点必须显式传 Provider 方言。</para></summary>
public sealed class FormattableSqlDialectTests
{
    [Test]
    public async Task MySql_HashComment_DoesNotParameterizeBracesInsideComment()
    {
        FormattableString sql = FormattableStringFactory.Create(
            "SELECT 1 # order {0}\nFROM t WHERE id = {1}", 10, 20);

        string mysql = FormattableSqlFormatter.Format(
            sql.Format, 0, sql.ArgumentCount, SqlDialect.MySql);
        string sqlite = FormattableSqlFormatter.Format(
            sql.Format, 0, sql.ArgumentCount, SqlDialect.Sqlite);

        // MySQL：# 到行尾是注释，注释内的 {0} 不是插值点。
        await Assert.That(mysql).Contains("# order {0}");
        await Assert.That(mysql).Contains("id = @p1");
        await Assert.That(mysql).DoesNotContain("@p0");
        // SQLite 对照：同一文本按普通字符处理，{0} 会被参数化。
        await Assert.That(sqlite).Contains("# order @p0");
    }

    [Test]
    public async Task MySql_BackslashEscapedQuote_KeepsPlaceholderInsideString()
    {
        const string format = @"SELECT 'it\'s {0}' AS x";
        FormattableString sql = FormattableStringFactory.Create(format, 10);

        string mysql = FormattableSqlFormatter.Format(
            sql.Format, 0, sql.ArgumentCount, SqlDialect.MySql);
        string sqlite = FormattableSqlFormatter.Format(
            sql.Format, 0, sql.ArgumentCount, SqlDialect.Sqlite);

        // MySQL：\' 是转义引号，字符串未闭合，{0} 仍是字符串内容。
        await Assert.That(mysql).Contains(@"'it\'s {0}'");
        await Assert.That(mysql).DoesNotContain("@p0");
        // SQLite 对照：\' 被当作闭合引号，{0} 落到字符串外并被参数化。
        await Assert.That(sqlite).Contains("@p0");
    }
}
