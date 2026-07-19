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

    // ─── PALORM020：索引声明有效性（ITM-203）───

    [Test]
    public async Task EmptyColumnsIndex_ReportsPalorm020_WithoutCascadingErrors()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("idx_orphan")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM020")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task DuplicateIndexName_SameEntity_ReportsPalorm020()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("idx_x", "name")]
            [Index("idx_x", "value")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("value")] public long Value { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Count(d => d.Id == "PALORM020")).IsEqualTo(1);
    }

    [Test]
    public async Task ExplicitIndexCollidingWithDerivedUniqueName_ReportsPalorm020()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            [Index("ux_t_code", "name")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("code")]
                [Unique]
                public string Code { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        // [Unique] 派生 ux_t_code 与显式 [Index("ux_t_code")] 冲突
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM020")).IsTrue();
    }

    [Test]
    public async Task ValidIndexDeclarations_DoNotReportPalorm020()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("idx_name", "name")]
            [Index("idx_value", "value")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("value")] public long Value { get; set; }
                [Column("code")]
                [Unique]
                public string Code { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM020")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    // ─── PALORM021：列名唯一性（ITM-409）───

    [Test]
    public async Task DuplicateColumnName_ReportsPalorm021()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("name")] public string DisplayName { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Count(d => d.Id == "PALORM021")).IsEqualTo(1);
    }

    [Test]
    public async Task DuplicateColumnName_CaseInsensitive_ReportsPalorm021()
    {
        // 三方言标识符默认大小写不敏感（PG 折叠小写/MySQL 依 OS/SQLite 不敏感）——统一按不敏感判定
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("Name")] public string Name { get; set; } = "";
                [Column("name")] public string DisplayName { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM021")).IsTrue();
    }

    [Test]
    public async Task DistinctColumnNames_DoNotReportPalorm021()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("display_name")] public string DisplayName { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM021")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    // ITM-510：索引名大小写碰撞（MySQL 大小写不敏感）——ix_Foo/ix_foo 应报 PALORM020。
    [Test]
    public async Task IndexNames_DifferingOnlyByCase_ReportsPalorm020()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("ix_Foo", "name")]
            [Index("ix_foo", "value")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("value")] public long Value { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Count(d => d.Id == "PALORM020")).IsEqualTo(1);
    }

    // ITM-512：命名空间校验——非 PalORM 命名空间的同名注解不得被当 PalORM 注解处理。
    [Test]
    public async Task NonPalORMTableAttribute_DoesNotTriggerDiagnostics()
    {
        const string source = """
            namespace Other
            {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class TableAttribute : System.Attribute
                {
                    public TableAttribute(string name) { }
                }
            }
            namespace Consumer
            {
                // 挂的是 Other.TableAttribute（非 PalORM）——不应触发 PALORM001（缺主键）等
                [Other.Table("plain")]
                public sealed class PlainPoco
                {
                    public long Id { get; set; }
                    public string Name { get; set; } = "";
                }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id.StartsWith("PALORM", System.StringComparison.Ordinal))).IsFalse();
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
