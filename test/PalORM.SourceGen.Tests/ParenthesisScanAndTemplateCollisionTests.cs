using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PalORM.SourceGen.Tests;

/// <summary>ITM-753(r21)：IsBalancedParentheses 方言词法覆盖。
/// <para>背景：ITM-741(r20) 重写扫描器后仍漏三类方言形态——MySQL 反斜杠转义单引号、
/// 方括号标识符（SQLite/T-SQL）、PG dollar-quoting。三者被当作未闭合区间吞掉后续括号，
/// 合法 [Computed] 表达式被判不平衡 → PALORM044 Error 误拒。</para></summary>
public sealed class ParenthesisScanTests
{
    [Test]
    [Arguments("COALESCE(a, b)", true)]
    [Arguments("LOWER(')x')", true)]
    [Arguments("('a' || ')')", true)]
    [Arguments("COALESCE(\"a)b\", 0)", true)]
    [Arguments("-- c )\n(1+1)", true)]
    [Arguments("COALESCE(a, b", false)]
    [Arguments("COALESCE(a, b))", false)]
    // ITM-753 新增三类方言形态
    [Arguments("CONCAT('it\\'s (x)', col)", true)]
    [Arguments("[we(ird] + 1", true)]
    [Arguments("$$a(b$$ + 1", true)]
    [Arguments("$tag$a(b$tag$ + 1", true)]
    public async Task IsBalancedParentheses_HandlesDialectLexemes(string expression, bool expected)
    {
        await Assert.That(
            SourceGenerationValidation.IsBalancedParentheses(expression)).IsEqualTo(expected);
    }

    [Test]
    public async Task PALORM044_MySqlEscapedQuote_DoesNotReport()
    {
        // ITM-753：MySQL 反斜杠转义单引号里的括号不得被计入配对
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Computed("CONCAT('it\\'s (x)', code)")]
                public string Slug { get; set; } = "";
            }
            """;
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "ParenScan");
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
    }
}

/// <summary>ITM-754(r21)：[SqlTemplate] 生成类名 SqlTemplates 与同命名空间既有类型冲突时须报 PALORM046
/// （否则 partial 声明冲突以 CS0260 落在 .g.cs，违反 ITM-573 家族"错误不得指向 .g.cs"）。</summary>
public sealed class SqlTemplateNamespaceCollisionTests
{
    [Test]
    public async Task ExistingNonPartialSqlTemplatesClass_ReportsPalorm046()
    {
        // 冲突成立条件：既有类型与生成类**同命名空间**（生成物落 model.Namespace）。
        // global namespace 下生成物落 PalORM.Generated，不冲突（另有用例覆盖）。
        const string source = """
            using PalORM;
            namespace Models
            {
                public class SqlTemplates { }
                public static class C
                {
                    [SqlTemplate("Q")]
                    public static System.FormattableString Q() => $"SELECT 1";
                }
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplatesClash");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM046")).IsTrue();
        await Assert.That(result.GeneratedSources.Any(pair =>
            pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ExistingPartialSqlTemplatesClass_DoesNotReport()
    {
        // 对照：既有同名类型是 partial（生成物将与之合并）——不应报
        const string source = """
            using PalORM;
            namespace Models
            {
                public static partial class SqlTemplates { }
                public static class C
                {
                    [SqlTemplate("Q")]
                    public static System.FormattableString Q() => $"SELECT 1";
                }
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplatesPartial");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM046")).IsFalse();
    }

    [Test]
    public async Task GlobalNamespaceSqlTemplatesClass_DoesNotConflict()
    {
        // 边界锁定：global namespace 时生成物落 PalORM.Generated，与 global 的 SqlTemplates
        // 不同命名空间——不报 PALORM046 且生成物可编译（澄清"global 也算冲突"的误判）
        const string source = """
            using PalORM;
            public class SqlTemplates { }
            public static class C
            {
                [SqlTemplate("Q")]
                public static System.FormattableString Q() => $"SELECT 1";
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplatesGlobal");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM046")).IsFalse();
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
    }
}
