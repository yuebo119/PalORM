// perf 入口子命令（自 perf.sh 迁移，T4-6）：smoke/full/gate/report/index。
// 设计原则沿用原编排器：本层只做编排，不改任何夹具的口径——每步调用该夹具自己的既有入口。
namespace PalORM.PerfCli;

internal static class PerfEntry
{
    private const string GateProject = "tools/PalORM.PerfGate";
    private const string BdnResults = "BenchmarkDotNet.Artifacts/results";
    private const string BaselineBdn = "bench/baselines/perf-baseline.json";
    private const string BaselineIndex = "bench/baselines/perfhub-index-baseline.json";

    public static int Gate()
    {
        var root = Perf.RepoRoot();
        Perf.Step("[门禁 1/2] 微基准 BDN 基线");
        if (File.Exists(Path.Combine(root, BaselineBdn)))
        {
            // set -e 语义：任一 check 失败即终止（对拍实证：旧版首败即退，不落到第二步）
            var code = Perf.Run("dotnet",
                $"run --project \"{Path.Combine(root, GateProject)}\" -c Release -- " +
                $"check --results \"{Path.Combine(root, BdnResults)}\" --baseline \"{Path.Combine(root, BaselineBdn)}\"");
            if (code != 0)
            {
                return code;
            }
        }
        else
        {
            Perf.Out($"跳过：缺 {BaselineBdn}");
        }

        Perf.Step("[门禁 2/2] 结果库基线（PerfHub 能力矩阵 + DapperSuite 哨兵）");
        if (File.Exists(Path.Combine(root, BaselineIndex)))
        {
            return Perf.Run("dotnet",
                $"run --project \"{Path.Combine(root, GateProject)}\" -c Release -- check-index --baseline \"{Path.Combine(root, BaselineIndex)}\"");
        }
        Perf.Out($"跳过：缺 {BaselineIndex}（先跑 record-index 录制）");
        return 0;
    }

    public static int Index()
    {
        var root = Perf.RepoRoot();
        Perf.Step("[索引] 从结果库重建 bench/reports/perf-index.md");
        return Perf.Run("dotnet", $"run --project \"{Path.Combine(root, GateProject)}\" -c Release -- index");
    }

    public static int Report()
    {
        var root = Perf.RepoRoot();
        // 唯一报告产物（规范 §6）：BDN 门禁明细 + 负载/内存 + 跨夹具批次登记 + 维度总览 + 四组表，一份文件。
        Perf.Step("[报告] 统一报告（BDN 门禁明细 + 负载/内存 + 跨夹具批次登记 + 维度总览 + 四组表）");
        var outPath = Path.Combine(root, "bench", "reports", $"perf-report-{Perf.Stamp()}.md");
        FullPerf.RunReport(root, Path.Combine(root, BdnResults), null, null, "", outPath);

        // 四组表（性能输出规范的固定表格组）由门禁工具从最新非子集信封生成，追加进唯一报告——
        // 该段曾是每次跑测汇报的最大人工步骤（.ai 本地脚本原型已验证），固化后人工只写归因。
        string tablesPath = outPath + ".tables.md";
        int code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, GateProject)}\" -c Release --no-build -- tables "
            + $"--results \"{Path.Combine(root, "bench", "results")}\" --out \"{tablesPath}\"");
        if (code == 0 && File.Exists(tablesPath))
        {
            File.AppendAllText(outPath, File.ReadAllText(tablesPath));
            File.Delete(tablesPath);
        }
        else
        {
            Perf.Err("四组表生成失败（不影响报告主体）");
        }

        return 0;
    }

    public static int Smoke()
    {
        var root = Perf.RepoRoot();
        Perf.Step("[1/4] 微基准冒烟（单点查询，BDN）");
        // B104 同族（2026-10-01）：filter 不得带引号——ProcessStartInfo 无 shell，单引号是字面量，
        // 带引号的过滤串匹配 0 基准后退出码仍 0（假绿）
        var code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, "bench", "PalORM.Benchmarks")}\" -c Release -- --filter *ADO_NET_GetByKey*");
        if (code != 0)
        {
            return code;
        }

        Perf.Step("[2/4] PerfHub 冒烟（SQLite 2000 档，quick）");
        code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, "bench", "PalORM.PerfHub")}\" -c Release -- run --dialects sqlite --tiers 2000 --quick");
        if (code != 0)
        {
            return code;
        }

        Perf.Step("[3/4] DapperSuite 冒烟（SQLite 单行，官方形状）");
        Environment.SetEnvironmentVariable("DAPPER_SUITE_DIALECT", "sqlite");
        // 同 [1/4]：filter 不带引号（B104 同族修复）
        code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, "bench", "PalORM.DapperSuite")}\" -c Release -- --filter *SqlCommand* --join");
        if (code != 0)
        {
            return code;
        }

        Perf.Step("[4/4] 索引报告");
        return Index();
    }

    /// <param name="options">--with-dappersuite：把 DapperSuite 哨兵并回本轮。默认**移出**（2026-10-03
    /// 降频裁决：连续多轮 9 项零发现，信息增量低）：哨兵改按周或驱动/大版本变更时手动并回，
    /// 统一报告的跨夹具登记会显示其最后批次时间，超期即可见。</param>
    public static int Full(string[] options)
    {
        bool withDapperSuite = options is not null && Array.IndexOf(options, "--with-dappersuite") >= 0;
        var root = Perf.RepoRoot();
        Perf.ClearSteps();
        Perf.StartClock();
        int stepTotal = withDapperSuite ? 5 : 4;
        var stepIdx = 0;
        List<string> stepFailed = [];

        void RunStep(string name, Func<int> action)
        {
            stepIdx++;
            Perf.Step($"[{stepIdx}/{stepTotal}] {name}");
            var t0 = DateTime.Now;
            var code = action();
            var status = code == 0 ? "OK" : "FAIL";
            if (code != 0)
            {
                // 单步失败不中断全量：全量的价值是最大覆盖，每步的结果库登记与健康度会如实反映状态；
                // 失败汇总到末尾并以非零退出（不静默吞掉）。
                stepFailed.Add(name);
            }
            Perf.RecordStep($"[{stepIdx}/{stepTotal}] {name}", status, Perf.ElapsedSince(t0), Perf.ElapsedSince(Perf.TotalStart));
        }

        // 步骤 1 跳过其内部报告：统一报告必须等三套夹具都写完结果库信封后再生成
        //（否则报告里的跨夹具节只含上一轮的批次——顺序错了就会得出"PerfHub 未采集"的假缺口）。
        RunStep("微基准全量 + 负载 + 内存 + 启动 + BDN 门禁", () =>
        {
            var prev = Environment.GetEnvironmentVariable("SKIP_REPORT");
            Environment.SetEnvironmentVariable("SKIP_REPORT", "1");
            try
            {
                return FullPerf.Run(skipReport: true, withRemote: Environment.GetEnvironmentVariable("WITH_REMOTE") == "1");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SKIP_REPORT", prev);
            }
        });
        RunStep("PerfHub 三方言全量（含并发扩展档）", () => Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, "bench", "PalORM.PerfHub")}\" -c Release -- " +
            "run --dialects sqlite,mysql,pg --tiers 2000,20000 --concurrency --threads 1,4,8"));
        // DapperSuite 只跑 SQLite：定位是"与 Dapper 官方数字可对照的外部锚点"，官方数字是
        // 单机 SQLite 的（2026-09-23 精简）。哨兵目的一个方言足够。
        // 2026-10-03 降频：移出默认全量（--with-dappersuite 并回），理由见方法 doc。
        if (withDapperSuite)
        {
            RunStep("DapperSuite SQLite（官方形状锚点）", () => DapperSuite.Run(["sqlite"]));
        }
        RunStep("门禁（BDN 基线 + 结果库基线）", Gate);
        RunStep("统一报告", Report);

        if (stepFailed.Count > 0)
        {
            Perf.Out("");
            Perf.Err($"全量跑测有失败步骤：{string.Join(' ', stepFailed)}");
            Perf.PrintFinalTable();
            return 1;
        }
        Perf.Out("");
        Perf.Out($"全量跑测完成：{(withDapperSuite ? "五" : "四")}步全通过，总耗时 {Perf.FmtDur(Perf.ElapsedSince(Perf.TotalStart))}。");
        Perf.PrintFinalTable();
        return 0;
    }

    public static int Usage()
    {
        Perf.Out("""
            用法:
              PerfCli smoke                                    # 三套夹具最小档冒烟（SQLite，约 5 分钟）
              PerfCli full [--with-dappersuite]                # 全量（三方言）+ 门禁 + 唯一报告
                                                               # DapperSuite 默认降频，--with-dappersuite 并回
              PerfCli compare <基线worktree> <轮数> [选项]      # 交替 A/B（转发 compare 编排器）
              PerfCli gate                                     # 只跑门禁（BDN 基线 + 结果库基线）
              PerfCli report                                   # 只重建统一报告（不重跑夹具）
              PerfCli index                                    # 只重建跨夹具索引（单独查看用）
              PerfCli bench-matrix|full-perf|dappersuite ...   # 内部步骤子命令（可独立调试）

            设计：本入口只做编排，不改任何夹具的口径——每步都调用该夹具自己的既有入口，
            保证"从统一入口跑"与"单独跑某套夹具"得到同样的结果。
            报告只有一个产物：bench/reports/perf-report-<时间戳>.md（在全部夹具写完成结果库之后生成）。
            """);
        return 1;
    }
}
