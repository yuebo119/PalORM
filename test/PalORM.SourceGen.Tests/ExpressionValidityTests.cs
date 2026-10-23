using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace PalORM.SourceGen.Tests;

/// <summary>2026-10-22 终审 P2-8：空串/纯空白 [Computed]/[DefaultValue] 表达式同时通过 NUL
/// 与括号平衡检查，会生成 "GENERATED ALWAYS AS ()" / "DEFAULT " 到迁移期才失败。</summary>
public sealed class ExpressionValidityTests
{
    [Test]
    public async Task PALORM044_EmptyComputedExpression_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Computed("")] public decimal Total { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM044")).IsTrue();
        await Assert.That(diagnostics.Single(d => d.Id == "PALORM044").Severity)
            .IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task PALORM048_WhitespaceDefaultValue_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [DefaultValue(" ")] public int Qty { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM048")).IsTrue();
        await Assert.That(diagnostics.Single(d => d.Id == "PALORM048").Severity)
            .IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task EmptyComputedExpression_GeneratorSkipsEntity()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Computed("")] public decimal Total { get; set; }
            }
            """;
        var result = GeneratorTestHost.RunGenerator(source, "EmptyComputedSkipConsumer");
        await Assert.That(result.GeneratedSources.Keys
            .Any(static key => key.StartsWith("RowFactory_", System.StringComparison.Ordinal))).IsFalse();
    }
}
