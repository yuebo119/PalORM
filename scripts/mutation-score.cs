// mutation-score.cs（测试系统全面优化方案 2026-11-04 · MUT-6）
// 判别力分数：把 conformance-mutation-replay.cs 的报告汇总为单行可比较分数。
//
// 为什么需要这一层：回放本身只回答"每条规则是否红过又绿过"，回答不了"整体判别力是多少、
// 这轮比上轮退了没有"。分数把三件事压成一行：规则通过率、契约覆盖数、耗时。
//
// 口径（写死在这里，避免每轮口径漂移）：
//   score    = round(100 × passed / total)          —— skipped 计入分母（失配规则不能算通过）
//   coverage = distinct(通过规则声明的期望契约) / 账本契约总数（能力 + 方言两个目录）
//   duration = 回放报告里的实际耗时
// 阈值默认 60（方案 §9 里程碑 M1）。--previous 传入上一轮报告时，分数不得低于上一轮。
//
// 用法：dotnet run --file scripts/mutation-score.cs -- --report <mutation-report.json>
//                                                      [--previous <prev-report.json>] [--min 60]
// 退出码：0 达标 / 1 低于阈值或较上轮下降 / 2 用法或输入错误。
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

string? reportPath = null;
string? previousPath = null;
int minScore = 60;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--report" when i + 1 < args.Length:
            reportPath = args[++i];
            break;
        case "--previous" when i + 1 < args.Length:
            previousPath = args[++i];
            break;
        case "--min" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out minScore))
            {
                Console.Error.WriteLine("::error::--min 需要整数");
                return 2;
            }
            break;
        default:
            Console.Error.WriteLine($"未知参数：{args[i]}（可用：--report <json> / --previous <json> / --min <n>）");
            return 2;
    }
}

if (reportPath is null)
{
    Console.Error.WriteLine("usage: mutation-score.cs --report <mutation-report.json> [--previous <prev.json>] [--min 60]");
    return 2;
}

string repoRoot = FindRepoRoot();
string resolved = Path.IsPathRooted(reportPath) ? reportPath : Path.Combine(repoRoot, reportPath);
if (!File.Exists(resolved))
{
    Console.Error.WriteLine($"::error::回放报告不存在：{resolved}（先跑 conformance-mutation-replay.cs -- --report）");
    return 2;
}

int totalContracts = CountContracts(repoRoot);
JsonNode report = JsonNode.Parse(File.ReadAllText(resolved))!;
int total = report["total"]!.GetValue<int>();
int passed = report["passed"]!.GetValue<int>();
int failed = report["failed"]!.GetValue<int>();
int skipped = report["skipped"]!.GetValue<int>();
double duration = report["durationSeconds"]!.GetValue<double>();
int covered = report["contractsCovered"] is JsonArray coveredArray ? coveredArray.Count : 0;

if (total == 0)
{
    Console.Error.WriteLine("::error::报告里没有规则记录（total=0）——空报告不得算作满分");
    return 1;
}

int score = (int)Math.Round(100.0 * passed / total, MidpointRounding.AwayFromZero);
string line = $"score={score.ToString(CultureInfo.InvariantCulture)} "
    + $"coverage={covered.ToString(CultureInfo.InvariantCulture)}/{totalContracts.ToString(CultureInfo.InvariantCulture)} "
    + $"rules={passed.ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)} "
    + $"(failed {failed.ToString(CultureInfo.InvariantCulture)}, skipped {skipped.ToString(CultureInfo.InvariantCulture)}) "
    + $"duration={duration.ToString("0.0", CultureInfo.InvariantCulture)}s";
Console.WriteLine(line);

List<string> problems = [];
if (score < minScore)
{
    problems.Add($"判别力分数 {score} < 阈值 {minScore}");
}
if (previousPath is not null)
{
    string prevResolved = Path.IsPathRooted(previousPath) ? previousPath : Path.Combine(repoRoot, previousPath);
    if (File.Exists(prevResolved))
    {
        JsonNode previous = JsonNode.Parse(File.ReadAllText(prevResolved))!;
        int prevTotal = previous["total"]!.GetValue<int>();
        int prevPassed = previous["passed"]!.GetValue<int>();
        if (prevTotal > 0)
        {
            int prevScore = (int)Math.Round(100.0 * prevPassed / prevTotal, MidpointRounding.AwayFromZero);
            Console.WriteLine($"上一轮：score={prevScore.ToString(CultureInfo.InvariantCulture)} "
                + $"rules={prevPassed.ToString(CultureInfo.InvariantCulture)}/{prevTotal.ToString(CultureInfo.InvariantCulture)}");
            if (score < prevScore)
            {
                problems.Add($"分数较上轮下降（{score} < {prevScore}）——规则通过率回退需先解释");
            }
        }
    }
    else
    {
        Console.WriteLine("上一轮报告不存在（首轮或产物过期），跳过对比");
    }
}

if (report["results"] is JsonArray results)
{
    foreach (JsonNode? result in results)
    {
        if (result?["verdict"]?.GetValue<string>() != "PASS")
        {
            string id = result?["id"]?.GetValue<string>() ?? "?";
            string contracts = result?["expectedContracts"] is JsonArray ids && ids.Count > 0
                ? string.Join(",", ids.Select(x => x!.GetValue<string>()))
                : "—";
            Console.Error.WriteLine($"::error::未通过规则 {id}（期望契约 {contracts}）");
        }
    }
}

if (problems.Count > 0)
{
    foreach (string problem in problems) Console.Error.WriteLine($"::error::{problem}");
    return 1;
}
Console.WriteLine("判别力分数达标");
return 0;

static int CountContracts(string repoRoot)
{
    string path = Path.Combine(repoRoot, "bench", "baselines", "conformance-contracts.json");
    if (!File.Exists(path)) return 0;
    JsonNode ledger = JsonNode.Parse(File.ReadAllText(path))!;
    int total = 0;
    foreach (string key in new[] { "capabilityCatalog", "dialectCatalog" })
    {
        total += ledger[key]?["count"]?.GetValue<int>() ?? 0;
    }
    return total;
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
