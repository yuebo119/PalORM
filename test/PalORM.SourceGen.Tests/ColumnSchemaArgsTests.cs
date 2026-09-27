using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// R3（v6.0）：[Column] 架构参数（Length/Precision/Scale/TypeName）的编译期契约——
/// PALORM017 停报（四参数已参与 DDL；StoreAs 仍报）、PALORM051 值域。
/// DDL 形态（VARCHAR(n)/DECIMAL(p,s)/TypeName 直通）由 SnapshotTests 的 FullFeatureEntity
/// （sku/weight/raw_kind 列）锁定。
/// </summary>
public sealed class ColumnSchemaArgsTests
{
    [Test]
    public async Task PALORM051_NegativeLength_Reports()
    {
        // 0 = 未设置（int? 属性在 v6.0 前是 CS0655 不可达面，改 int 后 0 为 sentinel 不报）
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Column("name", Length = -1)]
                public string Name { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM051")).IsTrue();
        await Assert.That(diagnostics.Single(d => d.Id == "PALORM051").Severity)
            .IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task PALORM051_ScaleGreaterThanPrecision_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Column("amount", Precision = 5, Scale = 8)]
                public decimal Amount { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM051")).IsTrue();
    }

    [Test]
    public async Task PALORM051_WhitespaceTypeName_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Column("name", TypeName = " ")]
                public string Name { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM051")).IsTrue();
    }

    [Test]
    public async Task PALORM051_ValidArgs_DoNotReport()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Column("name", Length = 128)]
                public string Name { get; set; } = "";
                [Column("amount", Precision = 10, Scale = 2)]
                public decimal Amount { get; set; }
                [Column("raw", TypeName = "JSONB")]
                public string Raw { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM051")).IsFalse();
    }

    [Test]
    public async Task PALORM017_ColumnSchemaArgs_StopReporting()
    {
        // v6.0 R3 起 Length/Precision/Scale/TypeName 参与 DDL——PALORM017 对四参数停报
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Column("name", Length = 128, Precision = 10, Scale = 2, TypeName = "TEXT")]
                public string Name { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM017")).IsFalse();
    }

    [Test]
    public async Task PALORM017_StoreAs_StillReports()
    {
        // ITM-553：StoreAs 涉及读写双路径，未实现——PALORM017 继续告警
        const string source = """
            using PalORM;
            public enum Kind { A, B }
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Column("kind", StoreAs = StoreAs.AsInt32)]
                public Kind Kind { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM017")).IsTrue();
    }
}
