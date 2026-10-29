using System.Globalization;
using System.Text.Json;

namespace PalORM.AotProbe;

/// <summary>report 子命令：facts JSON → 判定模型 → Markdown。
/// 模板冻结纪律（crossbench 11 处漂移教训）：报告内容只由 facts 数据推导，禁手改生成物。
/// 口径标签固定为"SQLite 目标应用闭包 · 官方 AOT 路径"（裁决 2026-10-28）。</summary>
internal static class ReportCommand
{
    internal static int Run(string[] args)
    {
        string? factsPath = null, outPath = null;
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--facts": factsPath = args[i + 1]; i += 2; break;
                case "--out": outPath = args[i + 1]; i += 2; break;
                default: Console.WriteLine($"unknown arg {args[i]}"); return 2;
            }
        }

        string repoRoot = MatrixRunner.FindRepoRoot();
        factsPath ??= LatestFacts(repoRoot);
        if (factsPath is null || !File.Exists(factsPath))
        {
            Console.WriteLine($"facts file not found: {factsPath ?? "<none>"}");
            return 2;
        }

        FactsFile facts = JsonSerializer.Deserialize(File.ReadAllText(factsPath), ToolJsonContext.Default.FactsFile)
            ?? throw new InvalidOperationException($"invalid facts file: {factsPath}");
        string markdown = Render(facts);

        outPath ??= Path.Combine(repoRoot, "tmp", "aotprobe",
            $"report-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, markdown);
        Console.WriteLine(markdown);
        Console.WriteLine($"report -> {outPath}");
        return 0;
    }

    private static string? LatestFacts(string repoRoot)
    {
        string dir = Path.Combine(repoRoot, "tools", "AotProbe", "results");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "aotprobe-*.json").OrderByDescending(f => f).FirstOrDefault()
            : null;
    }

    private static string Render(FactsFile facts)
    {
        var lines = new List<string>
        {
            "# AotProbe 报告",
            string.Empty,
            $"口径：SQLite 目标应用闭包 · 官方 AOT 路径 | rules {facts.RulesVersion} | catalog {facts.CatalogVersion} | manifest {facts.ManifestVersion} | fixtures {facts.FixtureVersion}",
            $"环境：SDK {facts.Sdk} | RID {facts.Rid} | {facts.Os} | 生成于 {facts.GeneratedAtUtc} (UTC)",
            string.Empty,
            "## 全库清单与等级",
            string.Empty,
            "| 库 | 档位 | 适配器 | 主等级 | 规则命中 | 归因 | 实现机制 | 官方 AOT 路径 |",
            "|---|---|---|---|---|---|---|---|",
        };

        // 全库清单视图（用户指令 2026-10-28）：遍历 manifest 全部库，未测的明确标注，
        // 绝不编造档位——报告等级只来自 facts，档位/路径来自 manifest，两者都是机器可读输入
        var measured = facts.Runs.GroupBy(r => r.Library).ToDictionary(g => g.Key, g => g.ToList());
        var listed = new HashSet<string>();
        foreach (var lib in AotProbeManifest.Libraries)
        {
            listed.Add(lib.Id);
            AppendLibraryRow(lines, lib.Id, lib.LibTier, lib.AdapterReady, lib.OfficialAotPath, lib.Mechanism,
                measured.TryGetValue(lib.Id, out var runs) ? runs : null);
        }
        foreach (string extra in measured.Keys.Where(k => !listed.Contains(k)).Order())
            AppendLibraryRow(lines, extra, null, false, "已移出清单", "—", measured[extra]);

        lines.Add(string.Empty);
        lines.Add("## 臂矩阵");
        lines.Add(string.Empty);
        lines.Add("| 库/臂 | 二进制 | 启动 | 通过 | IL 告警 lib/fw/host |");
        lines.Add("|---|---|---|---|---|");
        foreach (ArmRun arm in facts.Runs)
        {
            int pass = arm.Probes.Count(p => p.Status == "pass");
            string warn = $"{arm.Warnings.Count(w => w.Attribution == "lib")}/{arm.Warnings.Count(w => w.Attribution == "framework")}/{arm.Warnings.Count(w => w.Attribution == "host")}";
            lines.Add($"| {arm.Library}/{arm.Group}-{arm.Form} | {(arm.BinaryReady ? "ok" : "FAIL")} | {(arm.StartupOk ? "ok" : "FAIL")} | {pass}/{arm.Probes.Count} | {warn} |");
        }

        var failing = facts.Runs.SelectMany(r => r.Probes.Where(p => p.Status != "pass").Select(p => (Arm: r, Probe: p))).ToList();
        if (failing.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## 失败明细");
            foreach (var (arm, p) in failing)
                AppendFailureDetail(lines, arm, p);
        }

        var libWarnings = facts.Runs.SelectMany(r => r.Warnings.Where(w => w.Attribution == "lib").Select(w => (Arm: r, Warn: w))).ToList();
        if (libWarnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## 库归属 IL 告警（未缓解，仅影响 ✅ 档，v2.2：裁剪得当不降档）");
            foreach (var (arm, w) in libWarnings)
                lines.Add($"- {arm.Library}/{arm.Group}-{arm.Form} {w.Code} [{w.Origin}] {w.Text}");
        }

        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>单条失败明细（含已知缺口注记——登记册只注记不改档）。</summary>
    private static void AppendFailureDetail(List<string> lines, ArmRun arm, ProbeRunResult p)
    {
        lines.Add(string.Empty);
        lines.Add($"### {arm.Library}/{arm.Group}-{arm.Form} · {p.Probe}（{p.Status}，exit={p.ExitCode}）");
        string? known = KnownGaps.Match(arm.Library, p.Probe);
        if (known is not null)
            lines.Add($"- 已知缺口（登记册）：{known}");
        if (p.ErrorType is not null)
            lines.Add($"- 异常：{p.ErrorType}: {p.ErrorMessage}");
        foreach (AssertionFailure a in p.Assertions)
            lines.Add($"- 断言不符：{a.Key}（{a.Kind}）期望 `{a.Expected}` 实测 `{a.Actual}`");
    }

    /// <summary>单行库状态：已测（A-aot 臂在 facts 里）出等级与归因；未测只标状态，绝不编造档位。</summary>
    private static void AppendLibraryRow(
        List<string> lines, string id, AotProbeManifest.Tier? tier, bool adapterReady, string officialPath,
        string mechanism, List<ArmRun>? runs)
    {
        string tierText = tier switch
        {
            AotProbeManifest.Tier.Headline => "headline",
            AotProbeManifest.Tier.Watch => "watch",
            _ => "—",
        };
        ArmRun? main = runs?.FirstOrDefault(r => r is { Group: "A", Form: "aot" });

        string gradeText, ruleText, attrText;
        if (main is not null)
        {
            GradeResult g = GradingRules.Evaluate(ToGradeInput(main, runs!));
            gradeText = $"{GradingRules.Symbol(g.Grade)} {g.Grade}";
            ruleText = $"{g.RuleId}：{g.RuleText}";
            attrText = GradingRules.Label(GradingRules.Attribute(ToArmInput(runs!)));
        }
        else if (runs is null || runs.Count == 0)
        {
            gradeText = "未测（S1）";
            ruleText = "—";
            attrText = "—";
        }
        else
        {
            gradeText = "主测定臂未跑";
            ruleText = "—";
            attrText = GradingRules.Label(GradingRules.Attribute(ToArmInput(runs)));
        }
        lines.Add($"| {id} | {tierText} | {(adapterReady ? "就绪" : "S1")} | {gradeText} | {ruleText} | {attrText} | {mechanism} | {officialPath} |");
    }

    /// <summary>判定输入（v2）：主测定臂（A-aot）+ JIT 对照臂（A-jit，用于剔除库固有失败）。</summary>
    private static GradeInput ToGradeInput(ArmRun main, List<ArmRun> runs)
    {
        ArmRun? jit = runs.FirstOrDefault(r => r.Group == "A" && r.Form == "jit");
        LayerStat Stat(ArmRun? arm, ProbeLayer l)
        {
            if (arm is null)
                return new LayerStat(0, 0);
            var byLayer = arm.Probes
                .GroupBy(p => Enum.TryParse<ProbeId>(p.Probe, out var id) ? ProbeCatalog.Def(id).Layer : ProbeLayer.P3)
                .ToDictionary(g => g.Key, g => new LayerStat(g.Count(p => p.Status == "pass"), g.Count()));
            return byLayer.TryGetValue(l, out var s) ? s : new LayerStat(0, 0);
        }
        LayerStat jitP0 = Stat(jit, ProbeLayer.P0);
        ArmRun? root = runs.FirstOrDefault(r => r.Group == "A" && r.Form == "root");
        LayerStat rootP0 = Stat(root, ProbeLayer.P0);
        // 库内部抑制的告警计入"未缓解"同权（抑制≠消除，用户裁决 2026-10-29：Dapper.AOT 封顶 🟦）
        int suppressed = AotProbeManifest.Libraries
            .FirstOrDefault(l => l.Id == main.Library)?.SuppressedWarnings ?? 0;
        return new GradeInput(main.BuildOk, main.PublishOk, main.StartupOk,
            Stat(main, ProbeLayer.P0),
            jitP0.Applicable > 0 ? jitP0 : Stat(main, ProbeLayer.P0), // 无 JIT 臂时退化为自身（保守）
            rootP0, // Root 臂未跑时 Applicable=0，不参与判定
            Stat(main, ProbeLayer.P1), Stat(main, ProbeLayer.P2), Stat(main, ProbeLayer.P3),
            main.Warnings.Count(w => w.Attribution == "lib") + suppressed);
    }

    private static ArmInput ToArmInput(List<ArmRun> runs)
    {
        bool? P0(string group, string form)
        {
            ArmRun? arm = runs.FirstOrDefault(r => r.Group == group && r.Form == form);
            return arm is null ? null : MatrixRunner.ArmP0Pass(arm);
        }
        return new ArmInput(P0("A", "jit"), P0("A", "aot"), P0("A", "trimjit"), P0("A", "root"),
            P0("B", "aot"), P0("B", "root"));
    }
}
