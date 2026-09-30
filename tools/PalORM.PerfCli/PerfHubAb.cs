// compare 子命令（自 perfhub-ab.sh 迁移，T4-5）：PerfHub 交替 A/B 编排器。
// 跨版本对比的唯一可信执行方式（规范 §5）。
//
// 设计（v2 方案 §6）:
//   · 块 = 方言 × 档位。每个块内 HEAD 与基线背靠背各跑一遍，块间隔 <2 分钟——
//     机器与服务端状态漂移在两版间均匀分摊（顺序跑两轮的 v1 教训：地板单项漂移 50%）。
//   · 每轮每版每块各产出一份 history JSON（--label ab/<round>/<dialect>/<tier> 标记），
//     报告的 A/B 段按 label 配对、逐轮取中位、展示轮间散布。
//   · 基线 worktree 需已就绪（含 PerfHub 夹具 + IVT + .env.test），
//     见 docs/v5.8-perfhub-v2-plan.md 阶段 4.3 的就绪清单。
namespace PalORM.PerfCli;

internal static class PerfHubAb
{
    public static int Run(string[] args)
    {
        var root = Perf.RepoRoot();
        if (args.Length < 2)
        {
            Perf.Err("用法: PerfCli compare <基线worktree路径> <轮数> [选项...]");
            return 1;
        }
        var baselineDir = args[0];
        if (!int.TryParse(args[1], out var rounds))
        {
            Perf.Err("缺少轮数");
            return 1;
        }
        var extraArgs = args.Skip(2).ToArray();
        var work = Path.Combine(root, "bench", "perfhub", "results");

        // 选项默认值（环境变量可作缺省，EXTRA_ARGS 显式覆盖优先）
        var (dialects, tiers) = ResolveDialectsAndTiers(extraArgs);
        var dialectList = dialects.Split(',');
        var tierList = tiers.Split(',');

        // 已解析的方言/档位选项不再转发：PerfHub 只认空格形式，原样转发 `--dialects=pg`
        // 会以"未知选项"立刻退出（实测：按 `=` 形式调用 11 秒即失败）
        var forwardArgs = extraArgs
            .Where(a => !a.StartsWith("--dialects=", StringComparison.Ordinal) && !a.StartsWith("--tiers=", StringComparison.Ordinal))
            .ToList();

        Perf.Out($"[A/B] HEAD={root}");
        Perf.Out($"[A/B] 基线={baselineDir}（须已含 PerfHub + IVT + .env.test）");
        Perf.Out($"[A/B] 轮数={rounds}  方言={string.Join(' ', dialectList)}  档位={string.Join(' ', tierList)}");
        Perf.Out($"[A/B] 结果写入 {work}（label=ab/<round>/<dialect>/<tier>）");
        Perf.Out("");

        for (var round = 1; round <= rounds; round++)
        {
            foreach (var dialect in dialectList)
            {
                foreach (var tier in tierList)
                {
                    // 轮内交替起跑顺序：奇数轮 HEAD 先、偶数轮基线先——连起跑顺序的系统性偏差也抵消
                    if (round % 2 == 1)
                    {
                        RunOne(root, "HEAD", round, dialect, tier, forwardArgs);
                        RunOne(baselineDir, "v5.5.1", round, dialect, tier, forwardArgs);
                    }
                    else
                    {
                        RunOne(baselineDir, "v5.5.1", round, dialect, tier, forwardArgs);
                        RunOne(root, "HEAD", round, dialect, tier, forwardArgs);
                    }
                }
            }
        }

        // 基线的 history JSON 拷回主仓（报告按 label 聚合，两版数据须同目录）
        var copied = CopyBackHistories(baselineDir, work);
        Perf.Out("");
        Perf.Out($"[A/B] 完成：拷回基线历史 {copied} 份。生成报告：dotnet run --project bench/PalORM.PerfHub -- report");
        return 0;
    }

    private static int CopyBackHistories(string baselineDir, string work)
    {
        var copied = 0;
        var baselineResultsDir = Path.Combine(baselineDir, "bench", "perfhub", "results");
        if (!Directory.Exists(baselineResultsDir))
        {
            return 0;
        }
        foreach (var f in Directory.GetFiles(baselineResultsDir, "history-*.json"))
        {
            var baseName = Path.GetFileName(f);
            if (File.Exists(Path.Combine(work, baseName)))
            {
                continue;
            }
            File.Copy(f, Path.Combine(work, baseName));
            copied++;
        }
        return copied;
    }

    private static (string Dialects, string Tiers) ResolveDialectsAndTiers(string[] extraArgs)
    {
        // 选项默认值（环境变量可作缺省，EXTRA_ARGS 显式覆盖优先）
        var dialects = Environment.GetEnvironmentVariable("DIALECTS") is { } de && de.Length > 0 ? de : "sqlite,mysql,pg";
        var tiers = Environment.GetEnvironmentVariable("TIERS") is { } te && te.Length > 0 ? te : "2000";
        foreach (var arg in extraArgs)
        {
            if (arg.StartsWith("--dialects=", StringComparison.Ordinal))
            {
                dialects = arg["--dialects=".Length..];
            }
            else if (arg.StartsWith("--tiers=", StringComparison.Ordinal))
            {
                tiers = arg["--tiers=".Length..];
            }
        }
        return (dialects, tiers);
    }

    private static void RunOne(string projectDir, string version, int round, string dialect, string tier, List<string> forwardArgs)
    {
        Perf.Out($"── {DateTime.Now:HH:mm:ss} [{version}] r{round} {dialect}/{tier} ──");
        // 等价 .sh 的子 shell 内 (cd $dir && dotnet run ...)：切工作目录后以相对项目路径跑
        var cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = projectDir;
        try
        {
            var fwd = forwardArgs.Count > 0 ? " " + string.Join(' ', forwardArgs) : "";
            var (exitCode, output) = Perf.RunCapture("dotnet",
                $"run --project bench/PalORM.PerfHub -c Release -- " +
                $"run --dialects \"{dialect}\" --tiers \"{tier}\" --version \"{version}\" " +
                $"--label \"ab/{round}/{dialect}/{tier}\"{fwd}");
            if (exitCode != 0)
            {
                Console.Error.Write(output);
                Perf.Err($"[A/B] 失败: {version} r{round} {dialect}/{tier}");
                Environment.Exit(1);
            }
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }
}
