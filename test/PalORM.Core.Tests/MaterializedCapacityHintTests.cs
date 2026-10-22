using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>2026-10-22 终审 P2-6：物化容量启发只作为 List 初始容量，必须有上界。
/// 一次 10 万行全表曾把行数记给后续小查询（池化会话跨租期保留），每个小查询白分配
/// 约 800 KB。</summary>
public sealed class MaterializedCapacityHintTests
{
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
}

[Table("cap_probe")]
public sealed class CapProbe
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}
