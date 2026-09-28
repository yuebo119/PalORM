using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// ITM-874（r23）：索引名长度上限 PALORM052——派生名 ux_{表}_{列} 与显式 [Index] 名
/// 超过最严方言上限（PG 63 / MySQL 64）时编译期 Warning（不自动截断，防撞名；
/// 迁移期 1059 类错误的提前拦截面）。
/// </summary>
public sealed class IndexNameLengthTests
{
    private const string LongTable = "very_long_table_name_padding_padding_padding_padding";
    private const string LongColumn = "extremely_long_column_name_padding_padding_padding";

    [Test]
    public async Task PALORM052_DerivedUniqueNameTooLong_Reports()
    {
        string source = $$"""
            using PalORM;
            [Table("{{LongTable}}")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Unique]
                public string {{LongColumn}} { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM052")).IsTrue();
        await Assert.That(diagnostics.Single(d => d.Id == "PALORM052").Severity)
            .IsEqualTo(DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task PALORM052_ExplicitIndexNameTooLong_Reports()
    {
        string longIndexName = new('x', 64);
        string source = $$"""
            using PalORM;
            [Table("t")]
            [Index("{{longIndexName}}", "Name")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                public string Name { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM052")).IsTrue();
    }

    [Test]
    public async Task PALORM052_AtLimitNames_DoNotReport()
    {
        // 派生名恰好 63 字符不报——上限是"超过"而非"达到"；前提长度运行时断言钉住
        const string table = "tbl_63";
        string column = new('c', 53);   // 3 + 6 + 1 + 53 = 63
        await Assert.That($"ux_{table}_{column}".Length).IsEqualTo(63);
        string source = $$"""
            using PalORM;
            [Table("{{table}}")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Unique]
                public string {{column}} { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM052")).IsFalse();
    }
}
