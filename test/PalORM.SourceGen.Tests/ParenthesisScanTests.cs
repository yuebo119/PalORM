using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PalORM.SourceGen.Tests;

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
    // ITM-888（r24）：未闭合 dollar-quote fail-closed——原实现吞到末尾仍返回 true，
    // 畸形 $tag$(1+1 因后续括号自抵消（或无更多括号）过平衡检查，坏 DDL 晚失败在迁移期。
    // 撤修复（恢复"吞到末尾 return true"）此两行变红。
    [Arguments("$tag$(1+1", false)]
    [Arguments("$$foo(", false)]
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
