using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// 审计 2026-09-19（dev@0e6a040）A1/A2 防线——SqlShapeCache 的容量纪律与哈希一致性。
/// <para><b>A1（SHAPE-001）</b>：Take/Skip 的值内联进 SQL 文本且进缓存键，而缓存只增不减——
/// 动态 OFFSET 分页（Skip 随页码无界）使进程级缓存无界增长。与 BoundedQueryCache 的
/// 1024 上限纪律（CacheStore.cs）对照，本缓存必须有同等上界。</para>
/// <para><b>A2（SHAPE-002）</b>：CloneForExecution 绕过 AddClause 直接重建子句链，
/// 克隆体 _shapeHash 丢失全部子句成分——同一形状经原生/克隆两条路径产生双份条目，
/// 同表不同 Where 的分页查询全部挤进同一残缺哈希桶。命中核对（Matches 全量比对）
/// 保证正确性，本测试钉住的是"同形状必须复用同一条目"的缓存契约。</para>
/// </summary>
[NotInParallel("SqlShapeCache")]
public sealed class SqlShapeCacheGrowthTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

    [Test]
    public async Task DynamicSkipValues_ShareSingleCacheEntry()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        // SHAPE-010 参数化根解的收益证明：10,000 个不同 Skip 值的 SQL 文本恒同（值经 @pN
        // 绑定），缓存仅 1 个该形态条目——动态 OFFSET 分页不再产生形状膨胀。
        // PALORM005 豁免：ToSql() 是纯构建不打数据库
#pragma warning disable PALORM005
        for (int i = 0; i < 10_000; i++)
            _ = session.From<ShapeProbeEntity>().OrderBy(x => x.Id).Skip(i).Take(10).ToSql();

        // 探针：同形态任一页的 SQL（占位符文本全库唯一——表名唯一），断言仅 1 条缓存条目
        string pageAny = session.From<ShapeProbeEntity>().OrderBy(x => x.Id).Skip(9_999).Take(10).ToSql();
#pragma warning restore PALORM005

        await Assert.That(pageAny.Contains("@p", StringComparison.Ordinal)).IsTrue();
        await Assert.That(SqlShapeCache.Entries.Count(
            e => e.FullSql == pageAny)).IsEqualTo(1);
    }

    [Test]
    public async Task LimitValues_BindAsParameters_InDryRunSnapshot()
    {
        // 参数化行为断言：值必须进参数快照（丢值/错位即刻暴露）。
        // SQLite 方言文本形态：LIMIT @pN OFFSET @pM（take 先 skip 后），无子句参数 → 恰 2 个
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        DryRunResult dry = session.From<ShapeProbeEntity>()
            .OrderBy(x => x.Id).Skip(20).Take(10).AsDryRun();

        await Assert.That(dry.Sql).Contains("LIMIT @p");
        await Assert.That(dry.Sql).Contains("OFFSET @p");
        await Assert.That(dry.Parameters.Count).IsEqualTo(2);
        await Assert.That((int)dry.Parameters[0].Value!).IsEqualTo(10);
        await Assert.That((int)dry.Parameters[1].Value!).IsEqualTo(20);
    }

    [Test]
    public async Task ClonedBuilder_ReusesCacheEntryOfOriginalShape()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        // 组内先跑的容量测试会把缓存填满拒写——先清空保证本形状确实被缓存
        SqlShapeCache.Clear();
        QueryBuilder<ShapeProbeEntity> builder = session.From<ShapeProbeEntity>()
            .Where($"name = {"probe-a"}")
            .OrderBy(x => x.Id)
            .Take(5);

        string original = builder.ToSql();
        string cachedAgain = builder.ToSql();
        QueryBuilder<ShapeProbeEntity> clone = builder.CloneForExecution();
        string fromClone = clone.ToSql();

        // 克隆体与原生路径形状相同：SQL 相同；行为断言——缓存中同 SQL 文本的条目仅一条
        //（对并行测试噪声免疫：其它测试不可能产出本形状的 SQL 文本）
        await Assert.That(fromClone).IsEqualTo(original);
        await Assert.That(cachedAgain).IsEqualTo(original);
        await Assert.That(SqlShapeCache.Entries.Count(
            e => e.FullSql == original)).IsEqualTo(1);
    }

    [Test]
    public async Task DynamicTagValues_NewShapeRejectedWhenCacheFull()
    {
        // A1 的残余无界源（OFFSET 排除之外）：Tag 的业务标识是裸文本子句，动态值
        // 每个都是新形状。行为断言：填满上限后新形状被拒（对齐 BoundedQueryCache 1024 纪律）
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
#pragma warning disable PALORM005 // ToSql() 纯构建不打数据库
        for (int i = 0; i < 10_000; i++)
            _ = session.From<ShapeProbeEntity>().Where($"id > {i}").Tag($"biz-{i}").ToSql();

        // 全新形状：缓存已满（1024 上限拒写）则它不进缓存
        string freshShape = session.From<ShapeProbeEntity>()
            .Where($"name = {"fresh-probe-x"}").ToSql();
#pragma warning restore PALORM005

        await Assert.That(SqlShapeCache.Entries.Count(
            e => e.FullSql == freshShape)).IsEqualTo(0);
    }
}

[Table("shape_probe")]
internal sealed partial class ShapeProbeEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = string.Empty;
}
