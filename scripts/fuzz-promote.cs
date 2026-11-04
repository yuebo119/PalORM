// fuzz-promote.cs（测试系统全面优化方案 2026-11-04 · FUZ-6）
// 失败固化流程：把 fuzz 语料（artifacts/fuzz-corpus/*.txt）或失败样本
// （artifacts/fuzz-failures-*.txt）变成一段可粘贴的 [Test] 草稿，人工确认后并入固定集。
//
// 为什么不直接改测试文件：语料里的种子集可能包含数百个种子，直接灌进 FixedSeeds 会让
// 「固定集」失去意义（固定集的作用是快速回归，不是存档）。本脚本只做机械转换：
// 解析 → 生成草稿 → 人工判断该留多少、放哪个类。
//
// 语料行格式（RandomScenario.DScenario.ReplayKey）：
//   seed|seed-N/case-M|v1,v2,...|Condition|BatchSize|Sequence
// 失败行格式（RandomDiffTests 落盘）：
//   seed=N original=<ReplayKey> shrunk=<ReplayKey>
//
// 用法：
//   dotnet run --file scripts/fuzz-promote.cs -- --corpus artifacts/fuzz-corpus/seed-7.txt
//   dotnet run --file scripts/fuzz-promote.cs -- --failure artifacts/fuzz-failures-7-*.txt
//   dotnet run --file scripts/fuzz-promote.cs -- --corpus <file> --out PromotedFuzzTests.cs.draft
// 退出码：0 成功 / 1 无可用条目 / 2 用法或输入错。
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

string? corpusPath = null;
string? failurePath = null;
string? outPath = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--corpus" when i + 1 < args.Length:
            corpusPath = args[++i];
            break;
        case "--failure" when i + 1 < args.Length:
            failurePath = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;
        default:
            Console.Error.WriteLine($"未知参数：{args[i]}");
            return 2;
    }
}

if (corpusPath is null && failurePath is null)
{
    Console.Error.WriteLine("usage: fuzz-promote.cs (--corpus <file> | --failure <file>) [--out <draft>]");
    return 2;
}

var seeds = new SortedSet<int>();
var cases = new List<string>();
int parsed = 0;

foreach ((string? path, string kind) in new[] { (corpusPath, "corpus"), (failurePath, "failure") })
{
    if (path is null) continue;
    string[] matches = [.. Directory.GetFiles(Path.GetDirectoryName(path) ?? ".", Path.GetFileName(path))];
    if (matches.Length == 0)
    {
        matches = [path];
    }
    foreach (string file in matches.OrderBy(f => f, StringComparer.Ordinal))
    {
        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (kind == "corpus")
            {
                // seed|id|values|condition|batchSize|sequence
                string[] parts = line.Split('|');
                if (parts.Length < 6 || !int.TryParse(parts[0], out int seed)) continue;
                seeds.Add(seed);
                cases.Add($"{seed}:{parts[1]}");
                parsed++;
            }
            else
            {
                // seed=N original=<replaykey> shrunk=<replaykey>
                Match seedMatch = Regex.Match(line, @"seed=(\d+)");
                if (!seedMatch.Success) continue;
                seeds.Add(int.Parse(seedMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                parsed++;
            }
        }
    }
}

if (parsed == 0)
{
    Console.Error.WriteLine("::error::语料/失败文件里没有可解析条目（检查格式：corpus 为 seed|id|values|cond|batch|seq，failure 为 seed=N original=... shrunk=...）");
    return 1;
}

var draft = new StringBuilder();
draft.AppendLine("// 由 scripts/fuzz-promote.cs 生成（人工确认后并入固定集；勿直接粘贴未复核的种子集）");
draft.AppendLine($"// 来源：{(corpusPath is not null ? corpusPath : failurePath)}；解析 {parsed} 条，去重种子 {seeds.Count} 个");
draft.AppendLine("// 种子集：" + string.Join(", ", seeds));
draft.AppendLine();
draft.AppendLine("private static readonly int[] PromotedSeeds = [" + string.Join(", ", seeds) + "];");
draft.AppendLine();
draft.AppendLine("[Test]");
draft.AppendLine("public async Task PromotedCorpus_ProduceNoDifferences()");
draft.AppendLine("{");
draft.AppendLine("    (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync(\"fuzz_promoted\");");
draft.AppendLine("    await using SharedSqliteDb scope = shared;");
draft.AppendLine("    await using DataSession<SqliteProvider> session = db;");
draft.AppendLine();
draft.AppendLine("    List<string> differences = [];");
draft.AppendLine("    foreach (int seed in PromotedSeeds)");
draft.AppendLine("    {");
draft.AppendLine("        foreach (DScenario scenario in DGenerator.Generate(seed, 50))");
draft.AppendLine("        {");
draft.AppendLine("            if (await RandomDiffRunner.HasDifferenceAsync(scenario, session, raw, SqlDialect.Sqlite))");
draft.AppendLine("                differences.Add(scenario.ReplayKey);");
draft.AppendLine("        }");
draft.AppendLine("    }");
draft.AppendLine("    await Assert.That(string.Join(Environment.NewLine, differences.Take(10))).IsEmpty();");
draft.AppendLine("}");

string text = draft.ToString();
if (outPath is not null)
{
    File.WriteAllText(outPath, text, new UTF8Encoding(false));
    Console.WriteLine($"草稿已写入：{outPath}");
}
else
{
    Console.WriteLine(text);
}
Console.WriteLine($"解析 {parsed} 条，去重种子 {seeds.Count} 个（案例 {cases.Count}）");
return 0;

