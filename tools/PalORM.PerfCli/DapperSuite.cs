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
            var runArgs = extraArgs.Length > 0
                ? string.Join(' ', extraArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
                : "-f '*' --join";
            // 单方言失败不中断整批：一个库不可达（实测：托管库的虚拟机停机）不该让已跑方言的结果作废
            Environment.SetEnvironmentVariable("DAPPER_SUITE_DIALECT", dialect);
            var code = Perf.RunTee("dotnet",
                $"run --project \"{Path.Combine(root, "bench", "PalORM.DapperSuite")}\" -c Release -- {runArgs}", log);
            // BDN 在"全部基准 NA"（库不可达）时仍退 0 并打印 "Benchmarks with issues"——
            // 只看退出码会把这种情况误报成成功，故追加日志检查
            if (code == 0 && File.ReadAllText(log).Contains("Benchmarks with issues", StringComparison.Ordinal))
            {
                Perf.Err($"=== [{dialect}] 基准未产出有效结果（库不可达或设置错误，见 {log}）+ 继续下一方言 ===");
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
