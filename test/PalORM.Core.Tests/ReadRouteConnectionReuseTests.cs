using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>读路由连接的会话级复用契约。
/// <para><b>可证伪的观察点</b>：<see cref="DbOptions.ReadSessionSetupSql"/> 在每次"读连接被
/// 建立"时执行一次。若连接按会话复用，多次 <c>ForRead()</c> 查询后该 SQL 只留下一行记录；
/// 若退化为"每查询新建连接"（v5.6 之前的行为），行数会等于查询次数。
/// <c>ReadRoute_FreshSessionBuildsFreshConnection</c> 是对照组，证明该计数具备区分力。</para>
/// <para>这比"查询结果正确"强：结果正确在两种实现下都成立，只有副作用计数能区分它们。</para></summary>
public sealed class ReadRouteConnectionReuseTests
{
    // 每个用例独立的库名：TUnit 默认并行执行，共用内存库会互相污染计数
    private const string SetupProbeSql = "INSERT INTO setup_runs (id) VALUES (NULL)";

    [Test]
    public async Task ReadRoute_ReusesOneConnectionPerSession()
    {
        await using SqliteConnection mainKeeper = await OpenAsync(MainConnectionString("reuse"));
        await using SqliteConnection readKeeper = await OpenAsync(ReadConnectionString("reuse"));
        await PrepareAsync(mainKeeper);
        await PrepareAsync(readKeeper, seedRow: true);

        await using var session = await DataSession<SqliteProvider>.CreateAsync(ReadOptions("reuse"));
#pragma warning disable PALORM005 // 本测试的观察点正是"多次查询只建一次连接"，循环次数是自变量
        for (int i = 0; i < 5; i++)
        {
            List<ValueSemanticsEntity> rows = await session
                .From<ValueSemanticsEntity>().ForRead().ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(1);
        }
#pragma warning restore PALORM005

        await Assert.That(await CountSetupRunsAsync(readKeeper)).IsEqualTo(1L);
    }

    [Test]
    public async Task ReadRoute_FreshSessionBuildsFreshConnection()
    {
        await using SqliteConnection mainKeeper = await OpenAsync(MainConnectionString("fresh"));
        await using SqliteConnection readKeeper = await OpenAsync(ReadConnectionString("fresh"));
        await PrepareAsync(mainKeeper);
        await PrepareAsync(readKeeper, seedRow: true);

        // 对照组：每新建一个会话就多一次读连接建立——证明上面的 1 行不是偶然
#pragma warning disable PALORM005 // 同上：会话数与连接建立次数的对应关系是本测试的自变量
        for (int i = 0; i < 3; i++)
        {
            await using var session = await DataSession<SqliteProvider>.CreateAsync(ReadOptions("fresh"));
            _ = await session.From<ValueSemanticsEntity>().ForRead().ToListAsync();
        }
#pragma warning restore PALORM005

        await Assert.That(await CountSetupRunsAsync(readKeeper)).IsEqualTo(3L);
    }

    [Test]
    public async Task ReadRoute_WithoutReadConnectionString_FallsBackToMain()
    {
        string mainCs = MainConnectionString("fallback");
        await using SqliteConnection mainKeeper = await OpenAsync(mainCs);
        await PrepareAsync(mainKeeper, seedRow: true, seedName: "m");

        await using var session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = mainCs });

        // 未配置读连接串时读路由退化为主连接（不抛异常、结果来自主库）
        List<ValueSemanticsEntity> rows = await session
            .From<ValueSemanticsEntity>().ForRead().ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("m");
    }

    private static string MainConnectionString(string tag)
        => $"Data Source=read_reuse_main_{tag};Mode=Memory;Cache=Shared";

    private static string ReadConnectionString(string tag)
        => $"Data Source=read_reuse_read_{tag};Mode=Memory;Cache=Shared";

    private static DbOptions ReadOptions(string tag) => new()
    {
        ConnectionString = MainConnectionString(tag),
        ReadConnectionString = ReadConnectionString(tag),
        ReadSessionSetupSql = SetupProbeSql,
    };

    private static async Task<SqliteConnection> OpenAsync(string connectionString)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>在读库建立探针表（setup_runs）与实体表。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "Test fixture DDL with a constant, non-user-supplied schema string.")]
    private static async Task PrepareAsync(SqliteConnection conn, bool seedRow = false, string seedName = "r")
    {
        await using SqliteCommand create = conn.CreateCommand();
        create.CommandText =
            "CREATE TABLE IF NOT EXISTS value_semantics (id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "name TEXT NOT NULL, price REAL NOT NULL); " +
            "CREATE TABLE IF NOT EXISTS setup_runs (id INTEGER PRIMARY KEY AUTOINCREMENT)";
        await create.ExecuteNonQueryAsync();

        if (!seedRow) return;
        await using SqliteCommand seed = conn.CreateCommand();
        seed.CommandText = "INSERT INTO value_semantics (name, price) VALUES ($n, 1)";
        seed.Parameters.Add(new SqliteParameter("$n", seedName));
        await seed.ExecuteNonQueryAsync();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "Test fixture count query with a constant command text.")]
    private static async Task<long> CountSetupRunsAsync(SqliteConnection conn)
    {
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM setup_runs";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
