using System.Runtime.CompilerServices;

namespace PalORM.Core.Tests;

/// <summary>
/// 独立审计 2026-09-19 M2-3（A2）——<see cref="QueryBuilder{T}"/> 克隆完备性的机械守卫。
/// 背景：CloneForExecution 手工列举 17 个字段做复制（ctor 重建 10 项 + 初始化器直赋 7+ 项），
/// 该复制点已出过事故（r6-N1：_isolationLevel 漏传致条件分支死代码）。新增字段若忘记进克隆，
/// 编译器不报错（其余字段取默认值），静默产生行为分叉。
/// 本测试机械比对源码：**全部实例字段 = ctor 赋值集 ∪ 克隆体赋值集**——新增字段两者都不在即红。
/// （ArchitectureInvariantTests 同款源码扫描模式；QueryBuilder 是 struct 且热路径，反射/生成侧
/// 克隆方案被否决，守卫型测试是当前结构下的最优解。）
/// </summary>
public sealed class QueryBuilderCloneCompletenessTests
{
    // CallerFilePath 定位(同 ArchitectureInvariantTests)
    [Test]
    public async Task EveryInstanceField_IsCoveredByCtorOrClone()
    {
        string source = ReadQueryBuilderSource();
        var allFields = ExtractInstanceFields(source);
        var ctorFields = ExtractCtorAssignedFields(source);
        var cloneFields = ExtractCloneAssignedFields(source);

        await Assert.That(allFields.Count).IsGreaterThan(20); // 结构 sanity:字段全集非空

        var uncovered = allFields
            .Where(f => !ctorFields.Contains(f) && !cloneFields.Contains(f))
            .ToList();

        string uncoveredList = string.Join(",", uncovered);
        await Assert.That(uncoveredList).IsEmpty();
    }

    /// <summary>提取全部实例字段名（排除 static/const/readonly static）。</summary>
    private static HashSet<string> ExtractInstanceFields(string source)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.Trim();
            // 匹配 "internal/private [类型] _name" 形态（字段声明），排除 static
            if ((trimmed.StartsWith("internal ", StringComparison.Ordinal) || trimmed.StartsWith("private ", StringComparison.Ordinal)) &&
                !trimmed.Contains("static", StringComparison.Ordinal) && !trimmed.Contains('(', StringComparison.Ordinal))
            {
                int lastSpace = trimmed.LastIndexOf(' ', StringComparison.Ordinal);
                if (lastSpace > 0)
                {
                    string name = trimmed[(lastSpace + 1)..].TrimEnd(';', ' ', '=');
                    if (name.StartsWith('_', StringComparison.Ordinal))
                        fields.Add(name);
                }
            }
        }
        return fields;
    }

    /// <summary>ctor（与 QueryBuilderContext/Services 无关——只看本类 ctor 体）里被读的字段
    /// 视为"ctor 重建集"：克隆走同一 ctor，ctor 体内出现的 _field 赋值即覆盖。</summary>
    private static HashSet<string> ExtractCtorAssignedFields(string source)
    {
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        string? ctorBody = ExtractMethodBody(source, "internal QueryBuilder(QueryBuilderContext<T> ctx)");
        if (ctorBody is null) return assigned;
        foreach (string line in ctorBody.Split('\n'))
        {
            string t = line.Trim();
            // ctor 里 "_field = xxx;" 形态
            int eq = t.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && t.StartsWith('_', StringComparison.Ordinal))
            {
                string name = t[..eq].Trim();
                if (name.StartsWith('_', StringComparison.Ordinal))
                    assigned.Add(name);
            }
        }
        return assigned;
    }

    /// <summary>CloneForExecution 初始化器里 "_field = _field," 形态的字段。</summary>
    private static HashSet<string> ExtractCloneAssignedFields(string source)
    {
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        string? cloneBody = ExtractMethodBody(source, "internal QueryBuilder<T> CloneForExecution()");
        if (cloneBody is null) return assigned;
        foreach (string line in cloneBody.Split('\n'))
        {
            string t = line.Trim().TrimEnd(',');
            int eq = t.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && t.StartsWith('_', StringComparison.Ordinal))
            {
                string name = t[..eq].Trim();
                if (name.StartsWith('_', StringComparison.Ordinal))
                    assigned.Add(name);
            }
            // 克隆尾部的显式重置（_materializedClauses = null 等）也是赋值形态，同上已覆盖
        }
        return assigned;
    }

    private static string? ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        int braceStart = source.IndexOf('{', start, StringComparison.Ordinal);
        int depth = 0;
        for (int i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[(braceStart + 1)..i];
            }
        }
        return null;
    }

    private static string ReadQueryBuilderSource([CallerFilePath] string testPath = "")
    {
        // ArchitectureInvariantTests 同款定位：测试文件在 test/ 下，仓库根 = 上两级
        string repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "..", ".."));
        string path = Path.Combine(repoRoot, "src", "PalORM.Core", "QueryBuilder.cs");
        if (!File.Exists(path))
            throw new InvalidOperationException($"未找到 {path}——仓库结构变化需同步本测试的源码定位");
        return File.ReadAllText(path);
    }
}
