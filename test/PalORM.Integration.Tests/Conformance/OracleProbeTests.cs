using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>Oracle 侧探针常驻化（方案 §4.4 的 P1 与 P4）。
/// <para>P2（值映射错位）与 P3（比较器过宽）由 <c>ConformanceRowComparerTests</c> 的 17 条反例常驻承担；
/// 产品侧 P5/P6 由 <c>scripts/conformance-mutation-replay.cs</c> 承担。本类补 P1 与 P4。</para>
/// <para>两条都断言"注入后判据必须报红"。它们自身是套件自检：一旦判据被放宽到看不见这种形态，
/// 本类先红。</para></summary>
public sealed class OracleProbeTests
{
    private const string MirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_bulk_plain_mirror (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "name" TEXT NOT NULL,
            "amount" INTEGER NOT NULL
        )
        """;

    private static Dictionary<string, object?>[] Project(List<CfBulkPlainEntity> rows)
        => [.. rows.Select(entity => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = entity.Id,
            ["name"] = entity.Name,
            ["amount"] = entity.Amount
        })];

    [Test]
    public async Task P1_EmptySetInjection_IsCaught()
    {
        // 模拟手写 SQL 侧被写成 AND 1=0（结果集被压成空集）：
        // 比较器必须在行数上立刻报差异，而不是静默通过
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("oracle_probe_p1");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.BulkInsertAsync(
        [
            new CfBulkPlainEntity { Id = 1, Name = "a", Amount = 1 },
            new CfBulkPlainEntity { Id = 2, Name = "b", Amount = 2 }
        ]);
        await shared.ExecuteRawAsync(MirrorDdl);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> injected = await shared.QueryRawAsync(
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE 1 = 0""");

        IReadOnlyList<RowComparer.Difference> differences = RowComparer.Compare(injected, Project(palormRows));

        await Assert.That(palormRows.Count).IsEqualTo(2);
        await Assert.That(injected.Count).IsEqualTo(0);
        await Assert.That(differences.Count).IsGreaterThan(0);
        await Assert.That(differences.Any(d => d.Kind == "RowCount")).IsTrue();
    }

    [Test]
    public async Task P4_FilterBypassInjection_IsCaught()
    {
        // 模拟默认过滤失效（已删行进入结果集）：
        // L4 的"有效集相等"与"与已删集不交"两条判据都必须对这种形态报红
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("oracle_probe_p4");
        await using DataSession<SqliteProvider> seed = await shared.OpenSessionAsync();

        await seed.InsertAsync(new CfSoftDeleteEntity { Name = "alive" });
        await seed.InsertAsync(new CfSoftDeleteEntity { Name = "dead", DeletedAt = "2026-01-01" });

        (HashSet<long> expectedAlive, HashSet<long> expectedDead) = await shared.ReadSoftDeletePartitionAsync();
        await Assert.That(expectedAlive.Count).IsEqualTo(1);
        await Assert.That(expectedDead.Count).IsEqualTo(1);

        await using DataSession<SqliteProvider> injected = await shared.OpenSessionAsync();
        HashSet<long> mutatedAlive = [.. (await injected.IgnoreFilters()
            .From<CfSoftDeleteEntity>().ToListAsync()).Select(e => e.Id)];

        // 判据 1：有效集相等（变异后不等）
        await Assert.That(mutatedAlive.SetEquals(expectedAlive)).IsFalse();
        // 判据 2：不交（变异后与已删集相交）
        await Assert.That(mutatedAlive.Overlaps(expectedDead)).IsTrue();
    }
}