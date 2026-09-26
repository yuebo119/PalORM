using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>T9/step7（P2-44 候选复现）：手工 FormattableString 的两种“重名参数”嫌疑形态。
/// <para><b>背景</b>：step5 P2-44 登记（标 [推断]）：手工构造的 FormattableString 复用下标
/// 产生重名参数，PG 按位置绑定可能绑出预期外组合。本组用两种候选形态实测当前行为：
/// ① 复用参数下标（<c>{0} … {0}</c>）；② 格式串内手写字面量 <c>@p0</c>。</para>
/// <para><b>判别标准</b>：静默绑错 = 必须修的缺陷；响亮失败（FormatException/驱动异常）
/// 或语义正确的单参双引 = 无需修，登记结论。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "连接生命周期移交 DataSession（同 FromAllocationTests 口径）；keeper 由调用方释放。")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2241",
    Justification = "形态②的测试点就是'格式串无格式项却传参'——手写字面量占位符的嫌疑形状，CA2241 正是被测行为。")]
public sealed class FormattableDuplicateParamTests
{
    /// <summary>复现辅助：把被测形态跑成 SQL 文本（AsDryRun 不经库）。</summary>
    private static async Task<string> RenderAsync<T>(FormattableString where)
        where T : class, new()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync();
        await using var _keeper = keeper;
        return session.From<T>().Where(where).AsDryRun().Sql;
    }

    [Test]
    public async Task ReusedArgumentIndex_RendersSingleParamTwice_NotDuplicateNames()
    {
        // 形态①：两个格式项复用同一参数下标——格式化器按参数下标命名、绑定按参数个数，
        // 预期产物 = 一个 @p0 在 SQL 里出现两次（PG 上 $1 双引，语义 = 该值参与两处比较）
        string sql = await RenderAsync<Post>(
            FormattableStringFactory.Create("\"id\" = {0} OR \"id\" = {0}", 7));
        await Assert.That(sql).Contains("@p0");
        await Assert.That(sql).DoesNotContain("@p1");
    }

    [Test]
    public async Task LiteralPlaceholderInFormatText_IsRejected()
    {
        // 形态②：格式串内手写字面量 @p0（无格式项）——@pN 保留命名空间，出现即抛。
        // 修复前：SQLite 静默按未绑定 NULL 返回空集，PG 响亮报"there is no parameter $1"。
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync();
        await using var _keeper = keeper;
        await Assert.That(() => session.From<Post>()
            .Where(FormattableStringFactory.Create("\"id\" = @p0", 7)))
            .Throws<InvalidOperationException>();
    }

    /// <summary>字面量 '@p' 后非数字（如搜索 '@people'）不受保留规则影响——只挡 @pN 占位符形态。</summary>
    [Test]
    public async Task LiteralAtPText_WithoutDigits_PassesThrough()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync();
        await using var _keeper = keeper;
        string sql = session.From<Post>()
            .Where(FormattableStringFactory.Create("\"text\" = {0}", "@people domain"))
            .AsDryRun().Sql;
        await Assert.That(sql).Contains("@people");
    }

    private static async Task<(DataSession<SqliteProvider> Session, SqliteConnection Keeper)> OpenAsync()
    {
        SqliteConnection keeper = new($"Data Source=dupparam_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();
        await using (var init = keeper.CreateCommand())
        {
            init.CommandText = "CREATE TABLE posts (id INTEGER PRIMARY KEY, text TEXT);";
            await init.ExecuteNonQueryAsync();
        }
        var conn = new SqliteConnection(keeper.ConnectionString);
        await conn.OpenAsync();
        return (new DataSession<SqliteProvider>(conn, new DbOptions { ConnectionString = keeper.ConnectionString }, [], null), keeper);
    }
}

[Table("posts")]
internal sealed partial class Post
{
    [Key(AutoIncrement = false)] [Column("id")] public int Id { get; set; }
    [Column("text")] public string? Text { get; set; }
}
