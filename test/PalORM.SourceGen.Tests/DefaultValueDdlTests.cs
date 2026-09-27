using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// R2（v6.0）：[DefaultValue] DDL 落地的编译期契约——PALORM017 停报、PALORM047 互斥三态、
/// PALORM048 表达式快检。生成物层的 DEFAULT 子句形态由 SnapshotTests 的 FullFeatureEntity
/// （tier 列）锁定；真库生效由 Integration.Tests 的 MigrationDefaultTests 验证。
/// </summary>
public sealed class DefaultValueDdlTests
{
    [Test]
    public async Task PALORM017_DefaultValue_StopsReporting()
    {
        // v6.0 R2 起 [DefaultValue] 参与列 DEFAULT 子句——PALORM017（标注但静默无效）停报
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [DefaultValue("'pending'")]
                public string Status { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM017")).IsFalse();
    }

    [Test]
    public async Task PALORM047_DefaultValueWithComputed_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [DefaultValue("0")]
                [Computed("id * 2")]
                public long Doubled { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM047")).IsTrue();
        await Assert.That(diagnostics.Single(d => d.Id == "PALORM047").Severity)
            .IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task PALORM047_DefaultValueWithTimestamp_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [DefaultValue("now()")]
                [Timestamp]
                public System.DateTime Updated { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM047")).IsTrue();
    }

    [Test]
    public async Task PALORM047_DefaultValueOnAutoIncrementKey_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key]
                [DefaultValue("42")]
                public long Id { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM047")).IsTrue();
    }

    [Test]
    public async Task PALORM047_DefaultValueOnNonAutoIncrementKey_DoesNotReport()
    {
        // [Key(AutoIncrement = false)]（雪花 ID 等应用侧赋值主键）与 Guid 主键允许 DEFAULT——
        // 应用侧默认值主键是合法用法（如 PG gen_random_uuid()）
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key(AutoIncrement = false)]
                [DefaultValue("42")]
                public long Id { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM047")).IsFalse();
    }

    [Test]
    public async Task PALORM047_DefaultValueOnGuidKey_DoesNotReport()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key]
                [DefaultValue("gen_random_uuid()")]
                public System.Guid Id { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM047")).IsFalse();
    }

    [Test]
    public async Task PALORM048_DefaultValueWithUnbalancedParentheses_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [DefaultValue("coalesce(status, 'new'")]
                public string Status { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM048")).IsTrue();
        await Assert.That(diagnostics.Single(d => d.Id == "PALORM048").Severity)
            .IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task PALORM048_DefaultValueWithParenthesesInsideStringLiteral_DoesNotReport()
    {
        // R11 词法扫描语义：单引号字符串内的括号不计入配对（与 PALORM044 同一判定真源）
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [DefaultValue("coalesce(status, ')(')")]
                public string Status { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM048")).IsFalse();
    }
}
