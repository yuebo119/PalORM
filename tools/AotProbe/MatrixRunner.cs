using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PalORM.AotProbe;

/// <summary>矩阵 Runner：变体生成 → 构建发布 → 逐探针进程执行 → 断言 → facts JSON。
/// <para>阶梯（S0）：A-jit（适配器正确性对照）→ A-aot（主测定）；
/// B 组仅对 A-aot P0 全过者跑（物化相关探针）；--diagnose 时 A-aot 失败追加 trimjit 诊断臂。</para></summary>
internal static partial class MatrixRunner
{
    /// <summary>单探针超时；无重试机制（SQLite 文件库确定性环境，环境误判由 JIT 臂兜底——裁决 2026-10-28）。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);
    private const string MarkerPrefix = "AOTPROBE-OBS ";

    internal static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    internal sealed record Options(
        string? LibId, string? Rid, List<string> Forms, HashSet<string> Groups, bool Diagnose);

    /// <summary>while 手动步进而非 for 内 ++i：S127 禁止在 for 循环体改循环变量。</summary>
    private static Options ParseArgs(string[] args)
    {
        string? libId = null, rid = null;
        var forms = new List<string>(["jit", "aot"]);
        var groups = new HashSet<string>(["A", "B"]);
        bool diagnose = false;
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--lib": libId = args[i + 1]; i += 2; break;
                case "--rid": rid = args[i + 1]; i += 2; break;
                case "--forms": forms = [.. args[i + 1].Split(',', StringSplitOptions.TrimEntries)]; i += 2; break;
                case "--groups": groups = [.. args[i + 1].Split(',', StringSplitOptions.TrimEntries)]; i += 2; break;
                case "--diagnose": diagnose = true; i++; break;
                default: throw new ArgumentException($"unknown arg: {args[i]}");
            }
        }
        return new Options(libId, rid, forms, groups, diagnose);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        Options opt;
        try { opt = ParseArgs(args); }
        catch (ArgumentException ex) { Console.WriteLine(ex.Message); return 2; }
        catch (IndexOutOfRangeException) { Console.WriteLine("missing value for argument"); return 2; }

        var lib = AotProbeManifest.Libraries.FirstOrDefault(l => l.Id == opt.LibId);
        if (lib is null)
        {
            Console.WriteLine("usage: aotprobe run --lib <id> [--forms jit,aot] [--groups A,B] [--rid win-x64] [--diagnose]");
            Console.WriteLine("libraries: " + string.Join(", ", AotProbeManifest.Libraries.Select(l => l.Id + (l.AdapterReady ? "" : "(S1)"))));
            return 2;
        }
        if (!lib.AdapterReady)
        {
            Console.WriteLine($"library '{lib.Id}' has no adapter yet (S1 scope)");
            return 2;
        }

        string rid = opt.Rid ?? (OperatingSystem.IsWindows() ? "win-x64" : "linux-x64");
        string repoRoot = FindRepoRoot();

        var runs = new List<ArmRun>();
        await RunPlannedArmsAsync(repoRoot, lib, rid, opt, runs).ConfigureAwait(false);
        await WriteFactsAsync(repoRoot, rid, runs).ConfigureAwait(false);
        PrintSummary(runs);
        return 0;
    }

    private static async Task RunPlannedArmsAsync(
        string repoRoot, AotProbeManifest.Library lib, string rid, Options opt, List<ArmRun> runs)
    {
        foreach (string formName in opt.Forms.Where(f => f is "jit" or "aot"))
        {
            runs.Add(await RunArmAsync(repoRoot, lib, ProbeGroup.A, ParseForm(formName), rid).ConfigureAwait(false));
        }

        ArmRun? aAot = runs.FirstOrDefault(r => r is { Group: "A", Form: "aot" });
        bool aAotP0Pass = aAot is not null && ArmP0Pass(aAot);

        if (aAotP0Pass && opt.Groups.Contains("B"))
        {
            ArmRun bAot = await RunArmAsync(repoRoot, lib, ProbeGroup.B, ProbeForm.Aot, rid).ConfigureAwait(false);
            runs.Add(bAot);
            if (!ArmP0Pass(bAot))
                runs.Add(await RunArmAsync(repoRoot, lib, ProbeGroup.B, ProbeForm.Root, rid).ConfigureAwait(false));
        }
        else if (opt.Diagnose && aAot is not null && !aAotP0Pass && runs.All(r => r.Form != "trimjit"))
        {
            runs.Add(await RunArmAsync(repoRoot, lib, ProbeGroup.A, ProbeForm.TrimJit, rid).ConfigureAwait(false));
        }
    }

    private static void PrintSummary(List<ArmRun> runs)
    {
        Console.WriteLine();
        foreach (ArmRun arm in runs)
        {
            int pass = arm.Probes.Count(IsPass);
            Console.WriteLine($"{arm.Library}/{arm.Group}-{arm.Form}: binary={arm.BinaryReady} pass={pass}/{arm.Probes.Count} ilcWarnings={arm.Warnings.Count}");
            foreach (ProbeRunResult p in arm.Probes.Where(p => !IsPass(p)))
                Console.WriteLine($"  ✗ {p.Probe} status={p.Status} exit={p.ExitCode} {p.ErrorType}: {Truncate(p.ErrorMessage ?? "", 160)}");
        }
    }

    private static bool IsPass(ProbeRunResult p) => p.Status == "pass";

    /// <summary>臂级 P0 判定（报告/自检共用）：二进制就绪 + 至少一探针 + 全过。</summary>
    internal static bool ArmP0Pass(ArmRun arm) =>
        arm.BinaryReady && arm.Probes.Count > 0 && arm.Probes.All(IsPass);

    private static ProbeForm ParseForm(string name) => name switch
    {
        "jit" => ProbeForm.Jit,
        "aot" => ProbeForm.Aot,
        "trimjit" => ProbeForm.TrimJit,
        "root" => ProbeForm.Root,
        _ => throw new ArgumentException($"unknown form '{name}'"),
    };

    internal static async Task<ArmRun> RunArmAsync(
        string repoRoot, AotProbeManifest.Library lib, ProbeGroup group, ProbeForm form, string rid)
    {
        Console.WriteLine($"== {lib.Id}/{group}-{VariantBuilder.FormName(form)} ==");
        VariantBuilder.BuiltVariant variant = VariantBuilder.Create(repoRoot, lib, group, form, rid, $"{lib.Id}.cs");

        var sw = Stopwatch.StartNew();
        ProcResult build = await RunProcessAsync(
            "dotnet", BuildArguments(variant, form, rid), variant.Directory, BuildTimeout).ConfigureAwait(false);
        Console.WriteLine($"  build/publish exit={build.ExitCode} in {sw.Elapsed.TotalSeconds:F1}s");

        var warnings = ParseIlWarnings(build.Output, lib);
        if (!build.TimedOut && build.ExitCode == 0)
        {
            var probes = new List<ProbeRunResult>();
            ProbeId[] plan = group == ProbeGroup.A
                ? [.. ProbeCatalog.All.Where(p => ProbeCatalog.IsApplicable(p.Id, lib.Id)).Select(p => p.Id)]
                : ProbeCatalog.GetGroupBSet();
            foreach (ProbeId probe in plan)
            {
                probes.Add(await RunProbeAsync(variant, probe.ToString(), lib.Id, group, form).ConfigureAwait(false));
            }
            return new ArmRun(lib.Id, group.ToString(), VariantBuilder.FormName(form), BuildOk: true, null,
                PublishOk: true, null, warnings, probes);
        }

        // 失败分型：error CS* = 编译失败（R1），其余（ILC/native 编译期）= 发布失败（R2）
        bool compileFail = build.Output.Contains("error CS", StringComparison.Ordinal);
        string error = build.TimedOut ? "build/publish timeout" : Truncate(build.Output, 2000);
        return new ArmRun(lib.Id, group.ToString(), VariantBuilder.FormName(form), BuildOk: !compileFail, error,
            PublishOk: false, compileFail ? null : error, warnings, []);
    }

    private static string BuildArguments(VariantBuilder.BuiltVariant v, ProbeForm form, string rid) => form switch
    {
        // JIT 基线：普通 build，不 publish——变体 csproj 刻意不静态设 PublishAot（特性开关陷阱）
        ProbeForm.Jit => $"build \"{v.ProjectPath}\" -c Release",
        ProbeForm.Aot or ProbeForm.Root => $"publish \"{v.ProjectPath}\" -c Release -r {rid} --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:JsonSerializerIsReflectionEnabledByDefault=false -o \"{v.Directory}/publish\"",
        ProbeForm.TrimJit => $"publish \"{v.ProjectPath}\" -c Release -r {rid} --self-contained true -p:PublishTrimmed=true -o \"{v.Directory}/publish\"",
        _ => throw new UnreachableException(),
    };

    private static async Task<ProbeRunResult> RunProbeAsync(
        VariantBuilder.BuiltVariant variant, string probeName, string libId, ProbeGroup group, ProbeForm form)
    {
        string dbDir = Path.Combine(FindRepoRoot(), "tmp", "aotprobe", "db");
        Directory.CreateDirectory(dbDir);
        string dbPath = Path.Combine(dbDir, $"{libId}-{group}-{VariantBuilder.FormName(form)}-{probeName}-{Environment.ProcessId}.db3");
        DeleteDbTrio(dbPath);

        string fileName = variant.RunViaDotnet == "dotnet" ? "dotnet" : variant.BinaryPath;
        string arguments = variant.RunViaDotnet == "dotnet"
            ? $"\"{variant.BinaryPath}\" --probe {probeName} --db \"{dbPath}\""
            : $"--probe {probeName} --db \"{dbPath}\"";

        var sw = Stopwatch.StartNew();
        ProcResult r = await RunProcessAsync(fileName, arguments, variant.Directory, ProbeTimeout).ConfigureAwait(false);
        long elapsed = sw.ElapsedMilliseconds;

        ProbeObservation? obs = null;
        foreach (string line in r.Output.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                obs = JsonSerializer.Deserialize(trimmed[MarkerPrefix.Length..], ToolJsonContext.Default.ProbeObservation);
        }

        string status;
        string? errorType = null, errorMessage = null;
        List<AssertionFailure> assertions = [];
        if (r.TimedOut) status = "timeout";
        else if (obs is null) { status = "error"; errorType = "NoMarker"; errorMessage = $"exit={r.ExitCode}"; }
        else if (obs.Status == ProbeStatus.Error) { status = "error"; errorType = obs.ErrorType; errorMessage = obs.ErrorMessage; }
        else
        {
            assertions = Verify(probeName, obs);
            status = assertions.Count == 0 ? "pass" : "fail";
        }

        DeleteDbTrio(dbPath);
        return new ProbeRunResult(probeName, status, r.ExitCode, obs is not null, errorType, errorMessage,
            obs?.Fields ?? [], assertions, elapsed);
    }

    /// <summary>断言唯一入口：期望值全部来自 Fixtures，适配器观测值不参与期望构造（断言反转）。</summary>
    private static List<AssertionFailure> Verify(string probeName, ProbeObservation obs)
    {
        if (!Enum.TryParse<ProbeId>(probeName, out var probe) || !Fixtures.PerProbe.TryGetValue(probe, out var expected))
            return [new AssertionFailure("*", "no-fixture", "", "")];

        var failures = new List<AssertionFailure>();
        foreach (ExpectedField exp in expected)
        {
            ObsField? field = obs.Fields.FirstOrDefault(f => f.Key == exp.Key);
            if (field is null)
            {
                failures.Add(new(exp.Key, "missing-field", exp.Expected, ""));
                continue;
            }
            bool ok = exp.Kind switch
            {
                MatchKind.Exact => string.Equals(field.Value, exp.Expected, StringComparison.Ordinal),
                MatchKind.PositiveLong => long.TryParse(field.Value, CultureInfo.InvariantCulture, out long v) && v > 0,
                MatchKind.TypeSuffix => field.TypeName == exp.Expected
                    || field.TypeName.EndsWith("." + exp.Expected, StringComparison.Ordinal),
                _ => false,
            };
            if (!ok)
                failures.Add(new(exp.Key, exp.Kind.ToString(), exp.Expected, field.Value));
        }
        return failures;
    }

    private static async Task WriteFactsAsync(string repoRoot, string rid, List<ArmRun> runs)
    {
        ProcResult sdk = await RunProcessAsync("dotnet", "--version", repoRoot, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        string resultsDir = Path.Combine(repoRoot, "tools", "AotProbe", "results");
        Directory.CreateDirectory(resultsDir);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var facts = new FactsFile(
            FactsFile.CurrentSchema, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            GradingRules.RulesVersion, ProbeCatalog.CatalogVersion, AotProbeManifest.ManifestVersion, Fixtures.FixtureVersion,
            sdk.Output.Trim(), rid, System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            runs);
        string path = Path.Combine(resultsDir, $"aotprobe-{stamp}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(facts, ToolJsonContext.Default.FactsFile)).ConfigureAwait(false);
        Console.WriteLine($"facts -> {path}");
    }

    internal static async Task<ProcResult> RunProcessAsync(string fileName, string arguments, string workingDir, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start: {fileName} {arguments}");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        bool timedOut = false;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* 进程恰已退出：无需处理 */ }
        }
        string output = string.Concat(await stdoutTask.ConfigureAwait(false), "\n", await stderrTask.ConfigureAwait(false));
        return new ProcResult(process.ExitCode, output, timedOut);
    }

    private static List<IlWarning> ParseIlWarnings(string buildOutput, AotProbeManifest.Library lib)
    {
        var warnings = new List<IlWarning>();
        foreach (string rawLine in buildOutput.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            Match m = IlCodeRegex().Match(line);
            if (!m.Success)
                continue;
            Match origin = DllRegex().Match(line);
            string originName = origin.Success ? origin.Value : "";
            string attribution = "host";
            if (lib.Packages.Any(p => originName.StartsWith(p.Name, StringComparison.OrdinalIgnoreCase)))
                attribution = "lib";
            else if (originName.StartsWith("System.", StringComparison.Ordinal)
                || originName.StartsWith("Microsoft.", StringComparison.Ordinal)
                || originName.StartsWith("netstandard", StringComparison.Ordinal))
                attribution = "framework";
            warnings.Add(new IlWarning(m.Value, attribution, originName, Truncate(line, 400)));
        }
        return warnings;
    }

    [GeneratedRegex(@"\bIL[23]\d{3}\b")]
    private static partial Regex IlCodeRegex();

    [GeneratedRegex(@"[\w.]+\.dll")]
    private static partial Regex DllRegex();

    private static void DeleteDbTrio(string dbPath)
    {
        // 探针进程退出后句柄已释放，残留文件只影响磁盘整洁；失败不构成探针结果的一部分
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); }
            catch (IOException) { /* Windows 句柄延迟释放：忽略，下轮覆盖 */ }
            catch (UnauthorizedAccessException) { /* 同上 */ }
        }
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("PalORM.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    internal sealed record ProcResult(int ExitCode, string Output, bool TimedOut);
}
