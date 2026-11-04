// assert-coverage-matrix.cs（测试系统全面优化方案 2026-11-04 · COV-2）
// 覆盖率矩阵校验：把「仓里有哪些模块、各自由哪个测试通道采集、地板值从哪来」变成可失败的检查。
//
// 为什么需要它：覆盖率地板散落在 test-counts.json 的 coverage 键里，谁在用哪个键、哪个模块
// 还没地板，只有靠人记。新增一个 Provider 项目而忘配地板时，没有任何门禁会响（分母少了
// 一整个模块，全量地板照样绿）。本脚本把「未登记模块」变成硬失败。
//
// 判据（全部可执行）：
//   ① src/PalORM.* 下每个模块必须在矩阵里有一条登记（assembly 名对齐目录名）
//   ② 矩阵里不得留下不存在的模块（删了项目没删登记）
//   ③ floorRef 必须解析到 test-counts.json coverage 段的数值键；空 floorRef 必须带 pending 理由
//   ④ coverage 段的每个数值键必须被某条登记的 floorRef 引用（无孤儿地板）
//   ⑤ 同一 floorRef 不得被两条登记共用（一个地板只守一个模块）
// pending 记 WARN（列出，不静默）；其余违例 FAIL。退出码 0=通过 / 1=有 FAIL / 2=用法或输入错。
//
// --self-check：内嵌好/坏夹具自证（不读外部文件），供 V14 存活监测面使用。
using System.Text;
using System.Text.Json.Nodes;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length == 1 && args[0] == "--self-check")
{
    return SelfCheck();
}
if (args.Length > 0)
{
    Console.Error.WriteLine($"未知参数：{args[0]}（可用：--self-check）");
    return 2;
}

string repoRoot = FindRepoRoot();
string matrixPath = Path.Combine(repoRoot, "bench", "baselines", "coverage-matrix.json");
string countsPath = Path.Combine(repoRoot, "bench", "baselines", "test-counts.json");
if (!File.Exists(matrixPath) || !File.Exists(countsPath))
{
    Console.Error.WriteLine($"::error::输入缺失：{matrixPath} 或 {countsPath}");
    return 2;
}

JsonNode matrix = JsonNode.Parse(File.ReadAllText(matrixPath))!;
JsonNode counts = JsonNode.Parse(File.ReadAllText(countsPath))!;
(List<string> failures, List<string> warnings, int registered) =
    Evaluate(matrix, counts, DiscoverModules(repoRoot));

Console.WriteLine($"覆盖率矩阵：{registered} 个模块登记（工作区发现 {DiscoverModules(repoRoot).Count} 个 src 模块）");
foreach (string warning in warnings) Console.WriteLine($"  WARN {warning}");
if (failures.Count == 0)
{
    Console.WriteLine("覆盖率矩阵一致");
    return 0;
}
foreach (string failure in failures) Console.Error.WriteLine($"::error::{failure}");
return 1;

// ── 判定（自检与正式路径共用）──
static (List<string> Failures, List<string> Warnings, int Registered) Evaluate(
    JsonNode matrix, JsonNode counts, IReadOnlyList<string> assembliesOnDisk)
{
    List<string> failures = [];
    List<string> warnings = [];

    JsonNode? channels = matrix["channels"];
    JsonArray? modules = matrix["modules"] as JsonArray;
    if (modules is null)
    {
        failures.Add("矩阵缺 modules 数组");
        return (failures, warnings, 0);
    }

    JsonNode? coverage = counts["coverage"];
    var referencedFloors = new Dictionary<string, string>(StringComparer.Ordinal);
    var registeredAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (JsonNode? module in modules)
    {
        if (module is null) continue;
        string key = module["key"]?.GetValue<string>() ?? "";
        string assembly = module["assembly"]?.GetValue<string>() ?? "";
        string channel = module["channel"]?.GetValue<string>() ?? "";
        string floorRef = module["floorRef"]?.GetValue<string>() ?? "";
        string pending = module["pending"]?.GetValue<string>() ?? "";

        if (key.Length == 0 || assembly.Length == 0)
        {
            failures.Add("登记条目缺 key 或 assembly");
            continue;
        }
        registeredAssemblies.Add(assembly);

        if (!string.IsNullOrEmpty(channel) && channels?[channel] is null)
        {
            failures.Add($"{key}: 通道 '{channel}' 未在 channels 里定义");
        }
        if (!assembliesOnDisk.Contains(assembly, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"{key}: 登记的模块 {assembly} 在 src/ 下不存在（删了项目没删登记）");
        }

        if (floorRef.Length == 0)
        {
            if (pending.Length == 0)
            {
                failures.Add($"{key}: 既没有 floorRef 也没有 pending 理由（地板缺失必须显式说明）");
            }
            else
            {
                warnings.Add($"{key}: 尚无覆盖率地板（{pending}）");
            }
            continue;
        }

        // floorRef 形如 coverage.core
        string[] parts = floorRef.Split('.');
        JsonNode? floorNode = parts.Length == 2 ? coverage?[parts[1]] : null;
        if (floorNode is JsonValue value && value.TryGetValue<double>(out double floor))
        {
            Console.WriteLine($"  {key} ({assembly}) → {floorRef} = {floor}");
        }
        else
        {
            failures.Add($"{key}: floorRef '{floorRef}' 在 test-counts.json 里解析不到数值地板");
        }
        if (referencedFloors.TryGetValue(floorRef, out string? other))
        {
            failures.Add($"{key}: floorRef '{floorRef}' 已被 {other} 引用（一个地板只能守一个模块）");
        }
        else
        {
            referencedFloors[floorRef] = key;
        }
    }

    // 未登记模块：分母少了整个模块时全量地板照样绿，所以这条必须是硬失败
    foreach (string assembly in assembliesOnDisk)
    {
        if (!registeredAssemblies.Contains(assembly))
        {
            failures.Add($"{assembly}: src/ 下的模块未在覆盖率矩阵登记（先定通道与地板再合入）");
        }
    }

    // 孤儿地板：coverage 段有数值键却没有任何登记引用
    if (coverage is JsonObject coverageObject)
    {
        foreach ((string name, JsonNode? node) in coverageObject)
        {
            if (name.StartsWith('_')) continue;
            if (node is not JsonValue value || !value.TryGetValue<double>(out _)) continue;
            if (!referencedFloors.ContainsKey("coverage." + name))
            {
                failures.Add($"coverage.{name}: 有地板值但没有模块登记引用它（孤儿地板，删或补登记）");
            }
        }
    }

    return (failures, warnings, modules.Count);
}

// ── 自检夹具 ──
static int SelfCheck()
{
    var goodMatrix = Parse("""
        {"channels":{"unit":{"xml":"cov-core.xml"}},
         "modules":[{"key":"core","assembly":"PalORM.Core","channel":"unit","floorRef":"coverage.core"}]}
        """);
    var goodCounts = Parse("""{"coverage":{"core":74}}""");
    string[] coreOnly = ["PalORM.Core"];

    var missingFloor = Parse("""
        {"channels":{"unit":{}},"modules":[{"key":"core","assembly":"PalORM.Core","channel":"unit","floorRef":"coverage.core"}]}
        """);
    var emptyCounts = Parse("""{"coverage":{}}""");

    var pendingMatrix = Parse("""
        {"channels":{"unit":{}},"modules":[{"key":"testing","assembly":"PalORM.Testing","channel":"unit","floorRef":"","pending":"辅助库暂无口径"}]}
        """);
    string[] testingOnly = ["PalORM.Testing"];

    var duplicateRef = Parse("""
        {"channels":{"unit":{}},"modules":[
          {"key":"core","assembly":"PalORM.Core","channel":"unit","floorRef":"coverage.core"},
          {"key":"testing","assembly":"PalORM.Testing","channel":"unit","floorRef":"coverage.core"}]}
        """);
    string[] coreAndTesting = ["PalORM.Core", "PalORM.Testing"];

    var cases = new List<(string Name, int Expected, int Actual)>
    {
        ("好夹具（一个模块、地板解析成功）", 0, Judge(goodMatrix, goodCounts, coreOnly)),
        ("坏夹具①（src 多一个模块未登记）", 1, Judge(goodMatrix, goodCounts, ["PalORM.Core", "PalORM.MySql"])),
        ("坏夹具②（登记了不存在的模块）", 1, Judge(goodMatrix, goodCounts, ["PalORM.Testing"])),
        ("坏夹具③（floorRef 解析不到）", 1, Judge(missingFloor, emptyCounts, coreOnly)),
        ("坏夹具④（两个模块共用同一地板）", 1, Judge(duplicateRef, goodCounts, coreAndTesting)),
        ("好夹具②（pending 理由记为 WARN 不拦）", 0, Judge(pendingMatrix, emptyCounts, testingOnly)),
    };

    bool ok = true;
    foreach ((string name, int expected, int actual) in cases)
    {
        bool behaved = expected == actual;
        ok &= behaved;
        Console.WriteLine($"  {(behaved ? "PASS" : "FAIL")} {name}：期望 exit {expected}，实际 exit {actual}");
    }
    if (ok)
    {
        Console.WriteLine("assert-coverage-matrix self-check: PASS（好夹具 exit 0 / 坏夹具如期 exit 1）");
        return 0;
    }
    Console.Error.WriteLine("::error::assert-coverage-matrix self-check: FAIL（判据行为与夹具期望不符）");
    return 1;
}

static int Judge(JsonNode matrix, JsonNode counts, IReadOnlyList<string> assemblies)
{
    (List<string> failures, _, _) = Evaluate(matrix, counts, assemblies);
    return failures.Count == 0 ? 0 : 1;
}

static JsonNode Parse(string json) => JsonNode.Parse(json)!;

static List<string> DiscoverModules(string repoRoot)
{
    string srcDir = Path.Combine(repoRoot, "src");
    if (!Directory.Exists(srcDir)) return [];
    return [.. Directory.GetDirectories(srcDir)
        .Select(Path.GetFileName)
        .Where(name => name is not null && name.StartsWith("PalORM.", StringComparison.Ordinal))
        .Select(name => name!)
        .OrderBy(name => name, StringComparer.Ordinal)];
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    Console.Error.WriteLine("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}
