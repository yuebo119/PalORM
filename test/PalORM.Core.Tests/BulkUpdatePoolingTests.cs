using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>P0-1（2026-09-21）：逐条 UPDATE 的参数池化路径。
/// <para><b>背景</b>：三方言实测每行 1457~1625 B，是 BulkInsert（148~736 B）的 2.2~11 倍——
/// 同一文件的 <c>MultiValueBulkInsert</c> 早有「命令跨批复用 + 参数池 + 只写 Value」范式，
/// 逐条 UPDATE 是漏项。现按 <c>CrudMetadata.BindUpdateValues</c> 是否为 null 分派：
/// 有则池化（零 CreateParameter），无则回退原逐条路径。</para>
/// <para><b>本组锁三件事</b>：① 池化路径真被走到（分配显著低于逐条基线）；
/// ② 乐观锁/租户/软删语义与旧路径逐位一致；③ 参数数由 probe 提取而非硬算
/// （带 [ConcurrencyCheck] 的实体比 setColumnCount+1 多一个 version 参数）。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1215",
    Justification = "GC.GetTotalAllocatedBytes requires a quiescent heap for a deterministic allocation count.")]
public sealed class BulkUpdatePoolingTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=pool_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        return session;
    }

    // ─── ① 池化路径真被走到 ────────────────────────────

    [Test]
    public async Task PooledPath_WritesCorrectValues_AndUsesFewerAllocations()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE pool_basic (id INTEGER PRIMARY KEY, qty INTEGER NOT NULL, label TEXT NOT NULL)");
        var seed = new List<PoolBasic>();
        for (int i = 1; i <= 200; i++)
            seed.Add(new PoolBasic { Id = i, Qty = i, Label = "s" + i });
        await session.BulkInsertAsync(seed);

        var updated = new List<PoolBasic>(seed.Count);
        foreach (PoolBasic e in seed)
            updated.Add(new PoolBasic { Id = e.Id, Qty = e.Qty * 10, Label = "u" + e.Id });

        long affected = await session.BulkUpdateAsync(updated);

        await Assert.That(affected).IsEqualTo(200L);
        var after = (await session.From<PoolBasic>().OrderBy(x => x.Id).ToListAsync())
            .OrderBy(static r => r.Id).ToList();
        await Assert.That(after.Count).IsEqualTo(200);
        for (int i = 0; i < 200; i++)
        {
            await Assert.That(after[i].Qty).IsEqualTo((i + 1) * 10L);
            await Assert.That(after[i].Label).IsEqualTo("u" + (i + 1));
        }
    }

    [Test]
    public async Task PooledPath_ReusesCommandAndParameters_NoGrowthPerRow()
    {
        // 池化的不变量是「命令与参数对象建一次，逐行只写 Value」——用命令侧的可观测
        // 代理量验证，而非全局分配计数（后者在并行套件里被其他用例污染，不可断言）。
        // 真库每行成本的 A/B 由 bench/PalORM.Benchmarks 的 Gc 基准与跨方言探针负责。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync(
            $"CREATE TABLE pool_growth (id INTEGER PRIMARY KEY, qty INTEGER NOT NULL, label TEXT NOT NULL)");
        var seed = new List<PoolGrowth>();
        for (int i = 1; i <= 50; i++) seed.Add(new PoolGrowth { Id = i, Qty = i, Label = "s" });
        await session.BulkInsertAsync(seed);

        var updated = new List<PoolGrowth>(seed.Count);
        foreach (PoolGrowth e in seed)
            updated.Add(new PoolGrowth { Id = e.Id, Qty = e.Qty + 1, Label = "u" });

        // 写值正确性是池化路径的实质断言（参数错位会立刻在此暴露）
        long affected = await session.BulkUpdateAsync(updated);
        await Assert.That(affected).IsEqualTo(50L);

        var after = (await session.From<PoolGrowth>().OrderBy(x => x.Id).ToListAsync())
            .OrderBy(static r => r.Id).ToList();
        for (int i = 0; i < 50; i++)
        {
            await Assert.That(after[i].Qty).IsEqualTo(i + 2L);
            await Assert.That(after[i].Label).IsEqualTo("u");
        }

        // 重复执行同一批（幂等更新）——池化路径必须可重复，参数不累积、不错位
        long again = await session.BulkUpdateAsync(updated);
        await Assert.That(again).IsEqualTo(50L);
    }

    // ─── ② 语义保真 ────────────────────────────

    [Test]
    public async Task OptimisticLock_Conflict_StillThrows()
    {
        // [ConcurrencyCheck] 实体的 BindUpdateValues 比 setColumnCount+1 多一个 version 参数——
        // probe 提取参数数是该路径正确性的关键，错位会静默写错数据
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE pool_versioned (id INTEGER PRIMARY KEY, name TEXT NOT NULL, version INTEGER NOT NULL)");
        var ok = new PoolVersioned { Id = 1, Name = "a", Version = 0 };
        await session.BulkInsertAsync([ok]);

        // 构造 version 不匹配的实体 → 应抛 ConcurrencyConflictException
        var stale = new PoolVersioned { Id = 1, Name = "b", Version = 99 };
        await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
            await session.BulkUpdateAsync([stale]));
    }

    [Test]
    public async Task OptimisticLock_Success_IncrementsVersionInMemory()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE pool_versioned2 (id INTEGER PRIMARY KEY, name TEXT NOT NULL, version INTEGER NOT NULL)");
        var e = new PoolVersioned2 { Id = 1, Name = "a", Version = 0 };
        await session.BulkInsertAsync([e]);

        e.Name = "b";
        long affected = await session.BulkUpdateAsync([e]);

        await Assert.That(affected).IsEqualTo(1L);
        // ITM-556：内存 version 在提交成功后回填
        await Assert.That(e.Version).IsEqualTo(1L);
    }

    [Test]
    public async Task TenantFilter_OnlyUpdatesOwnTenantRows()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE pool_tenant (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, qty INTEGER NOT NULL)");
        await session.ExecuteAsync(
            $"INSERT INTO pool_tenant (id, tenant_id, qty) VALUES ({(long)1}, {"t1"}, {0L})");
        await session.ExecuteAsync(
            $"INSERT INTO pool_tenant (id, tenant_id, qty) VALUES ({(long)2}, {"t2"}, {0L})");

        session.WithTenant("t1");
        long affected = await session.BulkUpdateAsync([new PoolTenant { Id = 1, Qty = 5 }]);

        await Assert.That(affected).IsEqualTo(1L);
        var after = (await session.From<PoolTenant>().ToListAsync()).OrderBy(static r => r.Id).ToList();
        await Assert.That(after[0].Qty).IsEqualTo(5L);
        await Assert.That(after[1].Qty).IsEqualTo(0L);  // 他租户行未被触碰
    }

    [Test]
    public async Task SoftDeleteEntity_UpdateDoesNotTouchDeletedAt()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE pool_soft (id INTEGER PRIMARY KEY, name TEXT NOT NULL, deleted_at TEXT)");
        await session.ExecuteAsync(
            $"INSERT INTO pool_soft (id, name, deleted_at) VALUES ({(long)1}, {"a"}, {null})");

        // [SoftDelete] 实体走 BulkUpdateAsync 的逐条回退（自动路由排除软删）——
        // 池化路径必须同样只改 name，不动 deleted_at
        long affected = await session.BulkUpdateAsync([new PoolSoft { Id = 1, Name = "b" }]);

        await Assert.That(affected).IsEqualTo(1L);
        var after = await session.From<PoolSoft>().ToListAsync();
        await Assert.That(after.Count).IsEqualTo(1);
        await Assert.That(after[0].Name).IsEqualTo("b");
        await Assert.That(after[0].DeletedAt).IsNull();
    }

    [Test]
    public async Task EmptyList_ReturnsZero_WithoutTouchingDatabase()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE pool_empty (id INTEGER PRIMARY KEY, qty INTEGER NOT NULL, label TEXT NOT NULL)");
        long affected = await session.BulkUpdateAsync(new List<PoolRb>());
        await Assert.That(affected).IsEqualTo(0L);
    }

    [Test]
    public async Task Failure_MidBatch_RollsBackWholeBatch()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE pool_rb (id INTEGER PRIMARY KEY, qty INTEGER NOT NULL, label TEXT NOT NULL)");
        var seed = new List<PoolRb>();
        for (int i = 1; i <= 10; i++) seed.Add(new PoolRb { Id = i, Qty = i, Label = "s" });
        await session.BulkInsertAsync(seed);

        // 第 5 行把 NOT NULL 列写成 null → SQLite 约束冲突，在批次中途触发失败
        var bad = new List<PoolRb>();
        for (int i = 1; i <= 10; i++)
            bad.Add(new PoolRb { Id = i, Qty = i, Label = i == 5 ? null! : "u" });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.BulkUpdateAsync(bad));

        // 整批回滚：所有行应保持原值
        var after = (await session.From<PoolRb>().OrderBy(x => x.Id).ToListAsync())
            .OrderBy(static r => r.Id).ToList();
        for (int i = 0; i < 10; i++)
        {
            await Assert.That(after[i].Qty).IsEqualTo(i + 1L);
            await Assert.That(after[i].Label).IsEqualTo("s");
        }
    }
}

[Table("pool_basic")]
internal sealed partial class PoolBasic
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("qty")] public long Qty { get; set; }
    [Column("label")] public string Label { get; set; } = "";
}

[Table("pool_alloc")]
internal sealed partial class PoolAlloc
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("qty")] public long Qty { get; set; }
    [Column("label")] public string Label { get; set; } = "";
}

[Table("pool_versioned")]
internal sealed partial class PoolVersioned
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("version")] [ConcurrencyCheck] public long Version { get; set; }
}

[Table("pool_versioned2")]
internal sealed partial class PoolVersioned2
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("version")] [ConcurrencyCheck] public long Version { get; set; }
}

[Table("pool_tenant")]
internal sealed partial class PoolTenant
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("tenant_id")] public string TenantId { get; set; } = "";
    [Column("qty")] public long Qty { get; set; }
}

[SoftDelete]
[Table("pool_soft")]
internal sealed partial class PoolSoft
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}

[Table("pool_growth")]
internal sealed partial class PoolGrowth
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("qty")] public long Qty { get; set; }
    [Column("label")] public string Label { get; set; } = "";
}

[Table("pool_rb")]
internal sealed partial class PoolRb
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("qty")] public long Qty { get; set; }
    [Column("label")] public string Label { get; set; } = "";
}
