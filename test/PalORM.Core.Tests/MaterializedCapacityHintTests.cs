using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>2026-10-22 终审 P2-6：物化容量启发只作为 List 初始容量，必须有上界。
/// 一次 10 万行全表曾把行数记给后续小查询（池化会话跨租期保留），每个小查询白分配
/// 约 800 KB。</summary>
public sealed class MaterializedCapacityHintTests
{
    /// <summary>2026-11-01 惰性晋升：同类型第二次物化必须命中内联槽的容量提示。
    /// 3000 行是判据分水岭——有提示时初始容量 3000，无提示时 16 起倍增到 4096。</summary>
    [Test]
    public async Task SecondMaterializationOfSameType_ReusesRecordedCapacity()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:"
        });
        await db.MigrateAsync();
        await db.ExecuteAsync($"""
            WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 3000)
            INSERT INTO cap_probe (Id, Name) SELECT x, 'n' FROM seq
            """);

        var all = await db.GetAllAsync<CapProbe>();
        await Assert.That(all.Count).IsEqualTo(3000);

        var again = await db.From<CapProbe>().ToListAsync();
        await Assert.That(again.Count).IsEqualTo(3000);
        await Assert.That(again.Capacity).IsEqualTo(3000);
    }

    /// <summary>2026-11-01 惰性晋升：出现第二种类型时升级为字典，两类的提示都要保留。</summary>
    [Test]
    public async Task TwoEntityTypes_UpgradeToDictionaryAndKeepBothHints()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:"
        });
        await db.MigrateAsync();
        await db.ExecuteAsync($"""
            WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 3000)
            INSERT INTO cap_probe (Id, Name) SELECT x, 'n' FROM seq
            """);
        await db.ExecuteAsync($"""
            WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 2000)
            INSERT INTO cap_probe2 (Id, Name) SELECT x, 'n' FROM seq
            """);

        await Assert.That((await db.GetAllAsync<CapProbe>()).Count).IsEqualTo(3000);
        await Assert.That((await db.GetAllAsync<CapProbe2>()).Count).IsEqualTo(2000);

        var first = await db.From<CapProbe>().ToListAsync();
        var second = await db.From<CapProbe2>().ToListAsync();
        await Assert.That(first.Capacity).IsEqualTo(3000);
        await Assert.That(second.Capacity).IsEqualTo(2000);
    }

    [Test]
    public async Task LargeMaterialization_DoesNotInflateNextQueryCapacity()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:"
        });
        await db.MigrateAsync();
        await db.ExecuteAsync($"""
            WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 100000)
            INSERT INTO cap_probe (Id, Name) SELECT x, 'n' FROM seq
            """);

        var all = await db.GetAllAsync<CapProbe>();
        await Assert.That(all.Count).IsEqualTo(100000);

        var small = await db.From<CapProbe>().Take(5).ToListAsync();
        await Assert.That(small.Count).IsEqualTo(5);
        await Assert.That(small.Capacity)
            .IsLessThanOrEqualTo(SqlLimits.MaxMaterializedCapacityHint);
    }

    /// <summary>P1-1（2026-11-26）：一次大结果集查询不得把同实体后续小结果集查询的容量
    /// 永久抬高。原实现只上调提示、且小结果集不记录，2000 行的 GetAllAsync 会让后续单行
    /// 查询按 2000 分配（实测分配 1.2 KB → 16.7 KB；池化会话跨租期保留该记录，是稳态
    /// 而非偶发）。修法是把记录规则改为自适应收缩（超过本次规模的 4 倍即下调）。</summary>
    [Test]
    public async Task LargeQueryThenSingleRowQuery_ShrinksCapacityBackToDefault()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:"
        });
        await db.MigrateAsync();
        await db.ExecuteAsync($"""
            WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 2000)
            INSERT INTO cap_probe (Id, Name) SELECT x, 'n' FROM seq
            """);

        var all = await db.GetAllAsync<CapProbe>();
        await Assert.That(all.Count).IsEqualTo(2000);

        // 容量提示是读取前的预测，因此下调必然滞后一次：第一次小查询仍按旧提示 2000 分配。
        var firstSmall = await db.From<CapProbe>().Where($"Id = {7}").ToListAsync();
        await Assert.That(firstSmall.Count).IsEqualTo(1);
        await Assert.That(firstSmall.Capacity).IsEqualTo(2000);

        // 本次读取记录到 1 行（2000 > 16 × 4 触发收缩），下一次同实体查询回到默认容量。
        var secondSmall = await db.From<CapProbe>().Where($"Id = {8}").ToListAsync();
        await Assert.That(secondSmall.Capacity).IsEqualTo(16);

        // 第三次确认收缩稳定，不会因再次读到 1 行而回弹。
        var thirdSmall = await db.From<CapProbe>().Where($"Id = {9}").ToListAsync();
        await Assert.That(thirdSmall.Capacity).IsEqualTo(16);
    }
}

[Table("cap_probe")]
public sealed class CapProbe
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

[Table("cap_probe2")]
public sealed class CapProbe2
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}
