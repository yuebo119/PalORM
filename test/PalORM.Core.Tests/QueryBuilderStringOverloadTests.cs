using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>属性名字符串重载（2026-10-31 极致性能轮）与表达式重载的逐字节等价锁定。
/// <para>覆盖面：OrderBy/ThenBy 族、GroupBy、WhereIn/WhereNotIn、Select、Set 的 SQL 文本
/// 逐字节一致；属性名未映射时的构建期失败；ThenBy 前置守卫；真库执行结果一致。</para>
/// <para>等价判据用 <see cref="string.Equals(string?, string?, StringComparison)"/> Ordinal——
/// 两种重载共享同一拼接内核，任何分叉都意味着 SQL 语义分叉。</para></summary>
internal sealed class QueryBuilderStringOverloadTests
{
    [Test]
    public async Task OrderBy_Family_String_Matches_Expression()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        string expression = session.From<StringOverloadRow>()
            .Where($"user_name = {"a"}")
            .OrderBy(static e => e.UserName)
            .ThenBy(static e => e.Age, descending: true)
            .ToSql();
        string @string = session.From<StringOverloadRow>()
            .Where($"user_name = {"a"}")
            .OrderBy(nameof(StringOverloadRow.UserName))
            .ThenBy(nameof(StringOverloadRow.Age), descending: true)
            .ToSql();
        await Assert.That(string.Equals(expression, @string, StringComparison.Ordinal)).IsTrue();

        // 降序便捷形态（OrderByDescending/ThenByDescending）
        string expressionDesc = session.From<StringOverloadRow>()
            .OrderByDescending(static e => e.Age)
            .ThenByDescending(static e => e.Id)
            .ToSql();
        string stringDesc = session.From<StringOverloadRow>()
            .OrderByDescending(nameof(StringOverloadRow.Age))
            .ThenByDescending(nameof(StringOverloadRow.Id))
            .ToSql();
        await Assert.That(string.Equals(expressionDesc, stringDesc, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task GroupBy_String_Matches_Expression()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        string expression = session.From<StringOverloadRow>()
            .GroupBy(static e => e.UserName)
            .ToSql();
        string @string = session.From<StringOverloadRow>()
            .GroupBy(nameof(StringOverloadRow.UserName))
            .ToSql();
        await Assert.That(string.Equals(expression, @string, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task WhereIn_String_Matches_Expression()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        long[] ids = [1L, 2L, 3L];

        string expressionIn = session.From<StringOverloadRow>().WhereIn(static e => e.Id, ids).ToSql();
        string stringIn = session.From<StringOverloadRow>().WhereIn(nameof(StringOverloadRow.Id), ids).ToSql();
        await Assert.That(string.Equals(expressionIn, stringIn, StringComparison.Ordinal)).IsTrue();

        string expressionNotIn = session.From<StringOverloadRow>().WhereNotIn(static e => e.Id, ids).ToSql();
        string stringNotIn = session.From<StringOverloadRow>().WhereNotIn(nameof(StringOverloadRow.Id), ids).ToSql();
        await Assert.That(string.Equals(expressionNotIn, stringNotIn, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Select_String_Matches_Expression()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        string expression = session.From<StringOverloadRow>()
            .Select(static e => e.UserName, static e => e.Age)
            .ToSql();
        string @string = session.From<StringOverloadRow>()
            .Select(nameof(StringOverloadRow.UserName), nameof(StringOverloadRow.Age))
            .ToSql();
        await Assert.That(string.Equals(expression, @string, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Set_String_Matches_Expression()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        string expression = session.From<StringOverloadRow>()
            .Where($"id = {1L}")
            .Set(static e => e.Age, 42)
            .ToSql();
        string @string = session.From<StringOverloadRow>()
            .Where($"id = {1L}")
            .Set(nameof(StringOverloadRow.Age), 42)
            .ToSql();
        await Assert.That(string.Equals(expression, @string, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task UnknownPropertyName_Throws_AtBuildTime()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        // 未映射属性名（NotMapped/拼写错误）必须构建期响亮失败——静默回退属性名会引用不存在列
        await Assert.That(() => session.From<StringOverloadRow>().OrderBy("Nope"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => session.From<StringOverloadRow>().GroupBy("Nope"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => session.From<StringOverloadRow>().WhereIn("Nope", [1L]))
            .Throws<InvalidOperationException>();
        await Assert.That(() => session.From<StringOverloadRow>().Select("Nope"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => session.From<StringOverloadRow>().Set("Nope", 1))
            .Throws<InvalidOperationException>();
        await Assert.That(() => session.From<StringOverloadRow>().OrderBy(null!))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ThenBy_String_WithoutOrderBy_Throws()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await Assert.That(() => session.From<StringOverloadRow>().ThenBy(nameof(StringOverloadRow.Age)))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Execution_String_Matches_Expression()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE string_overload_rows (id INTEGER PRIMARY KEY, user_name TEXT NOT NULL, age INTEGER NOT NULL)");
        await session.ExecuteAsync($"INSERT INTO string_overload_rows (id, user_name, age) VALUES ({1L}, {"a"}, {30})");
        await session.ExecuteAsync($"INSERT INTO string_overload_rows (id, user_name, age) VALUES ({2L}, {"b"}, {20})");

        List<StringOverloadRow> byExpression = await session.From<StringOverloadRow>()
            .Where($"age > {10}")
            .OrderBy(static e => e.Age)
            .ToListAsync();
        List<StringOverloadRow> byString = await session.From<StringOverloadRow>()
            .Where($"age > {10}")
            .OrderBy(nameof(StringOverloadRow.Age))
            .ToListAsync();

        await Assert.That(byString.Count).IsEqualTo(2);
        await Assert.That(byString[0].Id).IsEqualTo(byExpression[0].Id);
        await Assert.That(byString[0].UserName).IsEqualTo(byExpression[0].UserName);
        await Assert.That(byString[1].Age).IsEqualTo(byExpression[1].Age);

        // 更新路径：Set 字符串重载真库执行
        int affected = await session.From<StringOverloadRow>()
            .Where($"id = {1L}")
            .Set(nameof(StringOverloadRow.Age), 99)
            .ExecuteNonQueryAsync();
        await Assert.That(affected).IsEqualTo(1);
        StringOverloadRow updated = (await session.From<StringOverloadRow>().Where($"id = {1L}").ToListAsync())[0];
        await Assert.That(updated.Age).IsEqualTo(99);
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = $"Data Source=string_overload_{Guid.NewGuid():N};Mode=Memory",
            MaxRetries = 0,
            CircuitBreakerThreshold = 0,
        });
}

/// <summary>属性名与列名故意不同（UserName→user_name）——证明字符串重载走 PropertyToColumn
/// 映射而非把属性名当列名。</summary>
[Table("string_overload_rows")]
internal sealed partial class StringOverloadRow
{
    [Key] public long Id { get; set; }
    [Column("user_name")] public string UserName { get; set; } = "";
    [Column("age")] public int Age { get; set; }
}
