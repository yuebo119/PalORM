// perf 入口子命令（自 perf.sh 迁移，T4-6）：smoke/full/gate/report/index/l0/crossbench。
// 设计原则沿用原编排器：本层只做编排，不改任何夹具的口径——每步调用该夹具自己的既有入口。
using PalORM.Bench.Shared;

namespace PalORM.PerfCli;

internal static class PerfEntry
{
    private const string GateProject = "tools/PalORM.PerfGate";
    private const string BdnResults = "BenchmarkDotNet.Artifacts/results";
    private const string BaselineBdn = "bench/baselines/perf-baseline.json";
    private const string BaselineIndex = "bench/baselines/perfhub-index-baseline.json";
    private const string CrossBenchProject = "bench/crossbench/PalormProtocol";
    private const string CrossBenchResultsDir = "bench/crossbench/results";

    /// <param name="ownSession">true=独立命令（自己起时钟与会话日志）；false=被 full 编排调用
    /// （沿用 full 的时钟与日志）。2026-10-19 实测缺陷：作为 full 的一步时无条件 StartClock 把
    /// 总时钟重置到门禁步，导致汇总百分比出现 7640% 之类失真、累计时间从 3s 重新计；同时
    /// Begin/EndSessionLog 嵌套创建 gate 日志并提前关闭 full 日志（full 日志缺 exit 尾行）。</param>
    public static int Gate(bool ownSession = true)
    {
        var root = Perf.RepoRoot();
        if (ownSession)
        {
            Perf.StartClock();
            Perf.BeginSessionLog(root, "gate");
        }
        Perf.Step("[门禁 1/2] 微基准 BDN 基线");
        if (File.Exists(Path.Combine(root, BaselineBdn)))
        {
            // set -e 语义：任一 check 失败即终止（对拍实证：旧版首败即退，不落到第二步）
            var code = Perf.Run("dotnet",
                $"run --project \"{Path.Combine(root, GateProject)}\" -c Release -- " +
                $"check --results \"{Path.Combine(root, BdnResults)}\" --baseline \"{Path.Combine(root, BaselineBdn)}\"");
            if (code != 0)
            {
                if (ownSession) Perf.EndSessionLog(code);
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
            var finalCode = Perf.Run("dotnet",
                $"run --project \"{Path.Combine(root, GateProject)}\" -c Release -- check-index --baseline \"{Path.Combine(root, BaselineIndex)}\"");
            if (ownSession) Perf.EndSessionLog(finalCode);
            return finalCode;
        }
        Perf.Out($"跳过：缺 {BaselineIndex}（先跑 record-index 录制）");
        if (ownSession) Perf.EndSessionLog(0);
        return 0;
    }

    public static int Index()
    {
        var root = Perf.RepoRoot();
        Perf.Step("[索引] 从结果库重建 bench/reports/perf-index.md");
        return Perf.Run("dotnet", $"run --project \"{Path.Combine(root, GateProject)}\" -c Release -- index");
    }

    /// <summary>L0 快线（作战卡 §6.3）：门禁关键项 5 分钟反馈通道。
    /// <para>覆盖 = 批量族四形态 + GetAllAsync + GetByKey 延迟哨兵，三方言、20000 档。
    /// 判定 = L0 专用基线（比值 +30% / 分配 +20%），三方言缺一即失败；判定窗口是最近 3 个
    /// L0 批次的窗口中位数（单批离群被吸收，持续回归需连续 2 批才越线，B203 同源口径）。
    /// BDN 不进 L0（单独约 5 分钟），微基准回归仍由全量轮覆盖。</para></summary>
    public static int L0()
    {
        var root = Perf.RepoRoot();
        Perf.ClearSteps();
        Perf.StartClock();
        Perf.BeginSessionLog(root, "l0");
        string baseline = Path.Combine(root, "bench", "baselines", "perfhub-l0-baseline.json");

        Perf.Step($"[1/2] PerfHub L0 子集（{PerfL0.Operations.Length} 操作 × 三方言 × {PerfL0.Tier} 档）");
        var t0 = DateTime.Now;
        int code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, "bench", "PalORM.PerfHub")}\" -c Release -- run "
            + $"--dialects sqlite,mysql,pg --tiers {PerfL0.Tier} --ops {PerfL0.OpsArgument} "
            + $"--label {PerfL0.Label} --no-report");
        Perf.RecordStep("[1/2] PerfHub L0 子集", code == 0 ? "OK" : "FAIL",
            Perf.ElapsedSince(t0), Perf.ElapsedSince(Perf.TotalStart));
        if (code != 0)
        {
            Perf.Err("L0 子集测量失败——检查方言连接或夹具状态");
            Perf.PrintFinalTable();
            Perf.EndSessionLog(code);
            return code;
        }

        Perf.Step("[2/2] L0 判定（3 批窗口，比值 +30% / 分配 +20%，三方言覆盖）");
        t0 = DateTime.Now;
        code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, GateProject)}\" -c Release -- "
            + $"check-l0 --baseline \"{baseline}\"");
        Perf.RecordStep("[2/2] L0 判定", code == 0 ? "OK" : "FAIL",
            Perf.ElapsedSince(t0), Perf.ElapsedSince(Perf.TotalStart));
        Perf.PrintFinalTable();
        Perf.EndSessionLog(code);
        return code;
    }

    public static int Report()
    {
        var root = Perf.RepoRoot();
        // 唯一报告产物（规范 §6）：BDN 门禁明细 + 负载/内存 + 跨夹具批次登记 + 维度总览 + 8+1 组横评，一份文件。
        Perf.Step("[报告] 统一报告（BDN 门禁明细 + 负载/内存 + 跨夹具批次登记 + 维度总览 + 8+1 组横评）");
        var outPath = Path.Combine(root, "bench", "reports", $"perf-report-{Perf.Stamp()}.md");
        FullPerf.RunReport(root, Path.Combine(root, BdnResults), null, null, "", outPath);

        // 四组表已升级为 8+1 组横评表（2026-11-02 用户定标「单源 8+1 重组」）：数据源 = 最近完整
        // CrossBench 批，表一~表八（CRUD/批量/事务/跨方言/分配/补充读/覆盖账本/异常判读）。
        // 该段曾是每次跑测汇报的最大人工步骤，固化后人工只写归因。
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
            Perf.Err("8+1 组横评表生成失败（不影响报告主体）");
        }

        // 8+1 组横评（2026-11-02 单源重组）：表一~表七与回归节合并为同一份产物，数据源 =
        // 最近完整 CrossBench 批（9 组 × 三方言 × 两档，同批同轮），分母统一为同批 ADO 基准。
        // 旧的 CrossBench 五臂节（scripts/crossbench-report.cs 冻结模板）不再追加：它只覆盖
        // 5 组，与横评表重复且组数口径不一致。
        VerifyReportSections(outPath);
        return 0;
    }

    /// <summary>报告章节齐全性校验（2026-11-02，模板 docs/性能报告模板.md 的机械载体）：
    /// 模板定义的关键章节任一缺失即报错到 stderr。防的失效形态是横评表生成失败后报告静默
    /// 缺整节（旧实现只打一行 Err 就返回 0），读者拿到的是"看起来完整"的半份报告。
    /// 不阻断退出码：报告主体（BDN/回归）仍有价值，与既有容错口径一致。</summary>
    private static void VerifyReportSections(string reportPath)
    {
        string[] required =
        [
            "## 数据源与新鲜度",
            "## 12 维总览",
            "## 8+1 组跨库横评",
            "**一、CRUD 单行与读**",
            "**七、异常判读**",
            "**图例**",
        ];
        if (!File.Exists(reportPath))
        {
            Perf.Err($"[报告模板] 产物不存在：{reportPath}");
            return;
        }

        string text = File.ReadAllText(reportPath);
        string[] missing = [.. required.Where(s => !text.Contains(s, StringComparison.Ordinal))];
        if (missing.Length > 0)
        {
            Perf.Err($"[报告模板] 章节缺失：{string.Join("、", missing)}"
                + "（对照 docs/性能报告模板.md；横评表生成失败时先查 tables 步骤的退出码）");
        }
    }

    /// <summary>CrossBench 8+1 组交叉批（ADO 基准 + 8 库）+ 横评报告（表一~表八）。
    /// 2026-10-25 用户指令起为 full 默认步骤；2026-11-02 单源重组：报告由 PerfGate tables 从
    /// 同一完整批生成，与统一报告里的横评节同源同模板（旧 crossbench-report.cs 冻结模板退役）。
    /// 臂序两遍反序、慢操作预算表等协议见 bench/crossbench/README.md。</summary>
    public static int CrossBench(bool ownSession = true)
    {
        var root = Perf.RepoRoot();
        if (ownSession)
        {
            Perf.ClearSteps();
            Perf.StartClock();
            Perf.BeginSessionLog(root, "crossbench");
        }
        var t0 = DateTime.Now;
        Perf.Step("[1/2] CrossBench 8+1 组全量批（三方言 × 两档 × 17 操作 × 9 组，两遍反序）");
        int code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, CrossBenchProject)}\" -c Release -- " +
            "--dialects sqlite,mysql,pg 2000 20000");
        if (ownSession)
        {
            Perf.RecordStep("[1/2] CrossBench 8+1 组全量批", code == 0 ? "OK" : "FAIL",
                Perf.ElapsedSince(t0), Perf.ElapsedSince(Perf.TotalStart));
        }
        if (code != 0)
        {
            if (ownSession)
            {
                Perf.PrintFinalTable();
                Perf.EndSessionLog(code);
            }
            return code;
        }

        Perf.Step("[2/2] 冻结模板单批报告（scripts/crossbench-report.cs）");
        t0 = DateTime.Now;
        code = CrossBenchReportLatest(root);
        if (ownSession)
        {
            Perf.RecordStep("[2/2] 冻结模板单批报告", code == 0 ? "OK" : "FAIL",
                Perf.ElapsedSince(t0), Perf.ElapsedSince(Perf.TotalStart));
            Perf.PrintFinalTable();
            Perf.EndSessionLog(code);
        }
        return code;
    }

    /// <summary>对最近完整 CrossBench 批生成 8+1 组横评报告，落 bench/reports/crossbench-report-&lt;戳&gt;.md。
    /// full 编排与独立 crossbench 子命令共用；无完整批时返回 0 并提示（不视为失败）。</summary>
    private static int CrossBenchReportLatest(string root)
    {
        string? batch = CrossBenchBatch.LatestFullBatchPath(Path.Combine(root, CrossBenchResultsDir));
        if (batch == null)
        {
            Perf.Out("跳过：无完整 CrossBench 批（8 库 + ADO 基准 × 三方言 × 两档）");
            return 0;
        }
        string outPath = Path.Combine(root, "bench", "reports", $"crossbench-report-{Perf.Stamp()}.md");
        int code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, GateProject)}\" -c Release -- tables "
            + $"--results \"{Path.Combine(root, "bench", "results")}\" --out \"{outPath}\"");
        if (code == 0)
        {
            Perf.Out($"CrossBench 报告: {outPath}");
        }
        return code;
    }

    /// <summary>跳过横评时说明报告取数来源与陈旧程度：比值在同一批内推导，陈旧不影响自洽性，
    /// 但读报告的人必须知道 8+1 节是哪一天的数据。</summary>
    private static string DescribeCrossBenchSource(string root)
    {
        const string hint = "周度任务（full --with-crossbench 开启）";
        string? batch = CrossBenchBatch.LatestFullBatchPath(Path.Combine(root, CrossBenchResultsDir));
        if (batch is null)
        {
            return $"{hint}；本机尚无完整横评批，报告的 8+1 节会缺失";
        }

        string name = Path.GetFileName(batch);
        string date = name.Length >= 19 ? name.Substring(11, 8) : string.Empty;
        if (!DateTime.TryParseExact(
                date, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime day))
        {
            return $"{hint}；报告取最近完整批 {name}";
        }

        int ageDays = (DateTime.Today - day.Date).Days;
        return $"{hint}；报告取最近完整批 {name}（{ageDays} 天前）";
    }

    /// <summary>最近的完整 CrossBench 批判别与装载走 <see cref="CrossBenchBatch"/>（与 PerfGate
    /// 8+1 组横评表共用同一份判据：9 组 × 三方言 × 两档 = 54 三元组）。</summary>
    public static int Smoke()
    {
        var root = Perf.RepoRoot();
        Perf.StartClock();
        Perf.BeginSessionLog(root, "smoke");
        Perf.Step("[1/4] 微基准冒烟（单点查询，BDN）");
        // B104 同族（2026-10-01）：filter 不得带引号——ProcessStartInfo 无 shell，单引号是字面量，
        // 带引号的过滤串匹配 0 基准后退出码仍 0（假绿）
        var code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, "bench", "PalORM.Benchmarks")}\" -c Release -- --filter *ADO_NET_GetByKey*");
        if (code != 0)
        {
            Perf.EndSessionLog(code);
            return code;
        }

        Perf.Step("[2/4] PerfHub 冒烟（SQLite 2000 档，quick）");
        code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, "bench", "PalORM.PerfHub")}\" -c Release -- run --dialects sqlite --tiers 2000 --quick");
        if (code != 0)
        {
            Perf.EndSessionLog(code);
            return code;
        }

        Perf.Step("[3/4] DapperSuite 冒烟（SQLite 单行，官方形状）");
        Environment.SetEnvironmentVariable("DAPPER_SUITE_DIALECT", "sqlite");
        // 同 [1/4]：filter 不带引号（B104 同族修复）
        code = Perf.Run("dotnet",
            $"run --project \"{Path.Combine(root, "bench", "PalORM.DapperSuite")}\" -c Release -- --filter *SqlCommand* --join");
        if (code != 0)
        {
            Perf.EndSessionLog(code);
            return code;
        }

        Perf.Step("[4/4] 索引报告");
        var finalCode = Index();
        Perf.EndSessionLog(finalCode);
        return finalCode;
    }

    /// <param name="options">--with-crossbench：把横评并回本轮。默认**跳过**（2026-11-02 用户定标：
    /// 横评改周度，日常只跑 PerfHub 与微基准）：跳过时报告仍从最近完整批取 8+1 表并打印该批
    /// 文件名与天数，超过一周即在日志里可见。--with-dappersuite：把 DapperSuite 哨兵并回本轮。
    /// 默认移出（2026-10-03 降频裁决：连续多轮 9 项零发现，信息增量低）：哨兵改按周或驱动/大版本
    /// 变更时手动并回，统一报告的跨夹具登记会显示其最后批次时间，超期即可见。</param>
    public static int Full(string[] options)
    {
        // B135（2026-10-04 实录）：full 是 20 分钟级的昂贵动作，曾把 `full --help` 的 --help
        // 当未知参数静默忽略直接开跑全量。未知参数一律响亮拒绝（含 -h/--help 输出用法），
        // 防拼错参数名（如 --with-dappersuit）被静默降级成默认全量。
        if (RejectUnknownOptions(options))
        {
            return Usage();
        }

        bool withDapperSuite = options is not null && Array.IndexOf(options, "--with-dappersuite") >= 0;
        // 2026-11-02 用户定标：横评（CrossBench）改为周度任务，日常 full 默认跳过。
        // 报告取数不变——仍从"最近完整批"读，跳过只是不刷新它；跳过时会打印该批文件名。
        bool withCrossBench = options is not null && Array.IndexOf(options, "--with-crossbench") >= 0;
        // 阶段 0 时间分解（作战卡 §4）：仅在 PALORM_PERFHUB_TIMING=1 时多跑一步，
        // 默认流程的步骤数与行为保持不变（红线：不改变既有输出格式与判定）。
        bool timingReport = Environment.GetEnvironmentVariable("PALORM_PERFHUB_TIMING") == "1";
        var root = Perf.RepoRoot();
        Perf.ClearSteps();
        Perf.StartClock();
        Perf.BeginSessionLog(root, "full");
        int stepTotal = (withDapperSuite ? 6 : 5) + (timingReport ? 1 : 0);
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

        // 时间分解作为可选步骤：默认关闭时步骤数/行为与改造前一致（阶段 0 不改默认流程）。
        void RunTimingReportStep()
        {
            if (timingReport)
            {
                RunStep("时间分解（阶段 0 旁车）", () => TimingReport.Build());
            }
        }

        // 跳过也占一个步骤位：否则步骤总数在周度与日常之间变化，日志与最终表的编号对不上，
        // 读日志的人无法判断"少的那步"是跳过还是没跑到。
        void RunSkipped(string name, string reason)
        {
            stepIdx++;
            Perf.Step($"[{stepIdx}/{stepTotal}] {name}");
            Perf.Out($"跳过：{reason}");
            Perf.RecordStep($"[{stepIdx}/{stepTotal}] {name}", "SKIP", 0, Perf.ElapsedSince(Perf.TotalStart));
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
        if (withCrossBench)
        {
            RunStep("CrossBench 8+1 组交叉批（横评）", () => CrossBench(ownSession: false));
        }
        else
        {
            RunSkipped("CrossBench 8+1 组交叉批（横评）", DescribeCrossBenchSource(root));
        }
        RunStep("门禁（BDN 基线 + 结果库基线）", () => Gate(ownSession: false));
        RunStep("统一报告", Report);
        RunTimingReportStep();

        if (stepFailed.Count > 0)
        {
            Perf.Out("");
            Perf.Err($"全量跑测有失败步骤：{string.Join(' ', stepFailed)}");
            Perf.PrintFinalTable();
            var failCode = 1;
            Perf.EndSessionLog(failCode);
            return failCode;
        }
        Perf.Out("");
        Perf.Out($"全量跑测完成：{Numeral(stepTotal)}步无失败，总耗时 {Perf.FmtDur(Perf.ElapsedSince(Perf.TotalStart))}。");
        Perf.PrintFinalTable();
        var okCode = 0;
        Perf.EndSessionLog(okCode);
        return okCode;
    }

    /// <summary>步骤数的中文数字（默认五步，含被跳过的横评；--with-dappersuite 六步；开计时旁车再加一步）。</summary>
    private static string Numeral(int stepTotal) => stepTotal switch
    {
        4 => "四",
        5 => "五",
        _ => stepTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>full 的参数白名单校验（B135）：未知参数或 -h/--help 一律打印用法并拒绝执行。
    /// 返回 true 表示"不要开跑"。</summary>
    private static bool RejectUnknownOptions(string[] options)
    {
        string[] known = ["--with-dappersuite", "--with-crossbench", "-h", "--help"];
        string[] unknown = options is null
            ? []
            : [.. options.Where(o => !known.Contains(o, StringComparer.Ordinal))];
        if (unknown.Length > 0)
        {
            Perf.Out("full：未知参数 " + string.Join(' ', unknown));
            return true;
        }
        return options is not null
            && (Array.IndexOf(options, "-h") >= 0 || Array.IndexOf(options, "--help") >= 0);
    }

    public static int Usage()
    {
        Perf.Out("""
            用法:
              PerfCli smoke                                    # 三套夹具最小档冒烟（SQLite，约 5 分钟）
              PerfCli full [--with-crossbench] [--with-dappersuite]
                                                               # 日常：微基准 + 负载 + 内存 + 启动 + BDN + PerfHub + 门禁 + 唯一报告
                                                               # 横评（CrossBench）改为周度，默认跳过；--with-crossbench 开启
                                                               # 跳过时报告仍从最近完整批取 8+1 表，并打印该批文件名与天数
                                                               # DapperSuite 默认降频，--with-dappersuite 并回
              PerfCli crossbench                               # 只跑 CrossBench 8+1 组批 + 横评报告（表一~表八）
              PerfCli compare <基线worktree> <轮数> [选项]      # 交替 A/B（转发 compare 编排器）
              PerfCli gate                                     # 只跑门禁（BDN 基线 + 结果库基线）
              PerfCli report                                   # 只重建统一报告（不重跑夹具）
              PerfCli index                                    # 只重建跨夹具索引（单独查看用）
              PerfCli l0                                       # L0 快线（关键项快速反馈，目标 ≤5 分钟）
              PerfCli timing-report [旁车路径] [输出路径]       # 阶段 0：时间分解（读 perf-timing-*.json）
              PerfCli bench-matrix|full-perf|dappersuite ...   # 内部步骤子命令（可独立调试）

            设计：本入口只做编排，不改任何夹具的口径——每步都调用该夹具自己的既有入口，
            保证"从统一入口跑"与"单独跑某套夹具"得到同样的结果。
            报告只有一个产物：bench/reports/perf-report-<时间戳>.md（在全部夹具写完成结果库之后生成）。
            """);
        return 1;
    }
}
