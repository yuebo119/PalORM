using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PalORM.SourceGen.Tests;

/// <summary>PALORM002-005 与 PALORM017 诊断负向测试——验证触发条件且不级联生成错误。
/// PALORM006/007 当前无 Analyzer 报告点（006 由 SqlFileEmitter 的 Obsolete-error 机制承担），
/// 见 DiagnosticDescriptors_006And007_RemainRegisteredWithoutAnalyzerTrigger。</summary>
public sealed class AnalyzerDiagnosticsTests
{
    [Test]
    public async Task PropertyWithoutColumn_ReportsPalorm002_WithoutCascadingErrors()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM002")).IsTrue();
        await Assert.That(diagnostics.Single(d => d.Id == "PALORM002").Severity)
            .IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task PropertyNamedIdOrWithColumn_DoesNotReportPalorm002()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM002")).IsFalse();
    }

    [Test]
    public async Task ForeignKeyToUnknownTable_ReportsPalorm003_WithoutCascadingErrors()
    {
        const string source = """
            using PalORM;
            [Table("orders")]
            public sealed class Order
            {
                [Key] public long Id { get; set; }
                [Column("customer_id")]
                [ForeignKey("no_such_table", "id", OnDelete = DeleteAction.Cascade)]
                public long CustomerId { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM003")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task ForeignKeyToKnownTable_DoesNotReportPalorm003()
    {
        const string source = """
            using PalORM;
            [Table("customers")]
            public sealed class Customer { [Key] public long Id { get; set; } }
            [Table("orders")]
            public sealed class Order
            {
                [Key] public long Id { get; set; }
                [Column("customer_id")]
                [ForeignKey("customers", "id", OnDelete = DeleteAction.Cascade)]
                public long CustomerId { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM003")).IsFalse();
    }

    [Test]
    public async Task ForeignKeyWithoutOnDelete_ReportsPalorm004_WithoutCascadingErrors()
    {
        const string source = """
            using PalORM;
            [Table("customers")]
            public sealed class Customer { [Key] public long Id { get; set; } }
            [Table("orders")]
            public sealed class Order
            {
                [Key] public long Id { get; set; }
                [Column("customer_id")]
                [ForeignKey("customers", "id")]
                public long CustomerId { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM004")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task CompositePrimaryKey_ReportsPalorm019()
    {
        // ITM-311：复合主键的 BindDelete 单 key 语义无法表达，必须明确拒绝
        const string source = """
            using PalORM;
            [Table("order_lines")]
            public sealed class OrderLine
            {
                [Key] public long OrderId { get; set; }
                [Key] public long LineNo { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM019")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task OrmCallInsideLoop_ReportsPalorm005_WithoutCascadingErrors()
    {
        const string source = """
            using System.Collections.Generic;
            public sealed class Repo
            {
                public void LoadAll(List<long> ids)
                {
                    foreach (long id in ids)
                    {
                        _ = this.From<object>();
                    }
                }
                private object From<T>() => new();
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM005")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task OrmCallOutsideLoop_DoesNotReportPalorm005()
    {
        const string source = """
            public sealed class Repo
            {
                public void LoadOne()
                {
                    _ = this.From<object>();
                }
                private object From<T>() => new();
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM005")).IsFalse();
    }

    [Test]
    public async Task IndexAndUnique_AfterAdrB_DoNotReportPalorm017()
    {
        // ADR-B 后 [Index]/[Unique] 参与索引 DDL 生成，PALORM017 停报
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("idx_name", "name")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")]
                [Unique]
                public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM017")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task DefaultValueAndColumnSchemaArgs_ReportPalorm017()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name", Length = 128)]
                public string Name { get; set; } = "";
                [Column("created_at")]
                [DefaultValue("NOW()")]
                public string CreatedAt { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        int count = diagnostics.Count(d => d.Id == "PALORM017");
        // [Column(Length=…)] + [DefaultValue] = 2 处独立告警（[Unique] 已由 ADR-B 落地停报）
        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task PlainEntity_DoesNotReportPalorm017()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM017")).IsFalse();
    }

    [Test]
    public async Task DiagnosticDescriptors_006And007_RemainRegisteredWithoutAnalyzerTrigger()
    {
        // 现状固化：PALORM006（SqlFile 缺失）由 SqlFileEmitter 的 Obsolete-error 机制承担，
        // PALORM007（列类型不匹配）无运行时 schema 对照数据源——两者在 Analyzer 中仅有描述符。
        // 若未来接入报告点，此测试提醒同步补触发测试。
        var analyzer = new PalORMAnalyzer();

        await Assert.That(analyzer.SupportedDiagnostics.Any(d => d.Id == "PALORM006")).IsTrue();
        await Assert.That(analyzer.SupportedDiagnostics.Any(d => d.Id == "PALORM007")).IsTrue();
    }

    private static async Task<(ImmutableArray<Diagnostic> AnalyzerDiagnostics, ImmutableArray<Diagnostic> CompileErrors)>
        AnalyzeAsync(string source)
    {
        string[] trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var references = trustedAssemblies
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "AnalyzerDiagnosticsConsumer",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ImmutableArray<Diagnostic> analyzerDiagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        ImmutableArray<Diagnostic> compileErrors = [.. compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)];
        return (analyzerDiagnostics, compileErrors);
    }
}
