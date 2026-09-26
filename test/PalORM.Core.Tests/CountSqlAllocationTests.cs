using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>T4（step7）：Count/聚合组合 SQL 的每调用分配基线（隔离单测口径）。
/// <para><b>背景</b>：CountBaseSqlCache（PERF-002）已缓存基底；未缓存的是
/// <c>WHERE defaultFilter</c> 组合与 <c>WHERE filter AND (where)</c> 组合——
/// 每次调用 2~4 个 concat 中间串。本组先建基线，再作为缓存化的 red/green 判据。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1215",
    Justification = "GC.GetTotalAllocatedBytes 基线测量；宽松 tripwire，非精密断言。")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "连接生命周期移交 DataSession（同 FromAllocationTests 口径）；keeper 由调用方释放。")]
public sealed class CountSqlAllocationTests
{
    private static async Task<(DataSession<SqliteProvider> Session, SqliteConnection Keeper)> OpenAsync(string tag)
    {
        string cs = $"Data Source=countalloc_{tag}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        SqliteConnection keeper = new(cs);
        await keeper.OpenAsync();
        await using (var init = keeper.CreateCommand())
        {
            init.CommandText = "CREATE TABLE filtered_entities (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, value INTEGER NOT NULL, deleted_at TEXT);";
            await init.ExecuteNonQueryAsync();
        }
        var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        return (new DataSession<SqliteProvider>(conn, new DbOptions { ConnectionString = cs }, [], null), keeper);
    }

    private static async Task<double> MeasureAsync(Func<Task> call)
    {
        for (var i = 0; i < 200; i++) await call();
        long before = GC.GetTotalAllocatedBytes();
        const int n = 500;
        for (var i = 0; i < n; i++) await call();
        long after = GC.GetTotalAllocatedBytes();
        return (after - before) / (double)n;
    }

    [Test]
    public async Task CountAsync_FilterOnly_AllocationBaseline()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync("f");
        await using var _keeper = keeper;
        session.WithTenant(7L);
        double b = await MeasureAsync(() => session.CountAsync<FilteredEntity>().AsTask());
        await Assert.That(b).IsLessThan(30_000); // gross 回归线：实测 3740.85，并行污染量级 KB
    }

    [Test]
    public async Task CountAsync_WithWhere_AllocationBaseline()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync("w");
        await using var _keeper = keeper;
        session.WithTenant(7L);
        double b = await MeasureAsync(async () => _ = await session.CountAsync<FilteredEntity>($"\"value\" > {5}"));
        await Assert.That(b).IsLessThan(30_000); // gross 回归线：实测 4035.38
    }
}
