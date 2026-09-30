// full-perf 子命令（自 run-full-perf.sh 迁移，T4-3）：微基准侧编排步骤。
// 由 full 入口第 1 步以 SKIP_REPORT=1 调用；单独跑只在调试时用，报告只能含 BDN 明细。
using System.Text;

namespace PalORM.PerfCli;

internal static class FullPerf
{
    public static int Run(bool skipReport, bool withRemote)
    {
        var root = Perf.RepoRoot();
        var benchDir = Path.Combine(root, "bench", "PalORM.Benchmarks");
        const string gate = "tools/PalORM.PerfGate";
        var resultsDir = Path.Combine(root, "BenchmarkDotNet.Artifacts", "results");
        var reportDir = Path.Combine(root, "bench", "reports");
        var stamp = Perf.Stamp();
        var reportMd = Path.Combine(reportDir, $"perf-report-{stamp}.md");
        var logDir = Path.Combine(reportDir, $"logs-{stamp}");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(logDir);

        Perf.Step("[0/6] 构建（Release）");
        BuildTail(benchDir, "PalORM.Benchmarks.csproj");
        BuildTail(Path.Combine(root, gate), "PalORM.PerfGate.csproj");
        // 启动量具（[3/6]）以 --no-build 跑 SourceGen.Tests 的 Release 产物——不在此构建时，
        // 新机器/清理 bin 后该步恒 fail（exe 缺失，2026-09-26 基线重录实测）。
        BuildTail(Path.Combine(root, "test", "PalORM.SourceGen.Tests"), "PalORM.SourceGen.Tests.csproj");

        Perf.Step("[1/6] 负载测试 第 1 轮（维度 3/4/11）");
        _ = Perf.RunFiltered("dotnet", $"run --project \"{benchDir}\" -c Release --no-build -- --workload",
            Path.Combine(logDir, "workload-run1.log"), "threads|种子");
        Perf.Step("[1/6] 负载测试 第 2 轮（运行间方差对照）");
        _ = Perf.RunFiltered("dotnet", $"run --project \"{benchDir}\" -c Release --no-build -- --workload",
            Path.Combine(logDir, "workload-run2.log"), "threads");
        var workloadJson = Perf.NewestFile(Path.Combine(benchDir, "bin", "Release", "net11.0"), "workload-sqlite-*.json");

        Perf.Step("[2/6] 大结果集内存 + 构建分配（维度 1/7）");
        _ = Perf.RunFiltered("dotnet", $"run --project \"{benchDir}\" -c Release --no-build -- --memory",
            Path.Combine(logDir, "memory.log"), "memory");
        var memoryJson = Perf.NewestFile(Path.Combine(benchDir, "bin", "Release", "net11.0"), "memory-sqlite.json");

        Perf.Step("[3/6] 启动量具（维度 9：单方法 IL 上界，两维）");
        var (startupCode, startupOutput) = Perf.RunCapture("dotnet",
            $"run --project \"{Path.Combine(root, "test", "PalORM.SourceGen.Tests")}\" -c Release --no-build -- --treenode-filter \"/*/*/RegistryScaleTests/*\"");
        var startup = startupCode == 0 && startupOutput.Contains("成功: 2", StringComparison.Ordinal) ? "ok" : "fail";
        Perf.Out($"启动量具: {startup}");
        // 落盘供编排层（perf report）在生成统一报告时取用——统一报告在全部夹具之后才生成，
        // 不能依赖本脚本的进程内变量
        File.WriteAllText(Path.Combine(reportDir, "last-startup-status.txt"), startup, new UTF8Encoding(false));

        Perf.Step("[4/6] BDN 微基准（维度 1，与门禁同参 1/3/5，约 5 分钟）");
        // 先清结果目录——混入陈旧报告会让门禁读到截断/异构 JSON（实测先例）
        var bdnArtifacts = Path.Combine(root, "BenchmarkDotNet.Artifacts");
        if (Directory.Exists(bdnArtifacts))
        {
            Directory.Delete(bdnArtifacts, recursive: true);
        }
        // PALORM_BENCH_LABEL：显式声明这是"门禁同参集"（可复现的操作性子集，与临时单基准跑区分开）
        Environment.SetEnvironmentVariable("PALORM_BENCH_LABEL", "gate-set");
        // 2026-09-30 全面性能轮实测（修复）：BDN filter 不能带 bash 单引号——ProcessStartInfo
        // 参数串不剥引号（bash 才剥），BDN 收到字面引号报 typo 且 filter 全空；退出码必须接住，
        // 否则 BDN 失败被吞、推迟到门禁 FATAL"结果目录不存在"才暴露（B104 家族）。
        int bdnExit = Perf.RunFiltered("dotnet",
            $"run --project \"{benchDir}\" -c Release --no-build -- " +
            "--filter *CrudBenchmarks* *OrmComparisonBenchmarks* " +
            "--launchCount 1 --warmupCount 3 --iterationCount 5 --exporters json",
            Path.Combine(logDir, "bdn.log"), "Global total");

        Perf.Step("[5/6] 门禁判定");
        var gateLog = Path.Combine(logDir, "gate.log");
        var gateExit = Perf.RunTail("dotnet",
            $"run --project \"{Path.Combine(root, gate)}\" -c Release --no-build -- " +
            $"check --results \"{resultsDir}\" --baseline \"{Path.Combine(root, "bench", "baselines", "perf-baseline.json")}\"",
            gateLog, 3);

        Perf.Step("[6/6] 生成 markdown 报告");
        // SKIP_REPORT=1：由编排层（perf full）在全部夹具跑完后统一生成一份报告。
        if (skipReport)
        {
            Perf.Out("已跳过（SKIP_REPORT=1，报告由编排层在全部夹具完成后统一生成）");
        }
        else
        {
            RunReport(root, resultsDir, workloadJson, memoryJson, startup, reportMd);
        }

        if (withRemote && File.Exists(Path.Combine(root, ".env.test")))
        {
            Perf.Out("（WITH_REMOTE=1：远程批量档由 .ai/perf-probe/RemoteBulk.cs 承担，属 gitignored 本地探针，不在本命令内复刻）");
        }

        Perf.Out("");
        Perf.Out("═══════════════════════════════════════════");
        Perf.Out(" 全量测评完成");
        // 报告行只在真生成了报告时打印——SKIP_REPORT 时打印预留路径会让人去找不存在的文件
        Perf.Out(skipReport
            ? " 报告: 由编排层（perf full 的第 5 步）在全部夹具完成后生成"
            : $" 报告: {reportMd}");
        Perf.Out($" 日志: {logDir}");
        Perf.Out($" 门禁: {(gateExit == 0 ? "通过" : "存在回归（见 gate.log）")}");
        Perf.Out("═══════════════════════════════════════════");
        // BDN 失败（含 filter 形态错）就地失败——负载/内存/启动是补充维度可容忍，
        // BDN 是门禁数据源不可容忍（2026-09-30 修复：吞码缺陷）
        if (bdnExit != 0) return bdnExit;
        return 0;
    }

    /// <summary>统一报告生成（report 入口与 full-perf 第 6 步共用；失败不抛出——报告的价值恰在如实呈现回归）。</summary>
    public static void RunReport(string root, string resultsDir, string? workloadJson, string? memoryJson, string startup, string reportMd)
    {
        var benchBin = Path.Combine(root, "bench", "PalORM.Benchmarks", "bin", "Release", "net11.0");
        workloadJson ??= Perf.NewestFile(benchBin, "workload-sqlite-*.json");
        memoryJson ??= Perf.NewestFile(benchBin, "memory-sqlite.json");
        var startupFile = Path.Combine(root, "bench", "reports", "last-startup-status.txt");
        var startupArg = File.Exists(startupFile) ? File.ReadAllText(startupFile).Trim() : startup;
        Directory.CreateDirectory(Path.Combine(root, "bench", "reports"));
        var outPath = reportMd;

        var args = $"report --results \"{resultsDir}\" " +
            $"--baseline \"{Path.Combine(root, "bench", "baselines", "perf-baseline.json")}\" " +
            $"--envelopes \"{Path.Combine(root, "bench", "results")}\" " +
            $"--index-baseline \"{Path.Combine(root, "bench", "baselines", "perfhub-index-baseline.json")}\" " +
            $"--out \"{outPath}\"";
        if (workloadJson is not null)
        {
            args += $" --workload \"{workloadJson}\"";
        }
        if (memoryJson is not null)
        {
            args += $" --memory \"{memoryJson}\"";
        }
        if (startupArg.Length > 0)
        {
            args += $" --startup \"{startupArg}\"";
        }
        _ = Perf.Run("dotnet", $"run --project \"{Path.Combine(root, "tools", "PalORM.PerfGate")}\" -c Release -- {args}");
        Perf.Out($"报告: {outPath}");
    }

    private static void BuildTail(string projectDir, string csproj)
    {
        var (_, output) = Perf.RunCapture("dotnet", $"build \"{Path.Combine(projectDir, csproj)}\" -c Release --nologo");
        // 等价 `2>&1 | tail -2`：只显示最后两行（tail 直通保留子进程行尾）
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.TakeLast(2))
        {
            Console.WriteLine(line.TrimEnd('\r'));
        }
    }
}
