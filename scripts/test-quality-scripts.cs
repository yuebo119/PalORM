// test-quality-scripts.cs（自 test-quality-scripts.sh 迁移，脚本 C# 化整改方案 T5-1；
// 语言门禁见本文件尾部"脚本语言政策"段，对应方案 T6-3）
// 质量脚本的固定回归夹具。验证成功与故障输入的退出码和计数。
// AI 系统脚本在 .ai/scripts/（本地工具，不入仓库）：存在时全量回归；
// 不存在时（fresh clone / CI）跳过 AI 段、仍回归仓库内防线
//（stub-check / SDK pin / secret-guard / 语言门禁四段）。
using System.Text;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM（对齐家族 bash 输出；行尾平台默认）
Console.OutputEncoding = new UTF8Encoding(false);

var repoRoot = FindRepoRoot();
Environment.CurrentDirectory = repoRoot;
var tmp = Path.Combine(Path.GetTempPath(), "quality-fixtures-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);
var fail = 0;

try
{
    // 2026-10-08 迁移后形态：AI 段被测对象已从 .ai/scripts/*.sh 迁为 scripts/*.cs（dotnet run）
    var aiScripts = Path.Combine(repoRoot, "scripts");
    var skipAi = !File.Exists(Path.Combine(aiScripts, "verify-action-items.cs"));
    if (skipAi)
    {
        Console.WriteLine("SKIP: scripts/verify-action-items.cs not found — AI 段跳过，仅回归仓库内防线");
    }

    if (!skipAi)
    {
        FixtureVerifyActionItems(aiScripts, tmp);
    }

    FixtureStubCheck(tmp);
    FixtureParamCollectionReuseGate(tmp);

    if (!skipAi)
    {
        FixtureReviewSnapshot(aiScripts, tmp);
        FixtureGateCheckG12(aiScripts, tmp);
        FixtureVerifyPhase(aiScripts, tmp);
    }

    FixtureSdkPin(repoRoot);
    FixtureNoPcreGrep(repoRoot);
    FixtureSecretGuardSelfTest();
    FixtureScriptLanguagePolicy(repoRoot);

    if (fail == 0)
    {
        Console.WriteLine("\n质量脚本回归夹具全部通过。");
    }
}
finally
{
    try
    {
        Directory.Delete(tmp, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // gate-check 夹具的 git 对象文件为只读，Windows 上递归删除会拒访问——临时目录留给系统清理
    }
}
return fail == 0 ? 0 : 1;

void Bail(string reason)
{
    Console.WriteLine(reason);
    fail = 1;
}

// ─── verify-action-items（AI 段，2026-10-08 C# 形态重写 + v2 语义对齐）───
// 夹具语义修正：原 v1 夹具断言"缺失标识符→FAIL"与 v2 收窄原则相反（v2 只核对文件路径，
// 标识符一律跳过——PalOrmDefinitelyMissingSymbol 类无扩展名无斜杠属跳过形态）；
// "缺失：1"（v1 文案）改对 v2 实际输出"缺失 1"。此两处漂移自 v2 重写日起未被发现（CI 盲区）。
void FixtureVerifyActionItems(string aiScripts, string tmpDir)
{
    Console.WriteLine("─── verify-action-items ───");
    var script = "scripts/verify-action-items.cs";
    File.WriteAllText(Path.Combine(tmpDir, "action-pass.md"), "# fixture\n`README.md`\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(tmpDir, "action-fail-file.md"), "# fixture\n`missing-file.yml`\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(tmpDir, "action-fail-symbol.md"), "# fixture\n`PalOrmDefinitelyMissingSymbol`\n", new UTF8Encoding(false));

    var passOk = RunDotnetFileCapture(script, [$"\"{Path.Combine(tmpDir, "action-pass.md")}\""], out var passLog);
    if (!passOk)
    {
        Bail($"FAIL 干净账本夹具未通过（{string.Join(' ', passLog.Split('\n').TakeLast(2))}）");
        return;
    }
    var failFileOk = RunDotnetFileCapture(script, [$"\"{Path.Combine(tmpDir, "action-fail-file.md")}\""], out var failFileLog);
    if (failFileOk)
    {
        Bail("FAIL 缺失文件未导致失败");
        return;
    }
    if (!failFileLog.Contains("缺失 1", StringComparison.Ordinal))
    {
        Bail("FAIL 缺失文件计数错误");
        return;
    }
    // v2 语义：标识符不是核对对象（多是"待创建"对象）——期望跳过而非失败
    var symbolOk = RunDotnetFileCapture(script, [$"\"{Path.Combine(tmpDir, "action-fail-symbol.md")}\""], out var symbolLog);
    if (!symbolOk || !symbolLog.Contains("跳过", StringComparison.Ordinal))
    {
        Bail("FAIL 标识符夹具应被 v2 跳过（收窄原则：只核对文件路径）");
        return;
    }
    Console.WriteLine("PASS verify-action-items");
}

// ─── stub-check（仓库内防线，C# 形态）───
void FixtureStubCheck(string tmpDir)
{
    Console.WriteLine("\n─── stub-check ───");
    var cleanDir = Path.Combine(tmpDir, "clean");
    var stubDir = Path.Combine(tmpDir, "stub");
    Directory.CreateDirectory(cleanDir);
    Directory.CreateDirectory(stubDir);
    File.WriteAllText(Path.Combine(cleanDir, "Complete.cs"), "internal sealed class Complete { int Value() { return 1; } }\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(stubDir, "Stub.cs"), "internal sealed class Stub { object Route() => this; }\n", new UTF8Encoding(false));

    _ = RunDotnetFileCapture("scripts/stub-check.cs", [cleanDir], out _);
    var stubOk = RunDotnetFileCapture("scripts/stub-check.cs", [stubDir], out var stubLog);
    if (stubOk)
    {
        Bail("FAIL 空壳夹具未导致失败");
        return;
    }
    if (!stubLog.Contains("发现 1 个", StringComparison.Ordinal))
    {
        Bail("FAIL 空壳计数错误");
        return;
    }
    Console.WriteLine("PASS stub-check");
}

// ─── gate-param-collection-reuse（参数集合复用纪律门禁）───
// 正反双路径：真实仓库当前全绿；去掉一处标记的故障夹具必须红（R-UNNESTB 的机械化拦截）
void FixtureParamCollectionReuseGate(string tmpDir)
{
    Console.WriteLine("\n─── gate-param-collection-reuse ───");
    // 正路径：真实仓库
    _ = RunDotnetFileCapture("scripts/gate-param-collection-reuse.cs", [repoRoot], out var okLog);
    if (!okLog.Contains("PASS", StringComparison.Ordinal))
    {
        Bail("FAIL 参数集合复用门禁在真实仓库上未通过");
        return;
    }

    // 反路径：故障夹具——复制 src/ 结构到 tmp，去掉一处标记后应报错并指位
    // （门禁按工作目录定位仓库根，故须切 cwd；脚本路径用绝对路径）
    var faultRoot = Path.Combine(tmpDir, "paramgate");
    var faultSrc = Path.Combine(faultRoot, "src", "Fault");
    Directory.CreateDirectory(faultSrc);
    File.WriteAllText(Path.Combine(faultRoot, "Directory.Build.props"), "<Project />\n", new UTF8Encoding(false));
    Directory.CreateDirectory(Path.Combine(faultSrc, "obj"));
    File.WriteAllText(Path.Combine(faultSrc, "Good.cs"),
        "class Good { void M(System.Data.Common.DbCommand c) { // PARAM-REUSE-OK[carrier] 载体命令\n c.Parameters.Clear(); } }\n",
        new UTF8Encoding(false));

    string gateScript = Path.Combine(repoRoot, "scripts", "gate-param-collection-reuse.cs");

    // 反路径①：完全缺标记
    File.WriteAllText(Path.Combine(faultSrc, "Bad.cs"),
        "class Bad { void M(System.Data.Common.DbCommand c) { c.Parameters.Clear(); } }\n",
        new UTF8Encoding(false));
    if (!RunGateExpectFailure(gateScript, faultRoot, "Bad.cs", "缺标记"))
    {
        return;
    }

    // 反路径②（2026-10-05 审计新增）：假 pool 断言——未点名池标识符必须被拦
    // （真实事故：局部事实为真、整体结论为假的标记骗过了纯声明制门禁）
    File.WriteAllText(Path.Combine(faultSrc, "Bad.cs"),
        "class Bad { void M(System.Data.Common.DbCommand c) { // PARAM-REUSE-OK[pool] 声称来自池\n c.Parameters.Clear(); } }\n",
        new UTF8Encoding(false));
    if (!RunGateExpectFailure(gateScript, faultRoot, "断言与事实矛盾", "假 pool（无标识符）"))
    {
        return;
    }

    // 反路径③：carrier 断言被证伪——同文件对该命令有执行调用
    File.WriteAllText(Path.Combine(faultSrc, "Bad.cs"),
        "class Bad { void M(System.Data.Common.DbCommand c) { // PARAM-REUSE-OK[carrier] 声称从不执行\n c.Parameters.Clear(); c.ExecuteNonQuery(); } }\n",
        new UTF8Encoding(false));
    if (!RunGateExpectFailure(gateScript, faultRoot, "断言与事实矛盾", "假 carrier（有执行调用）"))
    {
        return;
    }
    Console.WriteLine("PASS gate-param-collection-reuse");
}

// 门禁在故障夹具上必须非零退出且输出含指定关键字
bool RunGateExpectFailure(string gateScript, string faultRoot, string expectSubstring, string label)
{
    var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"run --file \"{gateScript}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = faultRoot,
    };
    using var gateProc = System.Diagnostics.Process.Start(psi)!;
    string log = gateProc.StandardOutput.ReadToEnd() + gateProc.StandardError.ReadToEnd();
    gateProc.WaitForExit();
    if (gateProc.ExitCode == 0 || !log.Contains(expectSubstring, StringComparison.Ordinal))
    {
        Bail($"FAIL 参数集合复用门禁未拦截：{label}");
        return false;
    }
    return true;
}

// ─── review-snapshot（AI 段，2026-10-08 C# 形态重写）───
void FixtureReviewSnapshot(string aiScripts, string tmpDir)
{
    Console.WriteLine("\n─── review-snapshot ───");
    _ = RunDotnetFileCapture("scripts/review-snapshot.cs", ["--no-build"], out var log);
    if (!log.Contains("构建状态", StringComparison.Ordinal) || !log.Contains("已跳过（--no-build）", StringComparison.Ordinal))
    {
        Bail("FAIL 快照无构建模式输出不完整");
        return;
    }
    if (log.Contains("测试文件：5835", StringComparison.Ordinal))
    {
        Bail("FAIL 快照仍统计生成产物");
        return;
    }
    Console.WriteLine("PASS review-snapshot");
}

// ─── gate-check G12 故障与恢复（AI 段，2026-10-08 C# 形态重写）───
// 三处形态修正：①gate-check.cs 以 PalORM.slnx 为仓库根哨兵——夹具根须放置空哨兵文件，
// 否则 FindRepoRoot 向上穿透到真实仓库根（语义污染）；②被测脚本用绝对路径（workingDir
// 已切夹具仓库）；③恢复段补 commit——原夹具 add -A 后 staged deletion 使 G33 必然 FAIL，
// 与"恢复后须通过"的断言矛盾（存量断裂根因之一，G33 加入后该段从未真绿）。
void FixtureGateCheckG12(string aiScripts, string tmpDir)
{
    Console.WriteLine("\n─── gate-check G12 ───");
    var gateScript = Path.Combine(repoRoot, "scripts", "gate-check.cs").Replace('\\', '/');
    var gateRoot = Path.Combine(tmpDir, "gate");
    var gateDir = Path.Combine(gateRoot, "src", "Fixture");
    Directory.CreateDirectory(gateDir);
    File.WriteAllText(Path.Combine(gateRoot, "PalORM.slnx"), "", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(gateDir, "Fixture.csproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><IsAotCompatible>true</IsAotCompatible></PropertyGroup></Project>\n",
        new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(gateDir, "Clean.cs"),
        "using System.Collections.Generic; public static class Clean { public static List<int> Values() => []; }\n",
        new UTF8Encoding(false));
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q")
    {
        WorkingDirectory = gateRoot,
        UseShellExecute = false,
    })!.WaitForExit();
    GitIn(gateRoot, "add .");
    // G33 工作树脏检查：临时仓库必须 commit 干净，否则 gate-check 的 G33 必然 FAIL（预存断裂根因）。
    var commitOut = GitIn(gateRoot, "-c user.name=fixture -c user.email=fixture@test.local commit -qm \"fixture init\"");
    var status = GitIn(gateRoot, "status --porcelain");
    if (status.Length > 0)
    {
        Bail($"FAIL G12 夹具仓库未清空（commit 输出=[{commitOut}] status=[{status}]）");
        return;
    }

    var gatePass = RunDotnetFileCapture(gateScript, [], out var gatePassLog, workingDir: gateRoot);
    if (!gatePass || !gatePassLog.Contains("PASS G12: 禁止公开 static 可写状态", StringComparison.Ordinal))
    {
        var head = string.Join(" | ", gatePassLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
        Bail($"FAIL G12 干净夹具未通过（gate-check 尾部输出：{head}）");
        return;
    }
    File.WriteAllText(Path.Combine(gateDir, "Broken.cs"),
        "public static class Broken { public static int Value\n{\n    get;\n    set;\n} }\n",
        new UTF8Encoding(false));
    GitIn(gateRoot, "add .");
    var brokenOk = RunDotnetFileCapture(gateScript, [], out var gateFailLog, workingDir: gateRoot);
    if (brokenOk || !gateFailLog.Contains("FAIL G12: 禁止公开 static 可写状态（违规数：1）", StringComparison.Ordinal))
    {
        Bail("FAIL G12 多行可写属性未导致失败或计数错误");
        return;
    }
    File.WriteAllText(Path.Combine(gateDir, "Broken.cs"),
        "using System.Collections.Generic; public static class Broken { public static List<int> Items { get; } = []; }\n",
        new UTF8Encoding(false));
    GitIn(gateRoot, "add .");
    var collectionOk = RunDotnetFileCapture(gateScript, [], out var gateCollectionLog, workingDir: gateRoot);
    if (collectionOk || !gateCollectionLog.Contains("FAIL G12: 禁止公开 static 可写状态（违规数：1）", StringComparison.Ordinal))
    {
        Bail("FAIL G12 可变集合属性未导致失败或计数错误");
        return;
    }
    File.Delete(Path.Combine(gateDir, "Broken.cs"));
    GitIn(gateRoot, "add -A");
    GitIn(gateRoot, "-c user.name=fixture -c user.email=fixture@test.local commit -qm \"remove broken\"");
    var recoveredOk = RunDotnetFileCapture(gateScript, [], out var gateRecoveredLog, workingDir: gateRoot);
    if (!recoveredOk || !gateRecoveredLog.Contains("PASS G12: 禁止公开 static 可写状态", StringComparison.Ordinal))
    {
        Bail("FAIL G12 移除违规后未恢复");
        return;
    }
    Console.WriteLine("PASS gate-check G12 故障与恢复");
}

// ─── verify-phase 参数失败传播（AI 段，2026-10-08 C# 形态重写）───
void FixtureVerifyPhase(string aiScripts, string tmpDir)
{
    Console.WriteLine("\n─── verify-phase ───");
    var ok = RunDotnetFileCapture("scripts/verify-phase.cs", ["invalid"], out var log);
    if (ok || !log.Contains("用法", StringComparison.Ordinal) || !log.Contains("phase-number", StringComparison.Ordinal))
    {
        Bail("FAIL 非法阶段参数失败传播或输出不完整");
        return;
    }
    Console.WriteLine("PASS verify-phase 参数失败传播");
}

// ─── SDK 固定与 CI 一致性 ───
void FixtureSdkPin(string root)
{
    Console.WriteLine("\n─── SDK pin ───");
    // rollForward: latestMinor 语义——global.json 是下限锚点，实跑 SDK 允许同 band 更高版本
    var pinMatch = Regex.Match(File.ReadAllText(Path.Combine(root, "global.json")), "\"version\"\\s*:\\s*\"(\\d+\\.\\d+\\.\\d+)");
    var sdkVersion = RunCapture("dotnet", "--version").Trim();
    var sdkMatch = Regex.Match(sdkVersion, "^(\\d+\\.\\d+\\.\\d+)");
    if (!pinMatch.Success || !sdkMatch.Success || pinMatch.Groups[1].Value != sdkMatch.Groups[1].Value)
    {
        Bail($"FAIL 当前 SDK band({sdkMatch.Groups[1].Value}) 与 global.json band({pinMatch.Groups[1].Value}) 不一致");
        return;
    }
    // ci.yml 已收敛为调用 verify.yml 的 14 行空壳（job 全部在 verify.yml）——SDK 断言改查 verify.yml（真源）
    var verifyYml = File.ReadAllText(Path.Combine(root, ".github", "workflows", "verify.yml"));
    if (verifyYml.Contains("dotnet-version: \"11.0.x\"", StringComparison.Ordinal))
    {
        Bail("FAIL CI 仍使用浮动 .NET SDK");
        return;
    }
    var setupCount = Regex.Matches(verifyYml, "uses: actions/setup-dotnet").Count;
    var globalJsonCount = Regex.Matches(verifyYml, "global-json-file: global.json").Count;
    if (setupCount != globalJsonCount)
    {
        Bail("FAIL CI setup-dotnet 未全部读取 global.json");
        return;
    }
    Console.WriteLine("PASS SDK 固定与 CI 一致性");
}

// ─── 脚本可移植性（无 PCRE grep 依赖；B87 三重防自指）───
void FixtureNoPcreGrep(string root)
{
    Console.WriteLine("\n─── 脚本可移植性（无 PCRE grep 依赖）───");
    // 模式要求 -oP 后跟空白（真实调用形态）；豁免注释行（注释提及历史写法是合法的，
    // 可移植性问题只在"实际调用"——三重防自指：说明文本、模式空格后缀、注释豁免）
    var pattern = new Regex("grep -oP\\s", RegexOptions.Compiled);
    List<string> hits = [];
    foreach (var dir in new[] { Path.Combine(root, "scripts"), Path.Combine(root, ".github", "workflows") })
    {
        if (!Directory.Exists(dir))
        {
            continue;
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var lines = SafeReadLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith('#'))
                {
                    hits.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}:{lines[i]}");
                }
            }
        }
    }
    if (hits.Count > 0)
    {
        Bail("FAIL 存在 grep -oP（PCRE 语法跨实现不兼容，换 POSIX grep+sed）:\n" + string.Join('\n', hits));
        return;
    }
    Console.WriteLine("PASS scripts/ 与 workflows/ 无 grep -oP（PCRE）依赖");
}

// ─── secret-guard 自测（B41：误报/真阳性双向向量回归）───
void FixtureSecretGuardSelfTest()
{
    Console.WriteLine("─── secret-guard 自测（B41：误报/真阳性双向向量回归）───");
    var ok = RunDotnetFileCapture("scripts/secret-guard.cs", ["--selftest"], out var log);
    if (!ok || !log.Contains("SELFTEST PASS", StringComparison.Ordinal))
    {
        Bail("FAIL secret-guard 自测未通过");
        return;
    }
    Console.WriteLine("PASS secret-guard 自测");
}

// ─── 脚本语言政策（D9 白名单制；方案 T6-3 机械门禁）───
// 仓内出现白名单外的 .sh/.mjs/.py/.rb/.pl 即 FAIL。白名单唯一条目：.githooks/pre-commit
//（git hook 机制要求可执行脚本入口）。新增白名单须在 docs/编码规范.md 登记理由并经用户同意。
void FixtureScriptLanguagePolicy(string root)
{
    Console.WriteLine("\n─── 脚本语言政策（白名单外非 C# 脚本）───");
    HashSet<string> whitelist = [".githooks/pre-commit"];
    HashSet<string> extensions = [".sh", ".mjs", ".py", ".rb", ".pl"];
    List<string> violations = [];
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal)
            || rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)
            || rel.StartsWith(".git/", StringComparison.Ordinal) || rel.StartsWith("node_modules/", StringComparison.Ordinal)
            || rel.StartsWith("bench/BenchmarkDotNet/", StringComparison.Ordinal) || rel.StartsWith("packages/", StringComparison.Ordinal)
            || rel.StartsWith("nupkgs/", StringComparison.Ordinal) || rel.StartsWith("artifacts/", StringComparison.Ordinal)
            || rel.StartsWith("data/", StringComparison.Ordinal) || rel.StartsWith(".ai/", StringComparison.Ordinal)
            || rel.StartsWith("docs/", StringComparison.Ordinal) || rel.StartsWith("BenchmarkDotNet.Artifacts/", StringComparison.Ordinal)
            || rel.StartsWith("bench/reports/", StringComparison.Ordinal)
            || rel.StartsWith(".playwright-mcp/", StringComparison.Ordinal) || rel.StartsWith("StrykerOutput/", StringComparison.Ordinal))
        {
            continue; // 依赖树/vendored 子树/本地工具(.ai 不入仓库)/历史文档不在政策范围
        }
        if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && !whitelist.Contains(rel, StringComparer.OrdinalIgnoreCase))
        {
            violations.Add(rel);
        }
    }
    if (violations.Count > 0)
    {
        Bail("FAIL 白名单外非 C# 脚本（政策：脚本一律 C#，豁免须用户同意并登记 docs/编码规范.md）:\n        "
            + string.Join("\n        ", violations));
        return;
    }
    Console.WriteLine("PASS 脚本语言政策（白名单外零非 C# 脚本）");
}

// ─── 进程助手 ───
static string[] SafeReadLines(string file)
{
    try
    {
        return File.ReadAllLines(file);
    }
    catch (IOException)
    {
        return [];
    }
}

bool RunDotnetFileCapture(string scriptFile, string[] args, out string output, string? workingDir = null)
{
    var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"run --file {scriptFile} {(args.Length > 0 ? "-- " + string.Join(' ', args) : "")}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8,
    };
    if (workingDir is not null)
    {
        psi.WorkingDirectory = workingDir;
    }
    using var p = System.Diagnostics.Process.Start(psi)!;
    var outTask = p.StandardOutput.ReadToEndAsync();
    var errTask = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    output = outTask.Result + errTask.Result;
    return p.ExitCode == 0;
}

static string RunCapture(string command, string arguments)
{
    var psi = new System.Diagnostics.ProcessStartInfo(command, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var outTask = p.StandardOutput.ReadToEndAsync();
    var errTask = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (outTask.Result + errTask.Result).Trim();
}

static string GitIn(string workingDir, string arguments)
{
    var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
    {
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var outTask = p.StandardOutput.ReadToEndAsync();
    var errTask = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (outTask.Result + errTask.Result).Trim();
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    Console.Error.WriteLine("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}
