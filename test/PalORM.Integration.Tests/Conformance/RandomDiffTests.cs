using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>D 层随机化差分（方案 §7）。
/// <para>固定种子集做回归（进 PR 门禁），新种子集做探索（由 <c>PALORM_FUZZ_SEED</c> 环境变量启用，
/// 夜间或手动触发）。失败场景先收缩再落盘到 <c>artifacts/fuzz-failures-*.txt</c>，收缩结果附
/// 完整重放三元组，供固化成 A 层固定用例。</para>
/// <para>预算：每方言 60 到 120 秒的探索量级对应约 8 到 16 个场景；本类的固定种子回归取 6 种子 × 8 场景，
/// 实测约 2 秒。</para></summary>
[NotInParallel("ConformanceFuzz")]
public sealed class RandomDiffTests
{
    private static readonly int[] FixedSeeds = [1, 7, 42, 100, 2026, 20261029];
    private const int CasesPerSeed = 8;

    private static async ValueTask<(SharedSqliteDb Shared, DataSession<SqliteProvider> Db, DbConnection Raw)>
        OpenAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
        await MirrorTable.EnsureAsync(db, SqlDialect.Sqlite, ct);
        return (shared, db, db.GetRawConnection());
    }

    [Test]
    public async Task FixedSeeds_ProduceNoDifferences()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("fuzz_fixed");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        List<string> differences = [];
        int executed = 0;
        foreach (int seed in FixedSeeds)
        {
            foreach (DScenario scenario in DGenerator.Generate(seed, CasesPerSeed))
            {
                executed++;
                if (await RandomDiffRunner.HasDifferenceAsync(scenario, session, raw))
                    differences.Add(scenario.ReplayKey);
            }
        }

        // 有效负载下限：防止"零场景执行"被当成零差异
        await Assert.That(executed).IsEqualTo(FixedSeeds.Length * CasesPerSeed);
        string report = differences.Count == 0
            ? ""
            : $"零差异断言失败：{differences.Count} 例，种子集 [{string.Join(",", FixedSeeds)}]："
                + string.Join(" | ", differences.Take(10));
        await Assert.That(report).IsEmpty();
    }

    [Test]
    public async Task ExploreSeed_ShrinksAndPersistsFailures()
    {
        string? seedText = Environment.GetEnvironmentVariable("PALORM_FUZZ_SEED");
        if (string.IsNullOrWhiteSpace(seedText) || !int.TryParse(seedText, out int seed))
        {
            // 未启用探索：CI 的固定种子回归已覆盖"网格内"，网格外由夜间任务传 PALORM_FUZZ_SEED 触发
            return;
        }

        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("fuzz_explore");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        List<string> persisted = [];
        const int cases = 12;
        foreach (DScenario scenario in DGenerator.Generate(seed, cases))
        {
            bool failed = await RandomDiffRunner.HasDifferenceAsync(scenario, session, raw);
            if (!failed) continue;

            // 收缩：失败谓词就是"同一场景重跑仍不一致"
            DScenario shrunk = await ScenarioShrinker.ShrinkAsync(
                scenario,
                candidate => RandomDiffRunner.HasDifferenceAsync(candidate, session, raw));
            persisted.Add($"seed={seed} original={scenario.ReplayKey} shrunk={shrunk.ReplayKey}");
        }

        await Assert.That(string.Join(Environment.NewLine, persisted.Take(10)))
            .IsEmpty()
            .Because($"探索种子 {seed} 共 {cases} 个场景，发现 {persisted.Count} 例差异；"
                + "按方案 §7.4 必须固化成 A 层固定用例后才能关闭");
    }
}