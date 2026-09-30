// bench-matrix 子命令（自 run-benchmarks.sh 迁移，T4-2）：微基准完整矩阵运行器。
// 与 perf 的分工——perf → 门禁集（与 CI 同参）+ 三套夹具 + 唯一报告，日常用；
// bench-matrix → 完整矩阵（11 个类里的其余 58 项）+ 基线录制，专项调查与换驱动时用。
namespace PalORM.PerfCli;

internal static class BenchMatrix
{
    public static int Run(string[] args)
    {
        var root = Perf.RepoRoot();
        var benchDir = Path.Combine(root, "bench", "PalORM.Benchmarks");
        // BDN 把报告写在仓库根（dotnet run --project 的 CWD 是仓库根），不在基准项目目录下
        var artifactsDir = Path.Combine(root, "BenchmarkDotNet.Artifacts");
        var baselineDir = Path.Combine(root, "bench", "baselines");
        var target = args.Length > 0 ? args[0] : "sqlite";
        var extra = args.Length > 1 ? args[1] : "";

        Perf.Out("═══════════════════════════════════════════════════════════════");
        Perf.Out(" PalORM 性能基准运行器");
        Perf.Out($" 目标: {target}");
        Perf.Out($" 时间: {DateTime.Now}");
        Perf.Out("═══════════════════════════════════════════════════════════════");

        Perf.Out(">>> 构建 Release...");
        _ = Perf.RunTail("dotnet", $"build \"{Path.Combine(benchDir, "PalORM.Benchmarks.csproj")}\" -c Release --nologo", NulLog(), 3);

        var code = target switch
        {
            // BDN filter 不带 bash 单引号——ProcessStartInfo 参数串不剥引号（2026-09-30 实测修复，
            // 同 FullPerf BDN 修复）；RunSuite 的退出码链路完好，无需额外处理
            "sqlite" => RunSuite(benchDir,
                "--filter *CrudBenchmarks* *BulkBenchmarksFixed* *BulkBenchmarks* " +
                "*GcBenchmarks* *SqlBuildBenchmarks* " +
                "*FeatureBenchmarks* *OrmComparisonBenchmarks* *BinaryBenchmarks* --exporters json",
                "sqlite", ">>> 运行 SQLite 基准（CRUD + Bulk + Transaction + Advanced）..."),
            "scale" => RunSuite(benchDir, "--filter *BulkBenchmarks*", "scale", ">>> 运行 BulkInsert 拐点扫描（100/1K/10K/100K）..."),
            "build" => RunSuite(benchDir, "--filter *SqlBuildBenchmarks* --exporters json", "build", ">>> 运行 SQL 构建微基准（严格配置 5/10/15）..."),
            "pg" => RunRemote(benchDir, "pg", "PALORM_BENCH_PG", "*PgBenchmarks*", "PostgreSQL"),
            "mysql" => RunRemote(benchDir, "mysql", "PALORM_BENCH_MYSQL", "*MySqlBenchmarks*", "MySQL"),
            "all" => RunSuite(benchDir, "--filter *", "all", ">>> 运行全部 SQLite + Scale + Build + Speed 基准（约 30 分钟）..."),
            "workload" => RunSuite(benchDir, "--workload", "workload", ">>> 并发负载测试（维度 3/4/11，规范 docs/性能基准规范.md；SQLite 档）..."),
            "speed" => PrintSpeedGuidance(),
            _ => PrintUsage(),
        };
        if (code != 0)
        {
            return code;
        }

        // 后处理：基线保存 / 对比
        if (extra == "--save-baseline")
        {
            var saveCode = SaveBaseline(root, artifactsDir, baselineDir, target);
            if (saveCode != 0)
            {
                return saveCode;
            }
        }
        if (extra.StartsWith("--compare", StringComparison.Ordinal))
        {
            PrintCompare(baselineDir, extra);
        }

        Perf.Out("");
        Perf.Out("═══════════════════════════════════════════════════════════════");
        Perf.Out(" 基准运行完成");
        Perf.Out($" 报告: {Path.Combine(artifactsDir, "results")}/");
        Perf.Out("═══════════════════════════════════════════════════════════════");
        return 0;
    }

    private static int RunSuite(string benchDir, string benchArgs, string logName, string banner)
    {
        Perf.Out(banner + "...");
        var log = Path.Combine(Path.GetTempPath(), $"bench-{logName}-{Perf.Stamp()}.log");
        var code = Perf.RunTee("dotnet", $"run --project \"{benchDir}\" -c Release --no-build -- {benchArgs}", log);
        if (code != 0)
        {
            return code;
        }
        Perf.Out($"日志: {log}");
        return 0;
    }

    private static int RunRemote(string benchDir, string target, string envVar, string filter, string label)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
        {
            Perf.Out("ERROR: 设置 " + envVar + " 环境变量");
            return 1;
        }
        return RunSuite(benchDir, $"--filter {filter}", target, $">>> 运行 {label} 基准...");
    }

    // 原 05_SqliteSpeedBenchmarks 独立类已删（2026-09-23 审计）：真子集且无 diagnoser 被
    // ResultReader 结构性排除在门禁外。其问句用"临时摘掉 01 的属性跑同一过滤器"回答即可。
    private static int PrintSpeedGuidance()
    {
        Perf.Out("纯速度交叉验证的做法（不再有专用类）：");
        Perf.Out("  1) 临时移除 bench/PalORM.Benchmarks/01_CrudBenchmarks.cs 的 [MemoryDiagnoser]");
        Perf.Out("  2) dotnet run --project tools/PalORM.PerfCli -- sqlite");
        Perf.Out("  3) 与此前带 diagnoser 的同项中位数对比；差 >5% 才说明 diagnoser 干扰了测量");
        return 0;
    }

    private static int PrintUsage()
    {
        Perf.Out("用法: dotnet run --project tools/PalORM.PerfCli -- bench-matrix [sqlite|pg|mysql|scale|build|all|workload]");
        Perf.Out("  追加 --save-baseline 保存基线 JSON");
        Perf.Out("  追加 --compare <version> 与已有基线对比");
        return 1;
    }

    private static string NulLog() => Path.Combine(Path.GetTempPath(), "perfcli-build-tail.log");

    private static int SaveBaseline(string root, string artifactsDir, string baselineDir, string target)
    {
        var jsonDir = Path.Combine(artifactsDir, "results");
        var jsons = Directory.Exists(jsonDir) ? Directory.GetFiles(jsonDir, "*-report*.json") : [];
        if (jsons.Length == 0)
        {
            Perf.Out("⚠ 未找到 BDN JSON 报告，跳过基线保存");
            return 0;
        }
        var baselineFile = Path.Combine(baselineDir, $"snapshot-{Perf.Stamp()}.json");
        Directory.CreateDirectory(baselineDir);
        // 与 perf-gate 同一数据源（BDN JSON），避免 CSV 解析漂移
        var version = Perf.Git("describe --tags --abbrev=0").Trim();
        if (version.Length == 0)
        {
            version = "dev";
        }
        var code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, "tools", "PalORM.PerfGate")}\" -c Release -- " +
            $"record --results \"{jsonDir}\" --out \"{baselineFile}\" " +
            $"--version \"{version}\" --date \"{DateTime.Now:yyyy-MM-dd}\" " +
            $"--scope \"bench-matrix {target}\"");
        if (code != 0)
        {
            Perf.Out("❌ 基线生成失败");
            return 1;
        }
        Perf.Out($"✅ 基线已保存: {baselineFile}");
        return 0;
    }

    private static void PrintCompare(string baselineDir, string extra)
    {
        var version = extra["--compare".Length..].Trim().Replace(" ", "", StringComparison.Ordinal);
        var baselineFile = Path.Combine(baselineDir, $"{version}.json");
        Perf.Out("");
        Perf.Out("═══════════════════════════════════════════════════════════════");
        Perf.Out($" 基线对比: 当前运行 vs {version}");
        Perf.Out("═══════════════════════════════════════════════════════════════");
        if (File.Exists(baselineFile))
        {
            Perf.Out("（对比功能需人工或后续脚本自动化——当前输出基线 JSON 供 diff 工具使用）");
            Perf.Out($"基线文件: {baselineFile}");
        }
        else
        {
            Perf.Out($"⚠ 基线 {version} 不存在，可用版本:");
            if (Directory.Exists(baselineDir))
            {
                foreach (var f in Directory.GetFiles(baselineDir, "*.json"))
                {
                    Perf.Out($"  {Path.GetFileNameWithoutExtension(f)}");
                }
            }
            else
            {
                Perf.Out("  (无)");
            }
        }
    }
}
