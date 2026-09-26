using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>T3（step7）：<c>From&lt;T&gt;()</c> 每查询分配基线——隔离单测运行口径。
/// <para><b>为什么是单测运行而非套件内断言</b>：TUnit 默认并行，全局分配计数
/// （<c>GC.GetTotalAllocatedBytes</c>）会被同批其他用例污染——同文件池化测试的既有
/// 结论（"并行套件里被其他用例污染，不可断言"）。故本组只建立基线读数 + 宽松 tripwire
/// （宽于实测 5 倍以上），回归保护由 PerfProbe 隔离进程探针负责。</para>
/// <para><b>背景</b>：step5 的 P1-1 曾登记"From&lt;T&gt; 每查询重建过滤子句 116~400B"，
/// 编制期实地核实已由 PERF-002（格式串静态化）+ v5.6 S2743（DefaultFilterForms 三形态
/// 缓存）大部闭环；本组实测两形态残余量，判定是否还有值得做的项。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1215",
    Justification = "GC.GetTotalAllocatedBytes 基线测量；宽松 tripwire，非精密断言。")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "连接生命周期移交 DataSession（与 DefaultFilterFormsTests 等同文件测试的会话构造口径一致）；keeper 由调用方释放。")]
public sealed class FromAllocationTests
{
    private static async Task<(DataSession<SqliteProvider> Session, SqliteConnection Keeper)> OpenAsync(string tag)
    {
        string cs = $"Data Source=fromalloc_{tag}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        SqliteConnection keeper = new(cs);
        await keeper.OpenAsync();
        await using (var init = keeper.CreateCommand())
        {
            init.CommandText = "CREATE TABLE pool_basic (id INTEGER PRIMARY KEY, qty INTEGER NOT NULL, label TEXT NOT NULL);" +
                               "CREATE TABLE filtered_entities (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, value INTEGER NOT NULL, deleted_at TEXT);";
            await init.ExecuteNonQueryAsync();
        }
        var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        // DataSession 持有连接（会话构造即接管生命周期，与项目其余测试同口径）；
        // keeper 由调用方随测试释放（Cache=Shared 内存库需一条 keeper 连接保活）。
        return (new DataSession<SqliteProvider>(conn, new DbOptions { ConnectionString = cs }, [], null), keeper);
    }

    private static double MeasureBPerQuery(Action build)
    {
        for (var i = 0; i < 200; i++) build();          // 预热：JIT + 缓存首触
        long before = GC.GetTotalAllocatedBytes();
        const int n = 1000;
        for (var i = 0; i < n; i++) build();
        long after = GC.GetTotalAllocatedBytes();
        return (after - before) / (double)n;
    }

    [Test]
    public async Task FromT_NonTenant_AllocationBaseline()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync("plain");
        await using var _keeper = keeper;
        double bPerQuery = MeasureBPerQuery(() => _ = session.From<PoolBasic>());
        // tripwire：宽松 5 倍于编制期预期残余（~200B）——真回归（>1KB）才红
        await Assert.That(bPerQuery).IsLessThan(100);
    }

    [Test]
    public async Task FromT_Tenant_AllocationBaseline()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync("tenant");
        await using var _keeper = keeper;
        session.WithTenant(7L);
        double bPerQuery = MeasureBPerQuery(() => _ = session.From<FilteredEntity>());
        await Assert.That(bPerQuery).IsLessThan(1000);
    }
}
