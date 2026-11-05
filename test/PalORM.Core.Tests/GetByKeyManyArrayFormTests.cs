using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>多键点读的 PostgreSQL 形态（<c>= ANY(@ids)</c>）锁定测试。
/// <para><b>为什么用方言重映射夹具</b>：PG 形态的判据是能力检测（生成物提供主键数组构造器
/// + Provider 的 <c>CreateArrayParameter</c> 对真实元素类型返回非 null），与
/// <see cref="BulkDeleteArrayFormTests"/> 同手法——报 PG 方言、底层 SQLite，
/// 由 <see cref="ArrayAnySqliteConnection"/> 把 <c>= ANY(…)</c> 重写为
/// <c>IN (SELECT value FROM json_each(…))</c>，于是真实的 SQL 文本、单数组参数契约与
/// 批循环全部可在无真库环境下锁定。真库侧由
/// <c>GetByKeyManyDialectTests.PostgreSql_GetByKeyMany_AnyArrayForm_ReturnsAllRows</c> 覆盖。</para>
/// <para><b>为什么必须有形状断言</b>：ANY 与 IN 在"结果对不对"上完全一致——能力检测写错而
/// 静默回退 IN 不会有任何红（B95 家族）。故断言语句里出现 <c>= ANY(</c> 且只有一个参数。</para>
/// <para><b>组键 ArrayFormFixtures</b>：与 BulkDelete/BulkUpdate 的数组形态测试共享静态采集钩子，
/// 必须同组串行（TestInfra 不变式）。</para></summary>
[NotInParallel("ArrayFormFixtures")]
internal sealed class GetByKeyManyArrayFormTests
{
    private static readonly List<(string Sql, int ParameterCount)> Shapes = [];

    private static async Task<DataSession<ArrayFormDialectProvider>> CreateSessionAsync()
    {
        Shapes.Clear();
        ArrayAnySqliteCommand.SetReaderRecorder((sql, count) => Shapes.Add((sql, count)));
        DataSession<ArrayFormDialectProvider> session = await DataSession<ArrayFormDialectProvider>.CreateAsync(
            new DbOptions
            {
                ConnectionString = $"Data Source=readmanyany_{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
            });
        await session.ExecuteAsync($"CREATE TABLE read_many_any (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        for (int i = 1; i <= 50; i++)
        {
            await session.ExecuteAsync($"INSERT INTO read_many_any (id, name) VALUES ({(long)i}, {"n" + i})");
        }
        return session;
    }

    [Test]
    public async Task PostgreSqlDialect_UsesSingleArrayParameter_AnyForm()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();

        var keys = new List<object>(50);
        for (long i = 1; i <= 50; i++) keys.Add(i);
        Shapes.Clear();
        List<ReadManyAnyRow> rows = await session.GetByKeyManyAsync<ReadManyAnyRow>(keys);

        // 语义：全部命中、值精确
        await Assert.That(rows.Count).IsEqualTo(50);
        await Assert.That(rows.All(static r => r.Name == "n" + r.Id)).IsTrue();
        // 形状：单语句 + 单参数 + = ANY（回退 IN 形态会产出 50 个 @pN，断言必红）
        await Assert.That(Shapes.Count).IsEqualTo(1);
        await Assert.That(Shapes[0].Sql).Contains("= ANY(");
        await Assert.That(Shapes[0].ParameterCount).IsEqualTo(1);
    }

    [Test]
    public async Task PostgreSqlDialect_EmptyKeys_SendsNoSql()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();

        Shapes.Clear();
        List<ReadManyAnyRow> rows = await session.GetByKeyManyAsync<ReadManyAnyRow>(Array.Empty<object>());

        await Assert.That(rows.Count).IsEqualTo(0);
        await Assert.That(Shapes.Count).IsEqualTo(0);
    }
}

[Table("read_many_any")]
internal sealed partial class ReadManyAnyRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}
