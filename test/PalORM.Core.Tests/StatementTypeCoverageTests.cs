using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PalORM.Core.Tests;

/// <summary>语句类型与查询子句的坐标登记守卫（《测试体系增强方案-2026-10-30》§5.3）。
/// <para><b>它挡什么</b>：新增语句类型（或查询子句）而无人给它写断言。不评估断言质量——
/// 质量由 Stryker（恢复后）与历史缺陷回放规则承担，三者不可互相替代。</para>
/// <para><b>两条断言轴</b>：① 正向——每个已登记坐标的证据文件存在、非空、含登记的证据片段；
/// 证据形态默认必须为 SQL 文本断言，行为断言只对驱动级 / 内部合并路径开放，且必须在
/// <see cref="_behaviorEvidenceAllowList"/> 登记（新增坐标拿"行为断言"绕开 SQL 文本要求会被拦下）。
/// ② 反向——查询子句以 <see cref="QueryClauseKind"/> 枚举为准（新增枚举值即失败），语句类型以
/// 产品源码字符串字面量中出现的动词为准（出现未登记的 MERGE/TRUNCATE/ALTER/DROP/EXEC/REPLACE 即失败）。</para>
/// <para><b>反向守卫的已知边界</b>：动词级扫描识别"新语句族"，识别不了"同族新变体"
/// （例如新增一种 CREATE 语句不会被拦）。这是文本扫描能给出的最强机械保证；
/// 更强的形态需要产品侧提供语句枚举，属产品改动，不在本方案范围。</para>
/// </summary>
internal sealed class StatementTypeCoverageTests
{
    private const int ExpectedStatementTypeCount = 14;
    private const int ExpectedClauseCount = 12;

    private sealed record StatementCoordinate(
        string Id, string Statement, string File, string Marker, EvidenceKind Evidence, string Rationale);

    /// <summary>证据形态。默认必须是 SQL 文本断言；只有驱动级协议等确实不产生 DbCommand 文本的
    /// 路径才允许行为断言，并且必须登记进 <see cref="_behaviorEvidenceAllowList"/>。</summary>
    private enum EvidenceKind
    {
        SqlText,
        Behavior,
    }

    private sealed record ClauseCoordinate(
        QueryClauseKind Kind, string File, string Marker, string Rationale);

    /// <summary>行为断言白名单：这几条路径在产品内不产生可断言的 DbCommand SQL 文本
    /// （PG Binary COPY 走 NpgsqlBinaryImporter、MySQL LOAD DATA 在 MySqlBulkCopy 内部、
    /// 存储过程以 CommandType.StoredProcedure 执行、SessionBatch 的合并 SQL 在引擎内部）。
    /// 新增坐标若想用行为断言，必须在此登记并写明理由，否则守卫按"缺 SQL 断言"失败。</summary>
    private static readonly string[] _behaviorEvidenceAllowList =
        ["PG-COPY", "MYSQL-LOAD-DATA", "STORED-PROC", "MULTI-STATEMENT"];

    /// <summary>14 个语句类型（方案 §5.3 手写清单）：CREATE TABLE / CREATE INDEX / DELETE /
    /// INSERT 单行 / INSERT 多值 / UPSERT ON CONFLICT / UPSERT ON DUPLICATE KEY / UPDATE /
    /// PG COPY / MySQL LOAD DATA / RETURNING / SELECT / 存储过程 / 多语句。</summary>
    private static readonly StatementCoordinate[] _statementTypes =
    [
        new("CREATE-TABLE", "CREATE TABLE", "test/PalORM.SourceGen.Tests/Snapshots/Migration_global__AllTypesEntity_7f6c8145.g.cs.snap",
            "CREATE TABLE IF NOT EXISTS", EvidenceKind.SqlText,
            "快照逐字比对，SnapshotTests 断言生成物与基线一致；列型/主键/默认值都在快照内"),
        new("CREATE-INDEX", "CREATE INDEX", "test/PalORM.SourceGen.Tests/Snapshots/Migration_global__FullFeatureEntity_4964faa8.g.cs.snap",
            "CREATE INDEX IF NOT EXISTS", EvidenceKind.SqlText,
            "同快照；索引名/列序/UNIQUE 形态在快照内，三方言各一份"),
        new("DELETE", "DELETE", "test/PalORM.Core.Tests/BulkDeleteArrayFormTests.cs",
            "= ANY(@ids)", EvidenceKind.SqlText,
            "批删数组形态 SQL 断言；IN 形态同文件另有一条"),
        new("INSERT-SINGLE", "INSERT（单行）", "test/PalORM.Core.Tests/InsertNoReturningTests.cs",
            "Contains(\"RETURNING\"", EvidenceKind.SqlText,
            "纯 INSERT 载荷不得携带 RETURNING，读返回变体必须有，正反两条"),
        new("INSERT-MULTI", "INSERT（多值段）", "test/PalORM.Core.Tests/StatementSqlShapeTests.cs",
            "MultiValueBulkInsert.AppendInsertStatement", EvidenceKind.SqlText,
            "两行 VALUES 段逐字断言：行内参数平铺、行间 \", \" 分隔"),
        new("UPSERT-ON-CONFLICT", "UPSERT（PG/SQLite ON CONFLICT）", "test/PalORM.Core.Tests/StatementSqlShapeTests.cs",
            "UpsertReturning).Contains(\"ON CONFLICT", EvidenceKind.SqlText,
            "冲突列名 + DO UPDATE SET 赋值形态一并断言"),
        new("UPSERT-ON-DUPLICATE-KEY", "UPSERT（MySQL ON DUPLICATE KEY）", "test/PalORM.Core.Tests/StatementSqlShapeTests.cs",
            "UpsertMySql).Contains(\"ON DUPLICATE KEY UPDATE", EvidenceKind.SqlText,
            "VALUES(col) 赋值 + 自增键 LAST_INSERT_ID 回填"),
        new("UPDATE", "UPDATE", "test/PalORM.Core.Tests/BulkUpdateBatchReuseTests.cs",
            "UPDATE `t` SET", EvidenceKind.SqlText,
            "批量 UPDATE 单语句形态：SET 段与表名引用"),
        new("PG-COPY", "PG Binary COPY", "test/PalORM.Integration.Tests/PgCopyByteCharTests.cs",
            "BulkInsertAsync(", EvidenceKind.Behavior,
            "Binary COPY 经 NpgsqlBinaryImporter，产品不经过 DbCommand，无 SQL 文本可断言；"
            + "由 byte/char 列的值级往返锁定，含 0x00 字节"),
        new("MYSQL-LOAD-DATA", "MySQL LOAD DATA", "test/PalORM.Integration.Tests/ExternalDatabaseBulkTests.cs",
            "local_infile", EvidenceKind.Behavior,
            "MySqlBulkCopy 内部走 LOAD DATA LOCAL INFILE，无 SQL 文本可断言；"
            + "由 local_infile 两态 + 行数/值对账锁定"),
        new("RETURNING", "RETURNING", "test/PalORM.Core.Tests/StatementSqlShapeTests.cs",
            "InsertReturning).Contains(\"RETURNING\"", EvidenceKind.SqlText,
            "PG/SQLite 的 INSERT RETURNING 载荷；MySQL 侧对应 LAST_INSERT_ID 族"),
        new("SELECT", "SELECT", "test/PalORM.Core.Tests/WhereInArrayFormTests.cs",
            "\" IN (\"", EvidenceKind.SqlText,
            "DryRun SELECT 的 IN 形态断言；IN 数组形态与参数编号一并锁住"),
        new("STORED-PROC", "存储过程调用", "test/PalORM.Integration.Tests/Conformance/TriDialectStoredProcTests.cs",
            "StoredProc(", EvidenceKind.Behavior,
            "CommandType.StoredProcedure，CommandText 为过程名；三方言各一条：SQLite 显式失败、PG/MySQL 真写行"),
        new("MULTI-STATEMENT", "多语句批次", "test/PalORM.Core.Tests/SessionBatchTests.cs",
            ".Append(", EvidenceKind.Behavior,
            "SessionBatch 合并多语句单批执行与整批回滚；合并 SQL 在引擎内部，无断言入口"),
    ];

    /// <summary>12 个查询子句：以 <see cref="QueryClauseKind"/> 为准，逐项登记证据文件。</summary>
    private static readonly ClauseCoordinate[] _clauses =
    [
        new(QueryClauseKind.Comment, "test/PalORM.Integration.Tests/ExpressionBuilderSmokeTests.cs",
            "Contains(\"aot-smoke\")", "Tag() 生成 /* ... */ 块注释，DryRun 断言注释内容进 SQL"),
        new(QueryClauseKind.CommonTableExpression, "test/PalORM.Integration.Tests/MultiEntityTests.cs",
            "Contains(\"WITH", "With(cte) 生成的 WITH 前缀与 CTE 名引用"),
        new(QueryClauseKind.Window, "test/PalORM.Integration.Tests/AdvancedFeatureTests.cs",
            "Contains(\"ROW_NUMBER\")", "UnsafeWindowOver 生成的窗口函数片段"),
        new(QueryClauseKind.Join, "test/PalORM.Integration.Tests/MultiEntityTests.cs",
            "Contains(\"JOIN\")", "InnerJoin/LeftJoin 生成的 JOIN 片段"),
        new(QueryClauseKind.DefaultFilter, "test/PalORM.Core.Tests/FirstSinglePipelineAndSharedNodeTests.cs",
            "Contains(\"deleted_at\")", "软删默认过滤注入 WHERE 的形态（首行/单行两条管线）"),
        new(QueryClauseKind.Where, "test/PalORM.Core.Tests/WhereInArrayFormTests.cs",
            "\" IN (\"", "Where 生成的 IN 形态与参数编号"),
        new(QueryClauseKind.GroupBy, "test/PalORM.Integration.Tests/ExpressionBuilderSmokeTests.cs",
            "Contains(\"GROUP BY\")", "GroupBy 生成的 GROUP BY 片段"),
        new(QueryClauseKind.Having, "test/PalORM.Integration.Tests/ExpressionBuilderSmokeTests.cs",
            "Contains(\"HAVING\")", "Having 生成的 HAVING 片段"),
        new(QueryClauseKind.OrderBy, "test/PalORM.Core.Tests/CteColumnQualificationTests.cs",
            "Contains(\"ORDER BY", "OrderBy 生成的 ORDER BY 列限定形态"),
        new(QueryClauseKind.Set, "test/PalORM.Core.Tests/BatchUpdateSqlBuilderTests.cs",
            "= v.pk SET", "Set 子句进入 UPDATE 的 SET 段（批量更新形态）"),
        new(QueryClauseKind.Raw, "test/PalORM.Core.Tests/RawControlCharGuardTests.cs",
            "it''s fine", "Raw 片段原样进入 SQL（含单引号转义与拒绝控制字符）"),
        new(QueryClauseKind.Lock, "test/PalORM.Integration.Tests/PessimisticLockTests.cs",
            "IndexOf(\"FOR UPDATE\"", "Lock 子句位置契约：FOR UPDATE 必须位于 WHERE 之后"),
    ];

    /// <summary>语句动词扫描表（反向守卫）。前八个是已登记族，后六个是"出现即失败"的新族。</summary>
    private static readonly string[] _statementVerbs =
    [
        "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "COPY", "LOAD", "CALL",
        "MERGE", "TRUNCATE", "ALTER", "DROP", "EXEC", "REPLACE",
    ];

    private static readonly HashSet<string> _registeredVerbs =
        new(["SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "COPY", "LOAD", "CALL"], StringComparer.Ordinal);

    /// <summary>扫描器哨兵：这些族必须在产品源码里被认出来，否则"空集也通过"。</summary>
    private static readonly string[] _sentinelVerbs = ["SELECT", "INSERT", "UPDATE", "DELETE", "CREATE"];

    private static readonly string[] _productSourceDirectories =
        ["PalORM.Core", "PalORM.SourceGen", "PalORM.MySql", "PalORM.PostgreSql", "PalORM.Sqlite"];

    private static readonly Regex _literalPattern =
        new("\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.None, TimeSpan.FromSeconds(10));

    private static readonly Regex _verbPattern = new(
        @"\b(" + string.Join("|", _statementVerbs) + @")\b",
        RegexOptions.None,
        TimeSpan.FromSeconds(10));

    [Test]
    public async Task StatementTypes_AreAllRegisteredWithEvidence()
    {
        await Assert.That(_statementTypes.Length).IsEqualTo(ExpectedStatementTypeCount);
        await Assert.That(_statementTypes.Select(static c => c.Id).Distinct(StringComparer.Ordinal).Count())
            .IsEqualTo(ExpectedStatementTypeCount);

        List<string> problems = await CollectEvidenceProblemsAsync(
            _statementTypes.Select(static c => (c.Id, c.File, c.Marker)));
        await Assert.That(string.Join("\n", problems)).IsEmpty();
    }

    [Test]
    public async Task BehaviorEvidence_IsLimitedToAllowList()
    {
        var declared = new HashSet<string>(
            _statementTypes.Where(static c => c.Evidence == EvidenceKind.Behavior).Select(static c => c.Id),
            StringComparer.Ordinal);
        var allowed = new HashSet<string>(_behaviorEvidenceAllowList, StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (string id in declared.Where(id => !allowed.Contains(id)))
        {
            problems.Add($"{id}: 以行为断言登记但不在白名单——新语句类型须先给 SQL 文本断言；"
                + "确无 DbCommand 文本时在白名单登记并写明驱动级理由");
        }
        foreach (string id in allowed.Where(id => !declared.Contains(id)))
        {
            problems.Add($"{id}: 白名单有而登记表不是行为断言——删除或改登记时同步移除白名单");
        }
        await Assert.That(string.Join("\n", problems)).IsEmpty();
    }

    [Test]
    public async Task QueryClauses_MatchEnumAndAllHaveEvidence()
    {
        QueryClauseKind[] enumValues = Enum.GetValues<QueryClauseKind>();

        // 先报未登记项：新增枚举值时失败信息直接给出坐标名，而不是只给一个计数差
        HashSet<QueryClauseKind> registered = [.. _clauses.Select(static c => c.Kind)];
        string unregistered = string.Join(", ",
            enumValues.Where(kind => !registered.Contains(kind)).Select(static kind => kind.ToString()));
        await Assert.That(unregistered).IsEmpty();

        await Assert.That(enumValues.Length).IsEqualTo(ExpectedClauseCount);
        await Assert.That(registered.Count).IsEqualTo(enumValues.Length);

        List<string> problems = await CollectEvidenceProblemsAsync(
            _clauses.Select(static c => (c.Kind.ToString(), c.File, c.Marker)));
        await Assert.That(string.Join("\n", problems)).IsEmpty();
    }

    [Test]
    public async Task ProductStatementVerbs_AreAllRegistered()
    {
        Dictionary<string, string> found = FindProductStatementVerbs();
        string unregistered = string.Join(", ", found
            .Where(pair => !_registeredVerbs.Contains(pair.Key))
            .Select(static pair => $"{pair.Key}（首个出现：{pair.Value}）"));
        await Assert.That(unregistered).IsEmpty();

        // 哨兵：扫描器本身必须能认出这几个族，否则空集也会"通过"
        string missingFamilies = string.Join(", ",
            _sentinelVerbs.Where(family => !found.ContainsKey(family)));
        await Assert.That(missingFamilies).IsEmpty();
    }

    private static async Task<List<string>> CollectEvidenceProblemsAsync(
        IEnumerable<(string Id, string File, string Marker)> coordinates)
    {
        var problems = new List<string>();
        foreach ((string id, string file, string marker) in coordinates)
        {
            string path = Path.Combine(RepositoryRoot(), file);
            if (!File.Exists(path))
            {
                problems.Add($"{id}: 覆盖文件不存在（{file}）——若文件已改名，同步更新本测试登记表");
                continue;
            }
            string text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            if (text.Length == 0)
            {
                problems.Add($"{id}: 覆盖文件为空（{file}）——该坐标实际无覆盖");
                continue;
            }
            if (!text.Contains(marker, StringComparison.Ordinal))
            {
                problems.Add($"{id}: {file} 不含证据片段 '{marker}'——断言被删或措辞已改，确认后更新登记");
            }
        }
        return problems;
    }

    private static Dictionary<string, string> FindProductStatementVerbs()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string directory in _productSourceDirectories)
        {
            foreach (string file in EnumerateProductSources(directory))
            {
                string source = File.ReadAllText(file);
                string relativePath = Path.GetRelativePath(RepositoryRoot(), file);
                RecordVerbs(source, relativePath, found);
            }
        }
        return found;
    }

    private static IEnumerable<string> EnumerateProductSources(string directory)
        => Directory
            .GetFiles(Path.Combine(RepositoryRoot(), "src", directory), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !IsBuildOutput(path))
            .OrderBy(static path => path, StringComparer.Ordinal);

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string separator = Path.DirectorySeparatorChar.ToString();
        return normalized.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
            || normalized.Contains($"{separator}bin{separator}", StringComparison.Ordinal);
    }

    private static void RecordVerbs(string source, string relativePath, Dictionary<string, string> found)
    {
        foreach (Match literal in _literalPattern.Matches(source))
        {
            foreach (Match verb in _verbPattern.Matches(literal.Groups[1].Value))
            {
                found.TryAdd(verb.Value, relativePath);
            }
        }
    }

    private static string RepositoryRoot([CallerFilePath] string testPath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "..", ".."));
}