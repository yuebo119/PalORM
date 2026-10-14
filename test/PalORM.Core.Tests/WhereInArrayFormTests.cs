using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>P0-2（2026-10-12 全量重读）：WhereIn/WhereNotIn 的 PG 数组形态
///（<c>col = ANY(@pN)</c> / <c>col != ALL(@pN)</c>，单参数替代 N 个占位符）。
/// <para>形状断言经 DryRun/ToSql（不触库）；行为断言经 PG 方言夹具
///（<see cref="PgWhereInArrayProvider"/>，SQLite 底 + ANY/ALL → json_each 改写，与
/// ArrayFormDialectProvider 同构）。NULL 语义锁定：数组 NULL 元素在 = ANY 下永不匹配、
/// != ALL 含 NULL 时全行过滤（与占位符 IN/NOT IN 逐位等价）。</para>
/// <para><b>串行槽</b>：连接层复用 ArrayAnySqliteConnection——其语句采集钩子是静态的，
/// 须与 BulkDelete/BulkUpdate 数组形态族同挂 "ArrayFormFixtures" 组防串台（2026-10-13
/// 实锤：SoftDelete 批删断言偶发收到 5 条语句，含本类并发执行泄漏的 4 条）。</para></summary>
[NotInParallel("ArrayFormFixtures")]
public sealed class WhereInArrayFormTests
{
    [Test]
    public async Task WhereIn_ArrayForm_SingleParameterAndStableShape()
    {
        await using var session = await CreateSessionAsync();
        DryRunResult dry = session.From<WinArrayEntity>().WhereIn(x => x.Qty, AsList(10, 20, 30)).AsDryRun();
        await Assert.That(dry.Sql.Contains("= ANY(@p0)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(dry.Sql.Contains(" IN (", StringComparison.Ordinal)).IsFalse();
        await Assert.That(dry.Parameters.Count).IsEqualTo(1);

        // 形状稳定性：值数量变化不再改变 SQL 文本（占位符形态下每个数量都是新形状——
        // 这是形状缓存对 WhereIn 几乎无效的根因，数组形态的解耦收益）
        string sql3 = session.From<WinArrayEntity>().WhereIn(x => x.Qty, AsList(10, 20, 30)).ToSql();
        string sql10 = session.From<WinArrayEntity>().WhereIn(x => x.Qty, AsList(1, 2, 3, 4, 5, 6, 7, 8, 9, 10)).ToSql();
        await Assert.That(sql10).IsEqualTo(sql3);
    }

    [Test]
    public async Task WhereNotIn_ArrayForm_UsesAllOperator()
    {
        await using var session = await CreateSessionAsync();
        DryRunResult dry = session.From<WinArrayEntity>().WhereNotIn(x => x.Qty, AsList(10, 20)).AsDryRun();
        await Assert.That(dry.Sql.Contains("!= ALL(@p0)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(dry.Parameters.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ArrayParameterNumberingContinuesAfterWhereParameters()
    {
        await using var session = await CreateSessionAsync();
        DryRunResult dry = session.From<WinArrayEntity>()
            .Where($"Name = {"a"}")
            .WhereIn(x => x.Qty, AsList(10, 20))
            .AsDryRun();
        // Where 参数占 @p0，数组参数续编 @p1（统一编号空间不因形态切换断裂）
        await Assert.That(dry.Sql.Contains("= ANY(@p1)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(dry.Parameters.Count).IsEqualTo(2);
    }

    [Test]
    public async Task UnsupportedElementType_FallsBackToPlaceholders()
    {
        await using var session = await CreateSessionAsync();
        // char 不在 Provider 数组支持面（CreateArrayParameter 返回 null）——回退占位符形态，
        // SQL 与参数个数与既有行为逐位一致
        DryRunResult dry = session.From<WinArrayEntity>().WhereIn(x => x.Code, AsList('a', 'b')).AsDryRun();
        await Assert.That(dry.Sql.Contains(" IN (@p0, @p1)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(dry.Parameters.Count).IsEqualTo(2);
    }

    [Test]
    public async Task EmptyList_SemanticsUnchangedOnArrayCapableProvider()
    {
        await using var session = await CreateSessionAsync();
        DryRunResult inEmpty = session.From<WinArrayEntity>().WhereIn<int>(x => x.Qty, []).AsDryRun();
        await Assert.That(inEmpty.Sql.Contains("1=0", StringComparison.Ordinal)).IsTrue();
        await Assert.That(inEmpty.Parameters.Count).IsEqualTo(0);

        DryRunResult notInEmpty = session.From<WinArrayEntity>().WhereNotIn<int>(x => x.Qty, []).AsDryRun();
        await Assert.That(notInEmpty.Parameters.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WhereIn_ArrayForm_ReturnsMatchingRows()
    {
        await using var session = await CreateSessionAsync();
        await InsertAsync(session, (1, "a", 10), (2, "b", 20), (3, "c", 30));

        List<WinArrayEntity> rows = await session.From<WinArrayEntity>()
            .WhereIn(x => x.Qty, AsList(10, 30))
            .ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].Name).IsEqualTo("a");
        await Assert.That(rows[1].Name).IsEqualTo("c");

        // 与既有 Where 条件 AND 组合
        List<WinArrayEntity> combined = await session.From<WinArrayEntity>()
            .Where($"Name != {"a"}")
            .WhereIn(x => x.Qty, AsList(10, 30))
            .ToListAsync();
        await Assert.That(combined.Count).IsEqualTo(1);
        await Assert.That(combined[0].Name).IsEqualTo("c");
    }

    [Test]
    public async Task WhereNotIn_ArrayForm_ExcludesListedRows()
    {
        await using var session = await CreateSessionAsync();
        await InsertAsync(session, (1, "a", 10), (2, "b", 20), (3, "c", 30));

        List<WinArrayEntity> rows = await session.From<WinArrayEntity>()
            .WhereNotIn(x => x.Qty, AsList(20))
            .ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task WhereIn_NullElement_NeverMatches()
    {
        await using var session = await CreateSessionAsync();
        await InsertAsync(session, (1, "a", 10), (2, "b", 20));

        // 数组含 NULL 元素：= ANY 下 NULL 永不匹配（同 IN 的 NULL 分支语义）——
        // 只有 "b" 行命中
        List<WinArrayEntity> rows = await session.From<WinArrayEntity>()
            .WhereIn(x => x.Name, ["b", null!])
            .ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("b");
    }

    [Test]
    public async Task WhereNotIn_NullElement_FiltersAllRows_ClassicNotInSemantics()
    {
        await using var session = await CreateSessionAsync();
        await InsertAsync(session, (1, "a", 10), (2, "b", 20));

        // NOT IN 含 NULL 的经典语义：全行过滤（!= ALL 对 NULL 元素产生 unknown，逐位等价）。
        // 该行为是既有占位符形态的同款 gotcha——数组形态不得"顺手修好"它（静默改变语义）。
        List<WinArrayEntity> rows = await session.From<WinArrayEntity>()
            .WhereNotIn(x => x.Name, ["a", null!])
            .ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(0);
    }

    private static TValue[] AsList<TValue>(params TValue[] values) => values;

    private static async Task InsertAsync(
        DataSession<PgWhereInArrayProvider> session, params (int Id, string Name, int Qty)[] rows)
    {
        foreach ((int id, string name, int qty) in rows)
            await session.ExecuteAsync(
                $"INSERT INTO win_array_rows (Id, Name, Qty, Code) VALUES ({id}, {name}, {qty}, {"x"})");
    }

    private static async Task<DataSession<PgWhereInArrayProvider>> CreateSessionAsync()
    {
        DataSession<PgWhereInArrayProvider> session = await DataSession<PgWhereInArrayProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=win_array_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE win_array_rows (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Qty INTEGER NOT NULL, Code TEXT NOT NULL)");
        return session;
    }
}

/// <summary>P0-2 测试夹具：PG 方言 + 数组参数（JSON 数组文本承载）——char 元素返回 null
/// 模拟"元素类型不在 Provider 支持面"的回退判定。连接层经 ArrayAnySqliteConnection 把
/// <c>= ANY(@p)</c> / <c>!= ALL(@p)</c> 改写为 json_each 子查询在 SQLite 上执行。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "static abstract 接口成员经 DataSession<TProvider> 泛型消费，无实例化点（同 ArrayFormDialectProvider）。")]
internal sealed class PgWhereInArrayProvider : IDbProvider
{
    public static string Name => "PgWhereInArray";

    public static SqlDialect Dialect => SqlDialect.PostgreSql;

    public static DbConnection CreateConnection(string connectionString, DbOptions options)
        => new ArrayAnySqliteConnection(connectionString);

    public static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    public static bool SupportsReturningClause => true;

    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    public static DbParameter CreateParameter(string name, object? value)
        => new SqliteParameter(name, value ?? DBNull.Value);

    public static DbParameter? CreateArrayParameter(string name, Array values)
        => values.GetType().GetElementType() == typeof(char)
            ? null   // char 在支持面外：调用方回退占位符形态
            : new SqliteParameter(name, ArrayFormDialectProvider.ToJsonArray(values));

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        command.CommandText = "SELECT 1";
        return 0;
    }
}

[Table("win_array_rows")]
internal sealed partial class WinArrayEntity
{
    [Key] public long Id { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
    [Column("Qty")] public int Qty { get; set; }
    [Column("Code")] public char Code { get; set; }
}
