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
    private const int MaxEntries = 1024;

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

    [Test]
    public async Task DynamicSkipValues_KeepCacheBounded()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        // 增量断言：静态缓存被并行测试组共享，绝对值不可控（账本 SHAPE-001 陷阱栏）
        int before = SqlShapeCache.TotalEntryCount;
        // 动态 OFFSET 分页：页码递增 → Skip 值无界。每个值生成不同 LIMIT/OFFSET 文本
        // PALORM005 豁免：ToSql() 是纯构建不打数据库——本测试钉的是缓存容量而非查询形态
#pragma warning disable PALORM005
            for (int i = 0; i < 10_000; i++)
                _ = session.From<ShapeProbeEntity>().OrderBy(x => x.Id).Skip(i).Take(10).ToSql();
#pragma warning restore PALORM005

        await Assert.That(SqlShapeCache.TotalEntryCount - before).IsLessThanOrEqualTo(MaxEntries);
    }

    [Test]
    public async Task ClonedBuilder_ReusesCacheEntryOfOriginalShape()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        int before = SqlShapeCache.TotalEntryCount;
        QueryBuilder<ShapeProbeEntity> builder = session.From<ShapeProbeEntity>()
            .Where($"name = {"probe-a"}")
            .OrderBy(x => x.Id)
            .Take(5);

        string original = builder.ToSql();
        string cachedAgain = builder.ToSql();
        QueryBuilder<ShapeProbeEntity> clone = builder.CloneForExecution();
        string fromClone = clone.ToSql();

        // 克隆体与原生路径形状相同：SQL 相同，且本形状只允许新增一个缓存条目
        await Assert.That(fromClone).IsEqualTo(original);
        await Assert.That(cachedAgain).IsEqualTo(original);
        await Assert.That(SqlShapeCache.TotalEntryCount - before).IsEqualTo(1);
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
