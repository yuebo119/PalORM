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
            "## 等级总表",
            string.Empty,
            "| 库 | 主等级 | 规则命中 | 归因 |",
            "|---|---|---|---|",
        };

        foreach (var libRuns in facts.Runs.GroupBy(r => r.Library))
        {
            ArmRun? main = libRuns.FirstOrDefault(r => r is { Group: "A", Form: "aot" });
            ArmInput arms = ToArmInput(libRuns.ToList());

            if (main is null)
            {
                lines.Add($"| {libRuns.Key} | — | 主测定臂（A-aot）未跑 | {GradingRules.Label(GradingRules.Attribute(arms))} |");
                continue;
            }

            GradeInput input = ToGradeInput(main);
            GradeResult g = GradingRules.Evaluate(input);
            lines.Add($"| {libRuns.Key} | {GradingRules.Symbol(g.Grade)} {g.Grade} | {g.RuleId}：{g.RuleText} | {GradingRules.Label(GradingRules.Attribute(arms))} |");
        }

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
            {
                lines.Add(string.Empty);
                lines.Add($"### {arm.Library}/{arm.Group}-{arm.Form} · {p.Probe}（{p.Status}，exit={p.ExitCode}）");
                if (p.ErrorType is not null)
                    lines.Add($"- 异常：{p.ErrorType}: {p.ErrorMessage}");
                foreach (AssertionFailure a in p.Assertions)
                    lines.Add($"- 断言不符：{a.Key}（{a.Kind}）期望 `{a.Expected}` 实测 `{a.Actual}`");
            }
        }

        var libWarnings = facts.Runs.SelectMany(r => r.Warnings.Where(w => w.Attribution == "lib").Select(w => (Arm: r, Warn: w))).ToList();
        if (libWarnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## 库归属 IL 告警（未缓解，影响 ✅ 档）");
            foreach (var (arm, w) in libWarnings)
                lines.Add($"- {arm.Library}/{arm.Group}-{arm.Form} {w.Code} [{w.Origin}] {w.Text}");
        }

        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    private static GradeInput ToGradeInput(ArmRun main)
    {
        var byLayer = main.Probes
            .GroupBy(p => Enum.TryParse<ProbeId>(p.Probe, out var id) ? ProbeCatalog.Def(id).Layer : ProbeLayer.P3)
            .ToDictionary(g => g.Key, g => new LayerStat(g.Count(p => p.Status == "pass"), g.Count()));
        LayerStat Stat(ProbeLayer l) => byLayer.TryGetValue(l, out var s) ? s : new LayerStat(0, 0);
        return new GradeInput(main.BuildOk, main.PublishOk, main.StartupOk,
            Stat(ProbeLayer.P0), Stat(ProbeLayer.P1), Stat(ProbeLayer.P2), Stat(ProbeLayer.P3),
            main.Warnings.Count(w => w.Attribution == "lib"));
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
