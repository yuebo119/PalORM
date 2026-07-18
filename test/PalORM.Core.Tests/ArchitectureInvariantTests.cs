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
/// </summary>
public sealed class ArchitectureInvariantTests
{
    /// <summary>必须经过默认过滤路由的实体表入口（新增触表入口时在此登记）。</summary>
    private static readonly string[] _filteredEntryPoints =
    [
        "GetAsync",
        "GetAllAsync",
        "UpdateCoreAsync",   // UpdateAsync 的实现体
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
        ["BulkMergeAsync"] = "逐条委托 SaveCoreAsync（Insert/Update 语义）",
        ["SeedAsync"] = "委托 BulkMergeAsync",
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
                || body.Contains("GetDefaultFilterWhereClause", StringComparison.Ordinal);
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
                "UpdateAsync", "ValidateSchemaAsync", "DiffAsync", "MigrateAsync",
                "SavepointAsync", "RollbackToAsync", "ExecuteWithResilience",
                "BeginTransactionAsync", "WithTransaction", "HealthCheckAsync",
                "ForRead", "DisposeAsync", "QueryAsyncEnumerable", "QueryMultipleAsync",
                "CreateAsync",
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
