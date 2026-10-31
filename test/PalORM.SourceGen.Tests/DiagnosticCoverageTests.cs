using System.Runtime.CompilerServices;

namespace PalORM.SourceGen.Tests;

// P1-D（方案 §6.7 / CONTRACT-2）：诊断逐码守卫。
// 从 SourceGen 源码里的 PALORMxxx 字面量枚举全部诊断 ID（分析器描述符 + 生成器侧描述符），
// 映射到测试引用；缺失即失败。豁免必须写理由（当前为空）。
// 判别力自检：FindUncovered 对合成输入能报出缺失 ID——守卫自身不是 no-op。

internal static class DiagnosticCoverage
{
    /// <summary>返回既未在测试中引用、也未被豁免的诊断 ID。</summary>
    public static IReadOnlyList<string> FindUncovered(
        IEnumerable<string> diagnosticIds,
        IReadOnlySet<string> testedIds,
        IReadOnlyDictionary<string, string> exemptions)
    {
        List<string> uncovered = [];
        foreach (string id in diagnosticIds.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal))
        {
            if (testedIds.Contains(id)) continue;
            if (exemptions.ContainsKey(id)) continue;
            uncovered.Add(id);
        }
        return uncovered;
    }
}

public sealed class DiagnosticCoverageTests
{
    /// <summary>豁免表：ID → 理由。当前为空（46/46 已覆盖）。</summary>
    private static readonly IReadOnlyDictionary<string, string> Exemptions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Test]
    public async Task AllDiagnosticIds_HaveTestReference()
    {
        string root = RepoRoot();
        HashSet<string> diagnosticIds = CollectDiagnosticIds(root);
        HashSet<string> testedIds = CollectTestedIds(root);

        List<string> uncovered = [.. DiagnosticCoverage.FindUncovered(diagnosticIds, testedIds, Exemptions)];
        await Assert.That(diagnosticIds.Count).IsGreaterThanOrEqualTo(46)
            .Because($"诊断 ID 地板 46，实际 {diagnosticIds.Count}——源码扫描可能失效");
        await Assert.That(string.Join(",", uncovered)).IsEmpty()
            .Because("每个诊断 ID 必须有触发/不触发用例；新增诊断未配测试时本守卫报红");
    }

    [Test]
    public async Task Exemptions_HaveReasons()
    {
        List<string> problems = [.. Exemptions.Where(static pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(static pair => pair.Key)];
        await Assert.That(string.Join(",", problems)).IsEmpty();
    }

    [Test]
    public async Task Guard_ReportsSyntheticMissingId()
    {
        // 判别力自检：合成"有 46 个 ID、测试缺一个新 ID"的输入，守卫必须报出它
        HashSet<string> ids = [.. Enumerable.Range(1, 46).Select(i => $"PALORM{i:D3}")];
        HashSet<string> tested = [.. ids.Where(static id => id != "PALORM046")];
        List<string> uncovered = [.. DiagnosticCoverage.FindUncovered(ids, tested, Exemptions)];
        await Assert.That(string.Join(",", uncovered)).IsEqualTo("PALORM046");
    }

    private static string RepoRoot([CallerFilePath] string testPath = "")
    {
        string? explicitRoot = Environment.GetEnvironmentVariable("PALORM_REPO_ROOT");
        if (!string.IsNullOrEmpty(explicitRoot) && File.Exists(Path.Combine(explicitRoot, "PalORM.slnx")))
            return explicitRoot;
        string dir = Path.GetDirectoryName(testPath) ?? AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "PalORM.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
        throw new InvalidOperationException($"无法定位仓库根（起点 {testPath}）");
    }

    /// <summary>源码侧 ID：只认双引号字符串字面量里的 PALORMxxx（描述符 id 的写法）。
    /// 注释中的提及不计，避免"只在注释里提到"被当成规则存在。</summary>
    private static HashSet<string> CollectDiagnosticIds(string root)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        string sourceDir = Path.Combine(root, "src", "PalORM.SourceGen");
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string line in File.ReadLines(file))
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                string code = comment >= 0 ? line[..comment] : line;
                foreach (string id in ExtractIds(code))
                    ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>测试侧引用：剥离注释后，出现 PALORMxxx 即视为有测试引用。</summary>
    private static HashSet<string> CollectTestedIds(string root)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (string project in (string[])["PalORM.SourceGen.Tests", "PalORM.Core.Tests"])
        {
            string dir = Path.Combine(root, "test", project);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }
                string text = StripComments(File.ReadAllText(file));
                foreach (string id in ExtractIds(text))
                    ids.Add(id);
            }
        }
        return ids;
    }

    private static IEnumerable<string> ExtractIds(string text)
    {
        for (int i = 0; i < text.Length - 8; i++)
        {
            if (!text.AsSpan(i).StartsWith("PALORM", StringComparison.Ordinal)) continue;
            ReadOnlySpan<char> tail = text.AsSpan(i + 6, 3);
            bool digits = true;
            foreach (char c in tail) digits &= char.IsAsciiDigit(c);
            if (digits) yield return text.Substring(i, 9);
        }
    }

    private static string StripComments(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            builder.Append(comment >= 0 ? line[..comment] : line).Append('\n');
        }
        return builder.ToString();
    }
}