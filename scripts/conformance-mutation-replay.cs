// conformance-mutation-replay.cs
// 一致性套件的产品侧变异回放（方案 §4.8 / 附录 B 第 6 项；D1-D7 回放见《测试体系增强方案-2026-10-30》§5.1）。
//
// 对每条规则执行四步：注入缺陷 -> 构建 -> 跑目标测试（期望报红）-> git 还原 -> 构建 -> 跑同一组测试（期望转绿）。
// 判定标准是"注入后红 且 还原后绿"，任一不符即 exit 1。注入内容均为硬替换，不用环境变量开关：
// 源生成器读取环境变量会被 Roslyn RS1035 拦下（分析器禁止读环境变量）。
//
// 规则可指定自己的测试项目（R-D5 的 PALORM054 检测测试在 test/PalORM.SourceGen.Tests）；
// 未指定时用默认项目 test/PalORM.Integration.Tests。多行片段按工作树实际行尾适配（仓库索引 LF，工作树受
// core.autocrlf 影响可能是 CRLF）。
//
// R-C 系列（方案《测试机制重设计方案-2026-11-03》§6.2 降级路径）：变异分（Stryker）自 2026-10-03 起
// 产出 0 个有效变异，"整体判别力"无基线可比；退一步只证明「契约这一族有判别力」——往产品代码注入
// 已知缺陷，要求指定契约 ID 的用例报红。期望契约由 Rule.ExpectedContracts 声明：报红名单里没有它，
// 即使退出码非零也判 FAIL（别的原因红了不构成本契约的判别力证据）。
//
// 前置条件：工作树必须清洁（脚本会用 git checkout 还原被改文件，脏树会丢改动）。
// 用法：dotnet run --file scripts/conformance-mutation-replay.cs [-- --rule <ID 前缀>]
//   --rule R-C 只跑契约族规则（迭代时省去历史规则的重建）；省略则跑全部。
// 输出末尾带 Markdown 台账（规则 / 注入点 / 报红测试数 / 报红测试名），供总报告登记。
using System.Diagnostics;
using System.Text.RegularExpressions;

const string DefaultTestProject = "test/PalORM.Integration.Tests";

string repoRoot = FindRepoRoot();
string dotnet = ResolveDotnet();

(List<(string Commit, string Subject)> Head, string Dirty) = (GitLog(repoRoot), GitStatus(repoRoot));
if (Dirty.Length > 0)
{
    Console.Error.WriteLine("工作树不清洁，变异回放会丢失改动。先提交或 stash 再执行：");
    Console.Error.WriteLine(Dirty);
    return 2;
}

Console.WriteLine($"仓库根: {repoRoot}");
Console.WriteLine($"dotnet: {dotnet}");
Console.WriteLine($"基线提交: {(Head.Count > 0 ? Head[0].Commit[..8] + " " + Head[0].Subject : "(none)")}");
Console.WriteLine();

// --rule <ID 前缀>：只跑指定前缀的规则（迭代契约族规则时不必重跑历史规则）
string? rulePrefix = null;
for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--rule") rulePrefix = args[i + 1];
}

// D1-D7 七条规则对应已修复的历史产品缺陷（《测试体系增强方案-2026-10-30》§4.2）：每条有真实缺陷背书，
// 注入点一行可换、可观测量（哪个期望值会变）在 Note 里写明。
var rules = new Rule[]
{
    new("R1a", "src/PalORM.Core/DataSession.Crud.cs",
        "if (!ignoreFilters && (features & EntityFeatures.SoftDelete) != 0)",
        "if ((features & EntityFeatures.SoftDelete) != 0)",
        "/*/*/SoftDeletePartitionTests/*",
        "From<T> 的软删过滤不再响应 IgnoreFilters"),
    new("R1b", "src/PalORM.Core/DataSession.cs",
        "bool hasSoftDelete = !_ignoreFilters && (features & EntityFeatures.SoftDelete) != 0;",
        "bool hasSoftDelete = (features & EntityFeatures.SoftDelete) != 0;",
        "/*/*/SoftDeletePartitionTests/*",
        "GetAll/Count 的第二注入路径同样失效"),
    new("R2", "src/PalORM.SourceGen/RowFactoryEmitter.cs",
        "string readCall = GetReadExpression(col, ordinal, model.GeneratedTypeSuffix);",
        "string readCall = GetReadExpression(col, ordinal < 2 ? 1 - ordinal : ordinal, model.GeneratedTypeSuffix);",
        "/*/*/DualSideComparisonTests/*",
        "物化列序前两位互换（RowFactory 序号漂移）"),
    new("R4", "src/PalORM.Core/QueryBuilder.cs",
        "return hasOr && (start > 0 || end < clauses.Count);",
        "return hasOr && (start > 0 || end < clauses.Count) && start < 0;",
        "/*/*/IncidentFilterPrecedenceTests/*",
        "AND 组补括号回退（C16 同源）"),

    // ── D1-D7 历史缺陷回放（2026-10-30 补齐）──────────────────────────────
    new("R-D1", "src/PalORM.SourceGen/MigrationEmitter.cs",
        "SqlGenerationDialect.PostgreSql => \"TIMESTAMPTZ\",",
        "SqlGenerationDialect.PostgreSql => \"TIMESTAMP\",",
        "/*/*/TimeKindMatrixTests/PostgreSql_DateTimeRoundTrip_ReadThenWriteBackSucceeds",
        "D1 PG 时间列型 TIMESTAMPTZ→TIMESTAMP（两处映射同时命中）；"
        + "可观测量：DateTime 读回 Kind=Unspecified，写回抛 ArgumentException"),
    new("R-D2", "src/PalORM.Core/DataSession_Bulk.cs",
        "BackfillGeneratedId(backfill!, entities[start + i], returnedIds[i]);",
        "BackfillGeneratedId(backfill!, entities[start + i], returnedIds[0]);",
        "/*/*/BulkInsertReturningTests/*",
        "D2 批量回填把首值写给所有行；可观测量：实体 Id 与库内 Id 逐行比对失败"),
    new("R-D3", "src/PalORM.Core/QueryBuilder.cs",
        "if (_dialect != SqlDialect.Sqlite) return;",
        "if (_dialect == SqlDialect.Sqlite) return;",
        "/*/*/BoundaryContractTests/*",
        "D3 SQLite 行锁放行（条件反号）；可观测量：ForUpdate/ForShare 不再抛 NotSupportedException"),
    // 2026-11-03 重锚：产品把 `catch (Exception ex) when (...)` 重构为
    // `NormalizeOnce(ex, ...) is { } mapped` 的单一真源形态（D4/D7 共用）。
    // 注入点随之迁移到 NormalizeOnce 内的内层超时解包分支——删掉它，D4 语义失效而
    // D7 的耗时判定分支保留（与原规则意图逐位一致）。
    new("R-D4", "src/PalORM.Core/CommandTimeoutNormalizer.cs",
        "        if (ex.InnerException is TimeoutException timeout)\n"
        + "            return new TimeoutException(timeout.Message, ex);\n",
        "",
        "/*/*/ErrorContractTests/*",
        "D4 超时不解包（删 NormalizeOnce 的内层 TimeoutException 解包，保留 D7 耗时判定）；"
        + "可观测量：PG 命令超时异常类型不再是 TimeoutException"),
    // 2026-11-03 重锚：`"ConcurrencyCheck"` 字面量已改为 PalORMAttributeNames 常量。
    new("R-D5", "src/PalORM.SourceGen/PalORMAnalyzer.cs",
        "        bool hasConcurrency = SourceGenerationValidation.EnumerateMappedProperties(entityType)\n"
        + "            .Any(p => p.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, PalORMAttributeNames.ConcurrencyCheck)));\n"
        + "        if (hasConcurrency)\n"
        + "        {\n"
        + "            ctx.ReportDiagnostic(Diagnostic.Create(\n"
        + "                SaveWithConcurrencyToken, invocation.GetLocation(), entityType.Name));\n"
        + "        }\n",
        "        // 注入：PALORM054 检测停用（回放 R-D5）\n"
        + "        _ = entityType;\n",
        "/*/*/AnalyzerDiagnosticsTests/*",
        "D5 SaveAsync 无编译期拒绝（PALORM054 不再报告）；"
        + "可观测量：期望诊断 PALORM054 缺席",
        "test/PalORM.SourceGen.Tests"),
    new("R-D6", "src/PalORM.Core/QueryBuilderExtensions.cs",
        "        if (paged._skip.HasValue)\n"
        + "            throw new InvalidOperationException(\n"
        + "                \"ToPageAsync uses keyset pagination with lastValue as the continuation anchor, so a Skip() on the builder \"\n"
        + "                + \"would be silently ignored. Remove Skip(), or use ToListAsync with Skip/Take for OFFSET pagination.\");\n",
        "",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "D6 ToPageAsync 放行 Skip（删构建期拒绝）；可观测量：Skip()+ToPageAsync 不再抛 InvalidOperationException。"
        + "2026-11-03 重锚：C-PAGE-01 已并入 D-PAGE-01（方案 §5.2），原 CapabilityContractTests 过滤器下已无用例",
        null, ["D-PAGE-01"]),
    new("R-D7", "src/PalORM.Core/CommandTimeoutNormalizer.cs",
        "=> dialect == SqlDialect.MySql && timeoutSeconds > 0;",
        "=> false;",
        "/*/*/ErrorContractTests/*",
        "D7 MySQL 超时未归一（耗时判定恒关）；可观测量：超时返回部分结果被当成功"),

    // ── R-C：契约族回放（方案 §6.2 降级路径，2026-11-03）──────────────────
    // 每条规则注入一个产品缺陷，要求「方言契约目录」里指定 ID 的用例报红——证明该契约不是
    // 恒真断言。过滤器覆盖 20 条 SQLite 契约（全部内存库，约 0.6 秒），期望契约由
    // ExpectedContracts 声明：报红名单缺它即 FAIL（别的原因红了不构成本契约的判别力证据）。
    new("R-C01", "src/PalORM.Core/DataSession_Bulk.cs",
        "            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);\n"
        + "        }\n"
        + "\n"
        + "        return total;\n"
        + "    }\n"
        + "\n"
        + "    /// <summary>json_each 形态的 DELETE",
        "            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);\n"
        + "        }\n"
        + "\n"
        + "        return total - 1;\n"
        + "    }\n"
        + "\n"
        + "    /// <summary>json_each 形态的 DELETE",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "SQLite 批量删除（json_each 形态）少删一行：返回数恒比实际少 1",
        null, ["D-BULKDEL-01"]),
    new("R-C02", "src/PalORM.Core/RowPreparedBulkInsert.cs",
        "                    valuesBinder(pool, entity, 0);\n"
        + "                    total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);",
        "                    if (ReferenceEquals(entity, entities[entities.Count - 1])) break;\n"
        + "                    valuesBinder(pool, entity, 0);\n"
        + "                    total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "SQLite 行预编译批量插入漏掉末行（影响行数一并少 1）",
        null, ["D-BULKIN-01"]),
    new("R-C03", "src/PalORM.Core/DataSession.Crud.cs",
        "            backfill(entity, id);",
        "            backfill(entity, 0);",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "自增主键回填写入 0（实体 Id 保持默认值）",
        null, ["D-INSERT-02"]),
    new("R-C04", "src/PalORM.Core/DataSession.Crud.cs",
        "        if (plan.IncrementVersion is null)\n"
        + "            return affectedRows;",
        "        if (plan.IncrementVersion is null)\n"
        + "            return affectedRows == 0 ? 1 : affectedRows;",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "UpdateAsync 未命中行也报 1（受影响行数虚报）",
        null, ["D-UPDATE-01"]),
    new("R-C05", "src/PalORM.Core/DataSession.PartialUpdate.cs",
        "            selected.Add(index);",
        "            for (int all = 0; all < plan.UpdateColumns.Count; all++) selected.Add(all);",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "部分更新忽略列选择器，改写全部列",
        null, ["D-UPDCOLS-01"]),
    new("R-C06", "src/PalORM.Core/DataSession.Crud.cs",
        "WHERE {TProvider.QuoteIdentifier(pkColumn)} = @p0{filter}\";",
        "WHERE {TProvider.QuoteIdentifier(pkColumn)} <> @p0{filter}\";",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "GetAsync 主键谓词反号（命中行查不到、未命中行反而查出）",
        null, ["D-GET-01"]),
    new("R-C07", "src/PalORM.Core/QueryBuilder.cs",
        "        builder.Append(\"WHERE \");",
        "        builder.Append(\"WHERE 1 = 1 OR \");",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "用户 WHERE 谓词被恒真式短路（过滤不生效）",
        null, ["D-QUERY-01"]),
    new("R-C08", "src/PalORM.Core/QueryBuilderExtensions.cs",
        "                string operation = descending ? \"<\" : \">\";",
        "                string operation = descending ? \"<\" : \">=\";",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "keyset 续页锚点由「严格大于」改为「大于等于」（升序页间重复一行）",
        null, ["D-PAGE-01"]),
    new("R-C09", "src/PalORM.Core/DataSession.Query.cs",
        "        object? r = await ExecuteAggregateScalarAsync<T>(\"AVG\", expression, ct).ConfigureAwait(false);",
        "        object? r = await ExecuteAggregateScalarAsync<T>(\"SUM\", expression, ct).ConfigureAwait(false);",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "AvgAsync 实际执行 SUM（未按行数取平均）",
        null, ["D-AGG-01"]),
    new("R-C10", "src/PalORM.SourceGen/CommandFactoryEmitter.cs",
        "                $\"{column} = excluded.{column}\"));",
        "                $\"{column} = {column}\"));",
        "/*/*/DialectCapabilityContractTests/Sqlite_Contract_Holds",
        "UPSERT 冲突分支 SET 自赋值（已存在行不更新）",
        null, ["D-SAVE-02"]),
};

var selected = rulePrefix is null
    ? rules
    : rules.Where(rule => rule.Id.StartsWith(rulePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
if (rulePrefix is not null)
    Console.WriteLine($"规则过滤 --rule {rulePrefix}：{selected.Length}/{rules.Length} 条\n");


var results = new List<RuleResult>();
foreach (Rule rule in selected)
{
    string project = rule.TestProject ?? DefaultTestProject;
    string[] expected = rule.ExpectedContracts ?? [];
    Console.WriteLine($"── {rule.Id}: {rule.Note}");
    Console.WriteLine($"   文件 {rule.File}");
    Console.WriteLine($"   项目 {project}");
    if (expected.Length > 0) Console.WriteLine($"   期望报红契约 {string.Join(", ", expected)}");

    string absolutePath = Path.Combine(repoRoot, rule.File);
    string original = File.ReadAllText(absolutePath);
    bool crlf = original.Contains("\r\n", StringComparison.Ordinal);
    string matchText = crlf ? rule.Original.Replace("\n", "\r\n", StringComparison.Ordinal) : rule.Original;
    string mutation = crlf ? rule.Mutated.Replace("\n", "\r\n", StringComparison.Ordinal) : rule.Mutated;
    if (!original.Contains(matchText, StringComparison.Ordinal))
    {
        Console.WriteLine("   SKIP 原片段未找到（源码可能已改动），跳过该规则");
        results.Add(new RuleResult(
            rule, project, "skip", false, false, new RunOutcome(false, 0, "", ""), false,
            new RunOutcome(false, 0, "", "")));
        continue;
    }

    File.WriteAllText(absolutePath, original.Replace(matchText, mutation, StringComparison.Ordinal));

    bool injectedBuild = Build(dotnet, repoRoot, project);
    RunOutcome injected = injectedBuild
        ? RunTests(dotnet, repoRoot, rule.Filter, project)
        : new RunOutcome(false, 0, "", "");

    Restore(repoRoot, rule.File);
    bool restoredBuild = Build(dotnet, repoRoot, project);
    RunOutcome restored = restoredBuild
        ? RunTests(dotnet, repoRoot, rule.Filter, project)
        : new RunOutcome(false, 0, "", "");

    bool injectedRed = injectedBuild && injected.Red;
    bool restoredGreen = restoredBuild && !restored.Red;
    // 契约族规则：报红名单里必须出现期望契约 ID，否则红的原因不是这条契约
    string[] missing = [.. expected.Where(id => !injected.FailedArgs.Contains(id, StringComparison.Ordinal))];
    bool expectedHit = injectedBuild && missing.Length == 0;
    string verdict = injectedBuild && injectedRed && expectedHit && restoredBuild && restoredGreen ? "PASS" : "FAIL";
    Console.WriteLine($"   注入: 构建={(injectedBuild ? "ok" : "fail")} 测试={(injectedRed ? "red" : "未红")} "
        + $"(报红 {injected.Failed} 项)");
    if (injected.FailedNames.Length > 0) Console.WriteLine($"   报红: {injected.FailedNames}");
    if (injected.FailedArgs.Length > 0) Console.WriteLine($"   报红契约: {injected.FailedArgs}");
    if (expected.Length > 0)
        Console.WriteLine(missing.Length == 0
            ? "   期望契约: 命中"
            : $"   期望契约: 未命中（缺 {string.Join(", ", missing)}）");
    Console.WriteLine($"   还原: 构建={(restoredBuild ? "ok" : "fail")} 测试={(restoredGreen ? "green" : "未转绿")}");
    Console.WriteLine($"   判定 {verdict}");
    Console.WriteLine();

    results.Add(new RuleResult(rule, project, verdict, injectedBuild, injectedRed, injected, restoredBuild, restored));
}

Console.WriteLine("汇总");
Console.WriteLine("| 规则 | 注入构建 | 注入报红 | 还原构建 | 还原转绿 | 判定 |");
Console.WriteLine("|------|----------|----------|----------|----------|------|");
foreach (RuleResult result in results)
{
    Console.WriteLine(
        $"| {result.Rule.Id} | {(result.InjectedBuild ? "ok" : "fail")} "
        + $"| {(result.Injected.Red ? $"red({result.Injected.Failed})" : "未红")} "
        + $"| {(result.RestoredBuild ? "ok" : "fail")} "
        + $"| {(result.RestoredBuild && !result.Restored.Red ? "green" : "未绿")} | {result.Verdict} |");
}

Console.WriteLine();
Console.WriteLine("回放台账（Markdown，供 docs/一致性测试总报告 \"未完成项\" 登记）");
Console.WriteLine("| 规则 | 缺陷 | 期望契约 | 注入点 | 目标过滤器 | 报红测试数 | 报红测试名 | 判定 |");
Console.WriteLine("|------|------|----------|--------|------------|------------|------------|------|");
foreach (RuleResult result in results)
{
    string expected = result.Rule.ExpectedContracts is { Length: > 0 } ids ? string.Join(", ", ids) : "—";
    Console.WriteLine(
        $"| {result.Rule.Id} | {Escape(result.Rule.Note.Split('；')[0])} "
        + $"| {expected} "
        + $"| `{result.Rule.File}` | `{result.Rule.Filter}` "
        + $"| {result.Injected.Failed} | {Escape(result.Injected.FailedNames)} | {result.Verdict} |");
}

int failed = results.Count(r => r.Verdict != "PASS");
int skipped = results.Count(r => r.Verdict == "skip");
Console.WriteLine();
Console.WriteLine(failed == 0 ? "全部规则通过" : $"{failed} 条规则未通过");
if (skipped > 0)
{
    // B189 族（无声 no-op）的机械提示：skip 也是"未通过"，但根因是注入片段与源码失配，
    // 修法是重锚片段而非改判定。不单列出来时，这两条规则会长期以"未通过"名义被当成
    // 已知噪声忽略——D4/D5 就这样静默失效了数月（2026-11-03 重锚后转 PASS）。
    Console.WriteLine($"其中 {skipped} 条为 SKIP（注入片段在源码中已失配，规则从未真正执行）：");
    foreach (RuleResult result in results.Where(static r => r.Verdict == "skip"))
    {
        Console.WriteLine($"  {result.Rule.Id}: `{result.Rule.File}` — 需重锚 Original 片段");
    }
}
return failed == 0 ? 0 : 1;

static string Escape(string text)
    => text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
        .Replace("|", "/", StringComparison.Ordinal);

static bool Build(string dotnet, string workingDirectory, string project)
{
    var (exit, _) = Execute(dotnet, $"build {project} -c Debug --nologo", workingDirectory);
    return exit == 0;
}

static RunOutcome RunTests(string dotnet, string workingDirectory, string filter, string project)
{
    var (exit, output) = Execute(
        dotnet,
        $"run --project {project} -c Debug --no-build -- --treenode-filter \"{filter}\"",
        workingDirectory);
    (int failed, string names, string failedArgs) = ParseFailures(output);
    if (exit != 0 && failed == 0)
    {
        // 解析失败时把尾巴打出来，避免"红但数不出来"静默通过
        Console.WriteLine("   诊断：退出码非零但未解析到失败摘要，输出尾部：");
        string[] tail = output.Split('\n');
        foreach (string line in tail[^Math.Min(20, tail.Length)..])
            Console.WriteLine($"     {line.TrimEnd()}");
    }
    return new RunOutcome(exit != 0, failed, names, failedArgs);
}

/// <summary>从 TUnit 控制台输出取失败项数与失败测试全名。失败块首行只给短名
/// （"失败 E4_MySql_... (1s 163ms)"），全名在紧随其后的栈帧（"在 命名空间.类.方法()"）里取。
/// 参数化用例的首行形如 "失败 Sqlite_Contract_Holds(D-BULKDEL-01) (19ms)"——括号里的实参是
/// 契约 ID 的唯一来源（栈帧里的方法名不含实参），单独收集进 FailedArgs 供契约族规则判定。
/// 摘要行形如 "失败: 3"。输出按 UTF-8 解码（中文摘要与栈帧前缀依赖它）。</summary>
static (int Failed, string Names, string Args) ParseFailures(string output)
{
    string text = Regex.Replace(output, "\u001b\\[[0-9;]*m", string.Empty);
    string[] lines = text.Split('\n');
    var names = new List<string>();
    var args = new List<string>();
    for (int i = 0; i < lines.Length; i++)
    {
        Match header = Regex.Match(
            lines[i],
            @"^\s*(?:失败|failed)\s+(?<short>[^\s(]+)(?:\((?<args>[^)]*)\))?",
            RegexOptions.IgnoreCase);
        if (!header.Success) continue;
        string shortName = header.Groups["short"].Value.Trim();
        if (header.Groups["args"].Success)
            args.Add(header.Groups["args"].Value.Trim());
        string fullName = shortName;
        for (int j = i + 1; j < Math.Min(i + 40, lines.Length); j++)
        {
            Match frame = Regex.Match(lines[j], @"(?:在|at)\s+(?<fqn>[A-Za-z0-9_.+]+)\(");
            if (frame.Success && frame.Groups["fqn"].Value.EndsWith("." + shortName, StringComparison.Ordinal))
            {
                fullName = frame.Groups["fqn"].Value;
                break;
            }
        }
        names.Add(fullName);
    }
    Match summary = Regex.Match(text, @"(?:失败|failed)\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase);
    int failed = summary.Success
        ? int.Parse(summary.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
        : names.Count;
    return (failed,
        string.Join(", ", names.Distinct(StringComparer.Ordinal)),
        string.Join(", ", args.Distinct(StringComparer.Ordinal)));
}

static void Restore(string repoRoot, string file)
    => Execute("git", $"checkout -- \"{file}\"", repoRoot);

static string GitStatus(string repoRoot)
{
    // 只关心已跟踪文件的改动：未跟踪文件不会被 git checkout 影响
    var (_, output) = Execute("git", "status --porcelain -uno", repoRoot);
    return output.Trim();
}

static List<(string Commit, string Subject)> GitLog(string repoRoot)
{
    var (_, output) = Execute("git", "log -1 --pretty=format:%H%x09%s", repoRoot);
    var list = new List<(string, string)>();
    foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        string[] parts = line.Split('\t', 2);
        if (parts.Length == 2) list.Add((parts[0].Trim(), parts[1].Trim()));
    }
    return list;
}

static (int Exit, string Output) Execute(string fileName, string arguments, string workingDirectory)
{
    var startInfo = new ProcessStartInfo(fileName, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory,
    };
    using var process = Process.Start(startInfo)!;
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
}

static string FindRepoRoot()
{
    string? dir = Directory.GetCurrentDirectory();
    while (!string.IsNullOrEmpty(dir))
    {
        if (File.Exists(Path.Combine(dir, "Directory.Build.props"))) return dir;
        dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("找不到仓库根（Directory.Build.props）");
}

static string ResolveDotnet()
{
    // global.json 锁定 rc.2，系统 SDK 可能已升级；优先用仓库既有本地 SDK，其次 PATH
    const string localSdk = @"C:\temp\dotnet-rc2\dotnet.exe";
    return File.Exists(localSdk) ? localSdk : "dotnet";
}

internal sealed record Rule(
    string Id, string File, string Original, string Mutated, string Filter, string Note,
    string? TestProject = null, string[]? ExpectedContracts = null);

internal sealed record RunOutcome(bool Red, int Failed, string FailedNames, string FailedArgs);

internal sealed record RuleResult(
    Rule Rule, string Project, string Verdict, bool InjectedBuild, bool InjectedRed, RunOutcome Injected,
    bool RestoredBuild, RunOutcome Restored);