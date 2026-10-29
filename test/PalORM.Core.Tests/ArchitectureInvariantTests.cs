using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PalORM.Core.Tests;

/// <summary>
/// 架构不变式测试（ITM-302 类缺陷的机械防线——"过滤覆盖断裂"根因类）。
/// 不变式：DataSession 所有触及实体表的公共入口，方法体必须经过默认过滤路由
/// （GetDefaultFilter* 或 内联 TenantParameterName 过滤 + BindDefaultFilterParameters）。
/// 背景：租户过滤曾仅覆盖 From&lt;T&gt;()，直连 CRUD/聚合全部绕过（跨租户数据泄露）；
/// ITM-404 再证 partial 文件（DataSession_Bulk.cs）是盲区——扫描范围覆盖全部
/// DataSession*.cs（ITM-405），方法体提取改为花括号配对（ITM-413：不再依赖
/// "\n    }" 文本定界，表达式体/嵌套方法不误吞）。
/// <para><b>T2 白盒豁免（r19/T-P3-19）</b>：本类以源码文本扫描为断言手段——架构不变式
/// （"所有 X 必须经过 Y"）在类型系统/运行时层面不可表达，只能源码级验证；这是该防线
/// 的设计形态，非实现细节窥探。</para>
/// </summary>
public sealed class ArchitectureInvariantTests
{
    /// <summary>必须经过默认过滤路由的实体表入口（新增触表入口时在此登记）。</summary>
    private static readonly string[] _filteredEntryPoints =
    [
        "GetAsync",
        "GetAllAsync",
        "UpdateCoreAsync",   // UpdateAsync 的实现体
        "UpdateColumnsEntryAsync", // 列子集更新内核：租户谓词经 HasTenantFilter + BindDefaultFilterParameters（写路径无软删过滤）
        "DeleteAsync",
        "CountAsync",
        "SumAsync",
        "MaxAsync",
        "MinAsync",
        "AvgAsync",
        "BulkDeleteAsync",   // ITM-404：批量删除与单条同语义
    ];

    /// <summary>豁免清单：触表但按契约不加默认过滤的入口（每项必须给依据）。</summary>
    private static readonly Dictionary<string, string> _exemptEntryPoints = new()
    {
        // 原始 SQL 家族：用户自带完整 SQL，框架不知道目标表——契约文档已声明过滤不适用
        ["QueryAsync"] = "原始 SQL 契约：用户 SQL 不可注入过滤（文档已声明）",
        ["QueryFirstAsync"] = "同 QueryAsync",
        ["QuerySingleAsync"] = "同 QueryAsync",
        ["ScalarAsync"] = "同 QueryAsync",
        ["ExecuteAsync"] = "同 QueryAsync",
        ["InsertAsync"] = "INSERT 无过滤语义（租户列值由实体携带）",
        ["SaveAsync"] = "UPSERT 委托 Insert/Update 路径",
        ["BulkInsertAsync"] = "同 InsertAsync（租户列值由实体携带）",
        ["BulkUpdateAsync"] = "逐条委托 UpdateCoreAsync（已过滤路由）",
        ["BulkUpdateBatchAsync"] = "v5.0 阶段 4.3b：批量 UPDATE 单语句（CASE WHEN/FROM VALUES）。租户过滤在 SQL 末尾自动追加，与 BulkUpdateAsync 对齐",
        ["BulkMergeAsync"] = "逐条委托 SaveCoreAsync（Insert/Update 语义）",
        ["SeedAsync"] = "委托 BulkMergeAsync",
        // ITM-677：实体物化原始 SQL 流式入口——与 QueryAsync 同族，文档已声明过滤不适用
        ["QueryAsyncEnumerable"] = "原始 SQL 契约：用户 SQL 不可注入过滤（文档已声明，ITM-677）",
        ["QueryStructsAsync"] = "同 QueryAsync：结构体投影直查，用户 SQL 自带过滤",
        ["UpdateColumnsAsync"] = "薄包装：过滤路由在内核 UpdateColumnsEntryAsync",
        // C4（v5.6.0）：连接池预热——不触表（只建连/归还池，无 SQL 到任何实体表）
        ["PreWarmAsync"] = "连接池预热：仅建连 + Provider 初始化钩子，不触任何实体表",
        // D2（2026-10-29）：批量插入回填主键——INSERT 无过滤语义（租户列值由实体携带），
        // RETURNING 只回读本次刚写入的行，不做范围读
        ["BulkInsertReturningAsync"] = "同 BulkInsertAsync：INSERT 无过滤语义，RETURNING 只回读本批写入行",
    };

    [Test]
    public async Task AllEntityTableEntryPoints_RouteThroughDefaultFilter()
    {
        string source = ReadAllDataSessionSources();
        var violations = new List<string>();

        foreach (string method in _filteredEntryPoints)
        {
            string? body = ExtractMethodBody(source, method);
            if (body is null)
            {
                violations.Add($"{method}: 在 DataSession*.cs 中未找到——若已改名/移除，同步更新本测试登记表");
                continue;
            }
            bool routed =
                body.Contains("GetDefaultFilter", StringComparison.Ordinal)
                || (body.Contains("TenantParameterName", StringComparison.Ordinal)
                    && body.Contains("BindDefaultFilterParameters", StringComparison.Ordinal))
                // 内联 tenantFilter 构造（HasTenantFilter 判定 + 参数绑定）也算路由
                || (body.Contains("HasTenantFilter", StringComparison.Ordinal)
                    && body.Contains("BindDefaultFilterParameters", StringComparison.Ordinal))
                // 聚合家族经 ExecuteScalarAsync 集中绑定，方法体只需出现过滤子句构造
                || body.Contains("GetDefaultFilterWhereClause", StringComparison.Ordinal)
            // v5.4 精炼 L3：聚合家族（Sum/Max/Min/Avg）改经 ExecuteAggregateScalarAsync
            // 集中路由——过滤子句构造移入该内核，薄包装体以内核调用名为路由凭证
            || body.Contains("ExecuteAggregateScalarAsync", StringComparison.Ordinal)
            // C10（2026-10-12 审读重开）：GetAsync/GetAllAsync 直通直调改造——过滤路由
            //（GetDefaultFilter*/BindDefaultFilterParameters）移入 GetByKeyCoreAsync/
            // GetAllCoreAsync 内核，薄包装体以内核调用名为路由凭证（L3 同款先例）
            || body.Contains("GetByKeyCoreAsync", StringComparison.Ordinal)
            || body.Contains("GetAllCoreAsync", StringComparison.Ordinal)
            // R4（2026-10-12 读预热包装）：GetAsync/GetAllAsync/Count/聚合/Query/Scalar 改
            // 非 async 薄入口（预热 + 委托 *EntryAsync 内核），过滤路由在内核体内——
            // 委托调用名为路由凭证（与 C10/L3 同款口径）
            || body.Contains("EntryAsync", StringComparison.Ordinal);
            if (!routed)
                violations.Add($"{method}: 方法体未经过默认过滤路由（GetDefaultFilter*/HasTenantFilter+BindDefaultFilterParameters）");
        }

        await Assert.That(string.Join("\n", violations)).IsEmpty();
    }

    [Test]
    public async Task NewPublicEntryPoints_AreRegisteredInThisTest()
    {
        // 反向守卫：DataSession*（含 partial）出现新的 public *Async 入口而两张表都没登记 → 失败。
        string source = ReadAllDataSessionSources();
        var known = _filteredEntryPoints
            .Concat(_exemptEntryPoints.Keys)
            .Concat(
            [
                // 非触表或非实体泛型入口（会话/事务/迁移/健康检查/生命周期）
                "UpdateAsync", "ValidateSchemaAsync", "MigrateAsync",
                "SavepointAsync", "RollbackToAsync", "ExecuteWithResilience",
                "BeginTransactionAsync", "WithTransaction", "HealthCheckAsync",
                // API-003（2026-09-23）：整事务重放——事务编排入口，不触表、不走过滤路由
                "WithTransactionRetry",
                "ForRead", "DisposeAsync", "QueryMultipleAsync",
                "CreateAsync",
                // 池化（2026-10-13 二轮方案）：DataSessionPool.RentAsync 是会话生命周期入口
                //（租出不触表，过滤路由由租期内的具体查询入口承担）
                "RentAsync",
            ])
            .ToHashSet(StringComparer.Ordinal);

        var unregistered = Regex.Matches(
                source,
                @"public\s+(?:static\s+)?(?:async\s+)?(?:ValueTask|Task|IAsyncEnumerable)[^\n(]*?\s(\w+)(?:<[^>(]+>)?\s*\(",
                RegexOptions.Multiline)
            .Select(static match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !known.Contains(name))
            .ToArray();

        await Assert.That(string.Join(", ", unregistered))
            .IsEmpty()
            .Because("DataSession 新增公共入口必须在 ArchitectureInvariantTests 登记：" +
                "触表入口进 _filteredEntryPoints（接过滤路由），豁免进 _exemptEntryPoints（给依据）");
    }

    /// <summary>EC 包含绊线（2026-10-14 G2 证伪轮的机械防线）：Enter 族调用必须处于
    /// async 方法体内。非 async 方法调用 Enter 时，Enter 内的 AsyncLocal 写直接落在调用方
    /// 活 ExecutionContext 上（async 方法体内的写会被 async method builder 在同步完成路径
    /// 还原）；每操作新会话 = 新 AsyncLocal 实例键 → EC map 无界增长 → O(N²)——SQLite 并发
    /// 饱和循环实测 20× 塌方。同步完成方言（SQLite）是真挂起方言的盲区（真挂起在续体恢复时
    /// 重置 EC，天然防火墙），故缺陷只在 SQLite 并发臂显形。
    /// <para><b>豁免面</b>：SessionOperationState 自身（Enter 的定义处）；DataSession 的
    /// EnterOperation/EnterReadOnly/EnterBatchOperation 三个<b>同步转发包装</b>（安全性由
    /// 本测试的递归面承担：这些包装的全部调用点也必须 async）。</para></summary>
    [Test]
    public async Task EnterLeaseCalls_MustBeInsideAsyncMethods_EcContainment()
    {
        // 自检（验证验证者）：合成负样本必须被抓、正样本必须放行（行首 4 空格对齐真实源码的方法级缩进）
        string probeBad = string.Join("\n",
            "    private ValueTask<T?> BadEntryAsync<T>() where T : class",
            "    {",
            "        using var operation = EnterReadOnly();",
            "        return SomethingCoreAsync<T>();",
            "    }");
        string probeGood = string.Join("\n",
            "    private async ValueTask<T?> GoodEntryAsync<T>() where T : class",
            "    {",
            "        using var operation = EnterReadOnly();",
            "        return await SomethingCoreAsync<T>().ConfigureAwait(false);",
            "    }");
        await Assert.That(string.Join(";", DetectNonAsyncEnterCalls(probeBad, "probe.cs")))
            .Contains("BadEntryAsync");
        await Assert.That(string.Join(";", DetectNonAsyncEnterCalls(probeGood, "probe.cs")))
            .IsEmpty();

        // 实源扫描：src/PalORM.Core 全部 .cs（SessionOperationState 除外）
        var violations = new List<string>();
        foreach (string file in Directory.GetFiles(FindCoreDir(), "*.cs"))
        {
            if (Path.GetFileName(file) == "SessionOperationState.cs")
                continue;
#pragma warning disable CA1849, S6966
            violations.AddRange(DetectNonAsyncEnterCalls(File.ReadAllText(file), Path.GetFileName(file)));
#pragma warning restore CA1849, S6966
        }
        await Assert.That(string.Join("\n", violations))
            .IsEmpty()
            .Because("Enter 族（EnterOperation/EnterReadOnly/EnterBatchOperation/_operationState.Enter*/" +
                "EnterTransactionFlow/EnterTransactionOperation）的调用点必须在 async 方法体内：" +
                "非 async 调用使 Enter 的 AsyncLocal 写逃逸调用方 EC（G2 缺陷根因，SQLite 并发 20× 塌方）。" +
                "同步转发包装（DataSession 的三个私有包装）的调用点同样必须 async——安全性递归由本测试承担。");
    }

    /// <summary>定位 src/PalORM.Core（[CallerFilePath] 与既有 ReadAllDataSessionSources 同源手法）。</summary>
    private static string FindCoreDir([CallerFilePath] string testPath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!, "..", "..", "src", "PalORM.Core"));

    /// <summary>检测非 async 方法体内的 Enter 族调用：对每个调用点向上找最近的方法签名行，
    /// 签名不含 async 即违规。转发包装（表达式体单行转发）自身豁免，但其调用点由同一规则覆盖。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S3776",
        Justification = "源码扫描绊线的分支构成（调用点遍历/签名回溯/豁免判定）是文本分析的必然形态。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S3267",
        Justification = "签名回溯需要早退循环（首个在调用点之后的签名即停），Select 形态不可表达。")]
    private static List<string> DetectNonAsyncEnterCalls(string source, string fileName)
    {
        var violations = new List<string>();
        // Enter 族调用模式（调用点，非定义处）
        var callPattern = new Regex(
            @"(EnterOperation\(|EnterReadOnly\(|EnterBatchOperation\(|_operationState\.Enter\(|"
            + @"_operationState\.EnterTransactionFlow\(|_operationState\.EnterTransactionOperation\()");
        // 方法签名（4 空格缩进，含修饰符与返回类型；lambda/局部函数不匹配——调用点在语句层）
        var signaturePattern = new Regex(
            @"^[ \t]{4}(?:public|private|internal|protected)[^\n=;]*?\s(\w+)(?:<[^>(]+>)?\s*\(",
            RegexOptions.Multiline);

        foreach (Match call in callPattern.Matches(source))
        {
            // 向上找最近的方法签名
            Match? enclosing = null;
            foreach (Match signature in signaturePattern.Matches(source))
            {
                if (signature.Index >= call.Index)
                    break;
                enclosing = signature;
            }
            if (enclosing is null)
                continue;   // 顶层/字段初始化等非方法上下文（如 lambda 字段）——本绊线不覆盖
            string signatureLine = enclosing.Value;
            // 同步转发包装豁免：签名后 200 字符内直接 => 转发 _operationState.XXX——安全性递归到调用点
            string rest = source[enclosing.Index..Math.Min(source.Length, enclosing.Index + 200)];
            if (rest.Contains("=> _operationState.", StringComparison.Ordinal)
                || rest.Contains("=> EnterOperation(", StringComparison.Ordinal))
                continue;
            if (!signatureLine.Contains("async", StringComparison.Ordinal))
            {
                int line = 1 + source[..call.Index].Count(static ch => ch == '\n');
                violations.Add($"{fileName}:{line} {enclosing.Groups[1].Value}: Enter 族调用处于非 async 方法（EC 逃逸风险，G2 根因）");
            }
        }
        return violations;
    }

    /// <summary>提取方法体：从签名行起花括号配对到闭合（ITM-413——文本定界会吞并
    /// 表达式体方法后续内容或被注释满足）。表达式体方法（=&gt;）取到首个分号。</summary>
    private static string? ExtractMethodBody(string source, string methodName)
    {
        Match signature = Regex.Match(
            source,
            $@"^\s{{4}}(?:public|private|internal)[^\n]*\s{Regex.Escape(methodName)}(?:<[^>(]+>)?\s*\(",
            RegexOptions.Multiline);
        if (!signature.Success)
            return null;

        int cursor = signature.Index;
        int depth = 0;
        bool entered = false;
        for (; cursor < source.Length; cursor++)
        {
            char current = source[cursor];
            if (!entered && current == ';' && depth == 0)
                return source[signature.Index..cursor]; // 表达式体（含参数默认值前的 => 链）
            if (current == '{') { depth++; entered = true; }
            else if (current == '}')
            {
                depth--;
                if (entered && depth == 0)
                    return source[signature.Index..(cursor + 1)];
            }
        }
        return null;
    }

    /// <summary>拼接全部 DataSession partial 文件（ITM-405：单文件扫描曾漏 Bulk 入口）。</summary>
    private static string ReadAllDataSessionSources([CallerFilePath] string testPath = "")
    {
        string coreDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!, "..", "..", "src", "PalORM.Core"));
        string[] files = Directory.GetFiles(coreDir, "DataSession*.cs");
        if (files.Length < 2)
            throw new InvalidOperationException(
                $"Expected at least DataSession.cs and DataSession_Bulk.cs under {coreDir}, found {files.Length}.");
        return string.Join("\n", files.OrderBy(static f => f, StringComparer.Ordinal).Select(File.ReadAllText));
    }
}
