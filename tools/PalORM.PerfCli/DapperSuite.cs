// dappersuite 子命令（自 dappersuite-run.sh 迁移，T4-4）：Dapper 官方基准套件逐方言跑测。
// 单独跑用于调试本夹具，或换驱动版本时做官方形状哨兵复测；用户入口是 perf 的 full/smoke。
// 串行而非并行：BDN 计时对 CPU 争用敏感，三库并行会同时污染三份数字。
namespace PalORM.PerfCli;

internal static class DapperSuite
{
    public static int Run(string[] args)
    {
        var root = Perf.RepoRoot();
        var dialects = args.Length > 0 ? args[0] : "sqlite,mysql,pg";
        var extraArgs = args.Skip(1).ToArray();

        Perf.LoadEnvTest(root, verbose: true, hardFail: true);

        var outDir = Path.Combine(root, "bench", "PalORM.DapperSuite", "results");
        Directory.CreateDirectory(outDir);
        var stamp = Perf.Stamp();

        List<string> failed = [];
        foreach (var dialect in dialects.Split(','))
        {
            Perf.Out($"=== [{dialect}] 开始（{stamp}） ===");
            var log = Path.Combine(outDir, $"{dialect}-{stamp}.log");
            // B104 同族修复（2026-10-01）：默认过滤串原为 "-f '*' --join"，单引号在
            // UseShellExecute=false 的 ProcessStartInfo 下是字面量（无 shell 剥引号）——BDN 收到
            // 带引号的过滤串匹配 0 个基准、打印清单后退出码仍 0，本步骤自 2026-09-29 起静默空跑
            // （哨兵停在旧批）。filter 不得带引号（与 FullPerf 的 BDN 步骤 2026-09-30 同类修复对齐）。
            var runArgs = extraArgs.Length > 0
                ? string.Join(' ', extraArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
                : "--filter * --join";
            // 单方言失败不中断整批：一个库不可达（实测：托管库的虚拟机停机）不该让已跑方言的结果作废
            Environment.SetEnvironmentVariable("DAPPER_SUITE_DIALECT", dialect);
            var code = Perf.RunTee("dotnet",
                $"run --project \"{Path.Combine(root, "bench", "PalORM.DapperSuite")}\" -c Release -- {runArgs}", log);
            // BDN 在"全部基准 NA"（库不可达）时仍退 0 并打印 "Benchmarks with issues"；过滤串失配时
            // 打印 "returned 0 benchmarks" 后也退 0——只看退出码都会误报成功，故两条日志判据并列。
            string logText = File.ReadAllText(log);
            if (code == 0 && (logText.Contains("Benchmarks with issues", StringComparison.Ordinal)
                || logText.Contains("returned 0 benchmarks", StringComparison.Ordinal)))
            {
                Perf.Err($"=== [{dialect}] 基准未产出有效结果（库不可达/过滤串失配/设置错误，见 {log}）+ 继续下一方言 ===");
                failed.Add(dialect);
            }
            else if (code == 0)
            {
                Perf.Out($"=== [{dialect}] 完成，日志 {log} ===");
            }
            else
            {
                Perf.Err($"=== [{dialect}] 失败（日志 {log}）+ 继续下一方言 ===");
                failed.Add(dialect);
            }
        }

        if (failed.Count > 0)
        {
            Perf.Err($"失败方言：{string.Join(' ', failed)}（其余方言已跑完，结果库仍有登记）");
            return 1;
        }

        Perf.Out($"全部方言跑测完成。BDN 报告在 {Path.Combine(root, "BenchmarkDotNet.Artifacts", "results")}/");
        return 0;
    }
}
