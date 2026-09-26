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
                               "CREATE TABLE filtered_entities (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, value INTEGER NOT NULL, deleted_at TEXT);" +
                               "CREATE TABLE softonly_entities (id INTEGER PRIMARY KEY, value INTEGER NOT NULL, deleted_at TEXT);" +
                               "CREATE TABLE tenantonly_entities (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, value INTEGER NOT NULL);";
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
        // tripwire：抗并行污染的 gross 回归线（实测 <1B；并行套件污染量级 KB，精确基线只隔离跑有效）
        await Assert.That(bPerQuery).IsLessThan(20_000);
    }

    [Test]
    public async Task FromT_Tenant_AllocationBaseline()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync("tenant");
        await using var _keeper = keeper;
        session.WithTenant(7L);
        double bPerQuery = MeasureBPerQuery(() => _ = session.From<FilteredEntity>());
        await Assert.That(bPerQuery).IsLessThan(20_000);
    }

    /// <summary>T6/T3 分解：租户 334.92B 的构成——软删单形态 / 租户单形态 / 双形态三读数相减。
    /// 判定参数绑定 deferral 是否值得做（占大头的项才是标的）。</summary>
    [Test]
    public async Task FromT_Tenant_Decomposition()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync("decomp");
        await using var _keeper = keeper;
        session.WithTenant(7L);
        double softOnly = MeasureBPerQuery(() => _ = session.From<SoftOnlyEntity>());
        double tenantOnly = MeasureBPerQuery(() => _ = session.From<TenantOnlyEntity>());
        double both = MeasureBPerQuery(() => _ = session.From<FilteredEntity>());
        // T6 分解实测（2026-09-26 隔离口径）：softOnly=57.1 / tenantOnly=253.3 / both=302.1。
        // 判定：租户路径的 ~200B 是 From<T> 期参数绑定（List+NpgsqlParameter+FormattableString），
        // defer 到执行期需改子句模型（ADR-L 定型点在 From 期），收益仅惠及租户会话且 PerfHub 夹具
        // 不覆盖——登记为后续 ADR 项，本轮只做 scope 缓存（−32.8B）。
        await Assert.That(softOnly).IsLessThan(20_000);
        await Assert.That(tenantOnly).IsLessThan(20_000);
        await Assert.That(both).IsLessThan(20_000);
    }

    [Test]
    public async Task ReadPipeline_AllocationBaseline()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync("read");
        await using var _keeper = keeper;
        await using (var seed = keeper.CreateCommand())
        {
            seed.CommandText = "INSERT INTO filtered_entities (id, tenant_id, value, deleted_at) VALUES (1, 7, 10, NULL)";
            await seed.ExecuteNonQueryAsync();
        }
        session.WithTenant(7L);
        // 只读管线每查询分配（含默认弹性执行器 CTS+timer 的 272B 常数）——T5 的标的
        double bPerQuery = await MeasureReadAsync(session);
        await Assert.That(bPerQuery).IsLessThan(20_000); // gross 回归线；精确基线隔离跑
    }

    /// <summary>只读管线每查询分配测量——循环重复<b>同一条</b>查询是分配测量的必需形态
    /// （非 N+1：无嵌套查询、无可变键），PALORM005 的生产语义在此不适用。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "PALORM005",
        Justification = "分配基线测量循环：同语句重复执行是 GC.GetTotalAllocatedBytes 口径要求，非生产 N+1 形态。")]
    private static async Task<double> MeasureReadAsync(DataSession<SqliteProvider> session)
    {
        for (var i = 0; i < 200; i++)
            _ = await session.From<FilteredEntity>().ToListAsync();
        long before = GC.GetTotalAllocatedBytes();
        const int n = 500;
        for (var i = 0; i < n; i++)
            _ = await session.From<FilteredEntity>().ToListAsync();
        long after = GC.GetTotalAllocatedBytes();
        return (after - before) / (double)n;
    }
}

[SoftDelete]
[Table("softonly_entities")]
internal sealed partial class SoftOnlyEntity
{
    [Key] public long Id { get; set; }
    [Column("value")] public long Value { get; set; }
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}

[TenantAware]
[Table("tenantonly_entities")]
internal sealed partial class TenantOnlyEntity
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("value")] public long Value { get; set; }
}
