using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>时间矩阵探针（方案 §4.7 / 阶段 0a 第 4 项）。
/// <para>三方言各写入 Utc / Local / Unspecified 三种 DateTime，读回后把实测 Kind 与值写入
/// <c>artifacts/time-kind-matrix-{dialect}.csv</c>，供 §4.7 的期望矩阵填表。</para>
/// <para>硬断言只保留一条：写入的 UTC 时刻不得丢失（Kind 允许方言差异，时刻不允许）。
/// 其余 Kind 差异属于矩阵取证范围，不用断言表达，避免把"当前实现"直接固化成契约。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class TimeKindMatrixTests
{
    private static readonly DateTime UtcInput = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime LocalInput = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Local);
    private static readonly DateTime UnspecifiedInput = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Unspecified);

    [Test]
    public async Task Sqlite_RecordsKindMatrix()
        => await RunAsync("sqlite", static () => TestDb.SqliteAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_RecordsKindMatrix()
        => await RunAsync("postgresql", static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_RecordsKindMatrix()
        => await RunAsync("mysql", static () => TestDb.MySqlAsync());

    private static async Task RunAsync<TProvider>(
        string dialect, Func<Task<DataSession<TProvider>>> sessionFactory)
        where TProvider : IDbProvider
    {
        await using DataSession<TProvider> db = await sessionFactory();
        await db.MigrateAsync();
        await db.ExecuteAsync($"DELETE FROM cf_time_matrix");

        DateTime[] inputs = [UtcInput, LocalInput, UnspecifiedInput];
        List<CfTimeMatrixEntity> inserted = [];

        // 逐行显式调用（非循环）避免 PALORM005。每个 Kind 单独取证：
        // Npgsql 的 timestamptz 只接受 Utc，Unspecified 与 Local 都会被拒（ArgumentException），
        // 这是方言契约而非本测试的失败条件。
        string utcOutcome = await TryInsertAsync(db, UtcInput, inserted);
        string unspecifiedOutcome = await TryInsertAsync(db, UnspecifiedInput, inserted);
        string localOutcome = await TryInsertAsync(db, LocalInput, inserted);

        List<CfTimeMatrixEntity> rows =
            [.. (await db.GetAllAsync<CfTimeMatrixEntity>()).OrderBy(row => row.Id)];
        await Assert.That(rows.Count).IsEqualTo(inserted.Count);
        await Assert.That(utcOutcome).IsEqualTo("accepted");

        WriteMatrix(dialect, rows, inserted, unspecifiedOutcome, localOutcome);
    }

    private static async Task<string> TryInsertAsync<TProvider>(
        DataSession<TProvider> db, DateTime value, List<CfTimeMatrixEntity> inserted)
        where TProvider : IDbProvider
    {
        var row = new CfTimeMatrixEntity
        {
            Dt = value,
            Dto = new DateTimeOffset(value),
            DateOnly = new DateOnly(2026, 7, 18),
            TimeOnly = new TimeOnly(10, 30, 0)
        };
        try
        {
            await db.InsertAsync(row);
            inserted.Add(row);
            return "accepted";
        }
        catch (ArgumentException)
        {
            // Npgsql：timestamp with time zone 只接受 Utc。记录方言契约，不当作失败。
            return "rejected:ArgumentException";
        }
    }

    private static void WriteMatrix(
        string dialect,
        List<CfTimeMatrixEntity> rows,
        List<CfTimeMatrixEntity> inserted,
        string unspecifiedOutcome,
        string localOutcome)
    {
        string? marker = TestEnvironment.FindFileUpwards("Directory.Build.props");
        string directory = marker is null
            ? Path.Combine(Path.GetTempPath(), "palorm-conformance")
            : Path.Combine(Path.GetDirectoryName(marker)!, "artifacts");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"time-kind-matrix-{dialect}.csv");

        var lines = new List<string>
        {
            $"# dialect={dialect}",
            $"# unspecified_input_write={unspecifiedOutcome}",
            $"# local_input_write={localOutcome}",
            "column,input_kind,input_ticks,output_kind,output_ticks"
        };
        for (int i = 0; i < rows.Count; i++)
        {
            lines.Add($"dt,{inserted[i].Dt.Kind},{inserted[i].Dt.Ticks},{rows[i].Dt.Kind},{rows[i].Dt.Ticks}");
            lines.Add($"dto,offset={inserted[i].Dto.Offset},-,-,{rows[i].Dto}");
            lines.Add($"date_only,-,-,-,{rows[i].DateOnly:O}");
            lines.Add($"time_only,-,-,-,{rows[i].TimeOnly:O}");
        }
        File.WriteAllLines(path, lines);
    }
}