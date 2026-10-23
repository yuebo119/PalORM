using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>C16（2026-10-12 全量审读）：用户 WHERE 链的 AND/OR 优先级。
/// <para>旧行为：Where(A).OrWhere(B).Where(C) 生成 <c>(A) OR (B) AND (C)</c>——SQL 求值
/// <c>A OR (B AND C)</c>，与链式直觉 <c>((A) OR (B)) AND (C)</c> 不同（返回不同行集）。
/// 修复后：AND 出现在顶屋 OR 之后时为已累积组补括号；纯 AND/OR 链文本逐字不变。</para></summary>
public sealed class WhereOrPrecedenceTests
{
    [Test]
    public async Task Where_OrWhere_Where_GroupsOrRunBeforeAnd()
    {
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
#pragma warning disable PALORM005 // ToSql 纯构建不打数据库
        string sql = session.From<WhereOrProbeRow>()
            .Where($"qty > {5}").OrWhere($"name = {"x"}").Where($"id < {100}").ToSql();
#pragma warning restore PALORM005

        await Assert.That(sql).Contains("((qty > @p0) OR (name = @p1)) AND (id < @p2)");
    }

    [Test]
    public async Task PureAndChain_SqlTextUnchanged_NoExtraParens()
    {
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
#pragma warning disable PALORM005
        string sql = session.From<WhereOrProbeRow>()
            .Where($"qty > {5}").Where($"id < {100}").ToSql();
#pragma warning restore PALORM005

        await Assert.That(sql).Contains("(qty > @p0) AND (id < @p1)");
        await Assert.That(sql).DoesNotContain("((");
    }

    [Test]
    public async Task TwoOrRuns_GroupsEachAndSegment_NoUnbalancedParens()
    {
        // 2026-10-22 终审 P1-2：两次「OR 后接 AND」——旧实现只开一次括号却闭两次，
        // 产出多一个右括号的非法 SQL。
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
#pragma warning disable PALORM005
        string sql = session.From<WhereOrProbeRow>()
            .Where($"qty > {5}")
            .OrWhere($"name = {"x"}")
            .Where($"id < {100}")
            .OrWhere($"name = {"y"}")
            .Where($"id > {0}")
            .ToSql();
#pragma warning restore PALORM005

        await Assert.That(sql).Contains(
            "((qty > @p0) OR (name = @p1)) AND ((id < @p2) OR (name = @p3)) AND (id > @p4)");
        await Assert.That(sql.Count(c => c == '(')).IsEqualTo(sql.Count(c => c == ')'));
    }

    [Test]
    public async Task OrThenAnd_ExecutedRowsFollowSequentialSemantics()
    {
        // 行为锁定：数据构造使两种读法返回不同行集——
        // A(qty>100) 命中 {3}；B(name='x') 命中 {2,3}；C(id<3) 命中 {1,2}
        // (A∨B)∧C = {2}；A∨(B∧C) = {2,3}
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=whereor_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE where_or_probe (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Qty INTEGER NOT NULL)");
        await session.ExecuteAsync($"INSERT INTO where_or_probe (Id, Name, Qty) VALUES (1, 'a', 5)");
        await session.ExecuteAsync($"INSERT INTO where_or_probe (Id, Name, Qty) VALUES (2, 'x', 50)");
        await session.ExecuteAsync($"INSERT INTO where_or_probe (Id, Name, Qty) VALUES (3, 'x', 500)");

        List<WhereOrProbeRow> rows = await session.From<WhereOrProbeRow>()
            .Where($"qty > {100}").OrWhere($"name = {"x"}").Where($"id < {3}").ToListAsync();

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Id).IsEqualTo(2);
    }
}

[Table("where_or_probe")]
internal sealed partial class WhereOrProbeRow
{
    [Key(AutoIncrement = false)] [Column("Id")] public long Id { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
    [Column("Qty")] public int Qty { get; set; }
}
