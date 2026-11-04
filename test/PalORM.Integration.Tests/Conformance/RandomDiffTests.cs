using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>D 层随机化差分（方案 §7）。
/// <para>固定种子集做回归（进 PR 门禁），新种子集做探索（由 <c>PALORM_FUZZ_SEED</c> 环境变量启用，
/// 夜间或手动触发）。失败场景先收缩再落盘到 <c>artifacts/fuzz-failures-*.txt</c>，收缩结果附
/// 完整重放三元组，供固化成 A 层固定用例。</para>
/// <para>预算：固定种子回归取 6 种子 × 8 场景，实测约 2 秒；探索模式单种子 200 场景
/// （2026-11-04 FIX-7 由 12 提到 200），跑完打印 <c>FUZZ-EXPLORE-RAN</c> 标记。</para>
/// <para>标记为何必须打印：夜间 lane 以标记数自证「探索真的跑了」。环境变量缺失时本用例直接
/// 返回且不打标记——静默跳过会被夜间判红，而不是被当成通过。</para></summary>
[NotInParallel("ConformanceFuzz")]
public sealed class RandomDiffTests
{
    private static readonly int[] FixedSeeds = [1, 7, 42, 100, 2026, 20261029];
    // FUZ-4：固定集 6 种子 × 84 案例 = 504（原 48）。PR 路径要求 30 秒内完成，
    // 案例数按实测耗时调整（2026-11-04 实测：见任务清单执行记录）。
    private const int CasesPerSeed = 84;

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
                if (await RandomDiffRunner.HasDifferenceAsync(scenario, session, raw, SqlDialect.Sqlite))
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
    public async Task ExploreSeeds_ShrinksAndPersistsFailures()
    {
        string? seedBaseText = Environment.GetEnvironmentVariable("PALORM_FUZZ_SEED_BASE");

        // FUZ-3：夜间按「种子基数 + 轮数」循环，单轮 50 案例（20 轮 = 1,000 场景）。
        // 循环而非把单种子案例数调到 1,000：单场景超时会被整体放大，失败样本也难定位到具体种子。
        if (!string.IsNullOrWhiteSpace(seedBaseText) && int.TryParse(seedBaseText, out int seedBase))
        {
            int rounds = ReadEnvInt("PALORM_FUZZ_ROUNDS") ?? 20;
            const int casesPerSeed = 50;
            await RunExplorationAsync(seedBase, rounds, casesPerSeed);
            return;
        }

        // 兼容 FIX-7 的单种子形态（手动触发或旧配置）
        string? seedText = Environment.GetEnvironmentVariable("PALORM_FUZZ_SEED");
        if (string.IsNullOrWhiteSpace(seedText) || !int.TryParse(seedText, out int seed))
        {
            // 未启用探索：CI 的固定种子回归已覆盖"网格内"，网格外由夜间任务传环境变量触发
            return;
        }
        await RunExplorationAsync(seed, 1, casesPerSeed: 200);
    }

    private static int? ReadEnvInt(string name)
    {
        string? text = Environment.GetEnvironmentVariable(name);
        return int.TryParse(text, out int value) ? value : null;
    }

    /// <summary>跑一段探索：逐种子生成场景、差分、失败收缩并落盘；语料按种子归档。</summary>
    private static async Task RunExplorationAsync(int seedBase, int rounds, int casesPerSeed)
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("fuzz_explore");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        List<string> persisted = [];
        int executed = 0;
        string corpusDir = Path.Combine(ArtifactDirectory(), "fuzz-corpus");
        Directory.CreateDirectory(corpusDir);

        for (int round = 0; round < rounds; round++)
        {
            int seed = seedBase + round;
            List<string> corpus = [];
            foreach (DScenario scenario in DGenerator.Generate(seed, casesPerSeed))
            {
                executed++;
                corpus.Add(scenario.ReplayKey);
                bool failed = await RandomDiffRunner.HasDifferenceAsync(scenario, session, raw, SqlDialect.Sqlite);
                if (!failed) continue;

                // 收缩：失败谓词就是"同一场景重跑仍不一致"
                DScenario shrunk = await ScenarioShrinker.ShrinkAsync(
                    scenario,
                    candidate => RandomDiffRunner.HasDifferenceAsync(candidate, session, raw, SqlDialect.Sqlite));
                persisted.Add($"seed={seed} original={scenario.ReplayKey} shrunk={shrunk.ReplayKey}");
            }

            // 语料归档：随轮次增长，供 FUZ-6 的失败固化脚本消费
            await File.WriteAllLinesAsync(Path.Combine(corpusDir, $"seed-{seed}.txt"), corpus);
        }

        // 执行标记（夜间 lane 的存活判据）：MTP 在 Linux 上不把测试进程的 Console.WriteLine
        // 转发到 stdout，标记实际落在 tunit-report.json 的 output 字段，故两处都算。
        Console.WriteLine($"FUZZ-EXPLORE-RAN seedBase={seedBase} rounds={rounds} cases={executed} differences={persisted.Count}");

        if (persisted.Count > 0)
        {
            // 失败样本落盘先于断言：断言失败时文件必须已经存在，否则产物随失败一起消失。
            string path = Path.Combine(ArtifactDirectory(),
                $"fuzz-failures-{seedBase}-{DateTime.UtcNow:yyyyMMddTHHmmss}.txt");
            await File.WriteAllLinesAsync(path, persisted);
            Console.WriteLine($"FUZZ-EXPLORE-PERSISTED {path}");
        }

        await Assert.That(string.Join(Environment.NewLine, persisted.Take(10)))
            .IsEmpty()
            .Because($"探索种子基数 {seedBase} 共 {rounds} 轮 {executed} 个场景，发现 {persisted.Count} 例差异；"
                + "按方案 §7.4 必须固化成 A 层固定用例后才能关闭");
    }

    /// <summary>产物目录解析到仓库根（测试可能从任意 CWD 启动）；artifacts/ 已被 .gitignore 忽略。</summary>
    private static string ArtifactDirectory()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            dir = dir.Parent;
        }
        string artifacts = Path.Combine(dir?.FullName ?? Environment.CurrentDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}