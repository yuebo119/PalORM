using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PalORM.SourceGen.Tests;

internal sealed class GeneratorPhase2Tests
{
    [Test]
    public async Task ExternalConsumer_CompilesWithoutImplicitUsings_AndSupportsSameNamedEntities()
    {
        const string source = """
            using PalORM;

            public readonly record struct Ulid(string Value);

            public sealed class UlidConverter : IValueConverter<Ulid, string>
            {
                public string ToProvider(Ulid value) => value.Value;
                public Ulid FromProvider(string value) => new(value);
            }

            [Table("root_users")]
            public sealed partial class RootUser
            {
                [Key] public long Id { get; set; }
                [Column("created_at")] public System.DateTimeOffset CreatedAt { get; set; }
            }

            namespace Alpha
            {
                [Table("alpha_users")]
                public sealed partial class User
                {
                    [Key] public long Id { get; set; }
                    [Column("created_at")] public System.DateTimeOffset CreatedAt { get; set; }
                    [Column("updated_at")] public System.DateTimeOffset UpdatedAt { get; set; }
                    [Column("external_id")]
                    [Converter(typeof(global::UlidConverter))]
                    public global::Ulid ExternalId { get; set; }
                    [Column("secondary_id")]
                    [Converter(typeof(global::UlidConverter))]
                    public global::Ulid SecondaryId { get; set; }
                }
            }

            namespace Beta
            {
                [Table("beta_users")]
                public sealed partial class User
                {
                    [Key] public long Id { get; set; }
                    [Column("created_at")] public System.DateTimeOffset CreatedAt { get; set; }
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string errors = FormatErrors(result.OutputCompilation);
        string generated = string.Join("\n", result.GeneratedSources.Values);
        string[] helperNames =
        [
            .. result.GeneratedSources.Values
                .SelectMany(static text => CSharpSyntaxTree.ParseText(text).GetRoot()
                    .DescendantNodes().OfType<ClassDeclarationSyntax>())
                .Select(static declaration => declaration.Identifier.ValueText)
                .Where(static name => name.StartsWith("RowFactory_", StringComparison.Ordinal)
                    || name.StartsWith("CommandFactory_", StringComparison.Ordinal)
                    || name.StartsWith("Migration_", StringComparison.Ordinal)
                    || name.StartsWith("TypeMapper_", StringComparison.Ordinal))
        ];

        await Assert.That(errors).IsEmpty();
        await Assert.That(generated).Contains("global::RootUser");
        await Assert.That(generated).Contains("global::Alpha.User");
        await Assert.That(generated).Contains("global::Beta.User");
        await Assert.That(helperNames.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(helperNames.Length);
    }

    [Test]
    public async Task InsertMetadata_UsesOneColumnPredicateAndOrder()
    {
        const string source = """
            using PalORM;

            namespace Models
            {
                [Table("items")]
                public sealed partial class Item
                {
                    [Key] public long Id { get; set; }
                    [Column("name")] public string Name { get; set; } = "";
                    [Column("ignored")]
                    [IgnoreOnInsert]
                    public string Ignored { get; set; } = "";
                    [Column("timestamp")]
                    [Timestamp]
                    public long Timestamp { get; set; }
                    [Column("computed")]
                    [Computed("name || '-computed'")]
                    public string Computed { get; set; } = "";
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string errors = FormatErrors(result.OutputCompilation);
        string commandFactory = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("CommandFactory_", StringComparison.Ordinal)).Value;
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];
        string bindInsert = ExtractMethod(commandFactory, "BindInsertToBatch(", "BindInsertValues(");

        await Assert.That(errors).IsEmpty();
        await Assert.That(commandFactory).Contains(
            "InsertSql = \"INSERT INTO items (name) VALUES (@p0)\"");
        await Assert.That(bindInsert).Contains("entity.Name");
        await Assert.That(bindInsert).DoesNotContain("entity.Ignored");
        await Assert.That(bindInsert).DoesNotContain("entity.Timestamp");
        await Assert.That(bindInsert).DoesNotContain("entity.Computed");
        // v4.6：BindInsertValues 是参数复用路径，已重新引入
        await Assert.That(commandFactory).Contains("BindInsertValues");
        await Assert.That(registry).Contains("new[] { \"name\" }");
    }

    [Test]
    public async Task InvalidOwnedJson_ReportsOnlyPalormDiagnostic_AndIsNotGenerated()
    {
        const string source = """
            using PalORM;

            [Table("invalid_json")]
            public sealed partial class InvalidJsonEntity
            {
                [Key] public long Id { get; set; }
                [Column("payload")]
                [OwnedJson]
                public Payload Payload { get; set; } = new();
            }

            public sealed class Payload
            {
                public string Value { get; set; } = "";
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);
        string generated = string.Join("\n", result.GeneratedSources.Values);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM008"]);
        await Assert.That(generated).DoesNotContain("InvalidJsonEntity");
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task ComputedColumn_GeneratesStoredExpression()
    {
        const string source = """
            using PalORM;

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("computed")]
                [Computed("\"name\" || '-computed'")]
                public string Computed { get; set; } = "";
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string migration = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("Migration_", StringComparison.Ordinal)).Value;

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(migration).Contains(
            "\\\"computed\\\" TEXT GENERATED ALWAYS AS (\\\"name\\\" || '-computed') STORED");
    }

    [Test]
    public async Task Converter_UsesProviderTypeForDdlBindingAndMaterialization()
    {
        const string source = """
            using PalORM;

            public readonly record struct Ulid(string Value);

            public sealed class UlidConverter : IValueConverter<Ulid, string>
            {
                public string ToProvider(Ulid value) => value.Value;
                public Ulid FromProvider(string value) => new(value);
            }

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("external_id")]
                [Converter(typeof(UlidConverter))]
                public Ulid ExternalId { get; set; }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string generated = string.Join("\n", result.GeneratedSources.Values);
        string commandFactory = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("CommandFactory_", StringComparison.Ordinal)).Value;
        string rowFactory = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("RowFactory_", StringComparison.Ordinal)).Value;
        string migration = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("Migration_", StringComparison.Ordinal)).Value;

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        // v4.0 优化 A：CommandFactory emit 从 `new Converter()` 内联改为类级 static readonly 单例字段，
        // 与 RowFactory 模式对齐（百万行 BulkInsert 含 Converter 列省 ~N 万 Gen0 分配）。
        await Assert.That(commandFactory).Contains(
            "IValueConverter<global::Ulid, string> _conv_ExternalId = new global::UlidConverter()");
        await Assert.That(commandFactory).Contains("_conv_ExternalId.ToProvider(entity.ExternalId)");
        await Assert.That(commandFactory).DoesNotContain("new global::UlidConverter()).ToProvider(entity");
        // v3.1: RowFactory emit 从 `new Converter()` 内联改为类级 static readonly 单例字段。
        // 断言 emit 同时含：(1) 类级 _conv_<prop> 字段声明；(2) Read lambda 内引用字段调用 FromProvider。
        await Assert.That(rowFactory).Contains(
            "IValueConverter<global::Ulid, string> _conv_ExternalId = new global::UlidConverter()");
        await Assert.That(rowFactory).Contains("_conv_ExternalId!.FromProvider(r.GetString(1))");
        await Assert.That(rowFactory).DoesNotContain("new global::UlidConverter()).FromProvider");
        await Assert.That(migration).Contains("\\\"external_id\\\" TEXT");
        await Assert.That(generated).DoesNotContain("ReadUlidFromBlob");
        await Assert.That(generated).DoesNotContain("ReadGuidFromBlob");
    }

    [Test]
    public async Task UlidWithoutConverter_ReportsPalorm016_AndIsNotGenerated()
    {
        const string source = """
            using PalORM;

            public readonly struct Ulid;

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("external_id")] public Ulid ExternalId { get; set; }
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);
        string generated = string.Join("\n", result.GeneratedSources.Values);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM016"]);
        await Assert.That(generated).DoesNotContain("global::Item");
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task InvalidAndMissingConverters_ReportPalorm016_AndAreNotGenerated()
    {
        const string source = """
            using PalORM;

            public readonly struct Ulid;

            public sealed class WrongConverter : IValueConverter<string, byte[]>
            {
                public byte[] ToProvider(string value) => [];
                public string FromProvider(byte[] value) => "";
            }

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("optional_id")] public Ulid? OptionalId { get; set; }
                [Column("external_id")]
                [Converter(typeof(WrongConverter))]
                public Ulid ExternalId { get; set; }
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);
        string generated = string.Join("\n", result.GeneratedSources.Values);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM016", "PALORM016"]);
        await Assert.That(generated).DoesNotContain("global::Item");
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task NotMappedUlid_DoesNotBlockEntityGeneration()
    {
        const string source = """
            using PalORM;

            public readonly struct Ulid;

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [NotMapped] public Ulid TransientId { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);
        string generated = string.Join("\n", result.GeneratedSources.Values);

        // PALORM024 不应触发（加了 Name 列后实体有可更新列）；PALORM016 不应触发（NotMapped 排除了 Ulid）
        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            .IsEmpty();
        await Assert.That(generated).Contains("global::Item");
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task ConverterPrimaryKey_BindDeleteUsesProviderValue()
    {
        const string source = """
            using PalORM;

            public readonly record struct Ulid(string Value);

            public sealed class UlidConverter : IValueConverter<Ulid, string>
            {
                public string ToProvider(Ulid value) => value.Value;
                public Ulid FromProvider(string value) => new(value);
            }

            [Table("items")]
            public sealed partial class Item
            {
                [Key]
                [Converter(typeof(UlidConverter))]
                public Ulid Id { get; set; }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string commandFactory = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("CommandFactory_", StringComparison.Ordinal)).Value;
        string bindDelete = ExtractMethod(commandFactory, "BindDelete(", "SetId(");

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        // v4.0 优化 A：BindDelete 也走单例字段 _conv_<prop>，不再每次 new Converter。
        await Assert.That(bindDelete).Contains("_conv_Id.ToProvider((global::Ulid)key)");
        await Assert.That(bindDelete).DoesNotContain("new global::UlidConverter()).ToProvider((global::Ulid)key)");
    }

    [Test]
    public async Task ExternalConverterWithInternalConstructor_ReportsPalorm016()
    {
        const string converterSource = """
            using PalORM;

            public readonly record struct ExternalId(string Value);

            public sealed class ExternalConverter
                : IValueConverter<ExternalId, string>
            {
                internal ExternalConverter() { }
                public string ToProvider(ExternalId value) => value.Value;
                public ExternalId FromProvider(string value) => new(value);
            }
            """;
        CSharpCompilation converterCompilation = CreateCompilation(
            converterSource,
            assemblyName: "ExternalConverters");
        using var image = new MemoryStream();
        await Assert.That(converterCompilation.Emit(image).Success).IsTrue();
        MetadataReference converterReference =
            MetadataReference.CreateFromImage(image.ToArray());

        const string consumerSource = """
            using PalORM;

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("external_id")]
                [Converter(typeof(ExternalConverter))]
                public ExternalId ExternalId { get; set; }
            }
            """;
        CSharpCompilation consumerCompilation = CreateCompilation(
            consumerSource,
            [converterReference]);
        ImmutableArray<Diagnostic> diagnostics = await consumerCompilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(consumerCompilation);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM016"]);
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task UnknownGuidLikeType_ReportsPalorm016_WithoutGeneratedErrors()
    {
        const string source = """
            using PalORM;

            public readonly struct OrderGuid;

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("order_guid")] public OrderGuid OrderGuid { get; set; }
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM016"]);
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task NullablePrimitive_UsesUnderlyingProviderType()
    {
        const string source = """
            using PalORM;

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("optional_count")] public int? OptionalCount { get; set; }
                [Column("small_value")] public short SmallValue { get; set; }
                [Column("byte_value")] public byte ByteValue { get; set; }
                [Column("character")] public char Character { get; set; }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string migration = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("Migration_", StringComparison.Ordinal)).Value;
        string rowFactory = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("RowFactory_", StringComparison.Ordinal)).Value;

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(migration).Contains("\\\"optional_count\\\" INTEGER");
        await Assert.That(migration).Contains("\\\"small_value\\\" SMALLINT");
        await Assert.That(migration).Contains("\\\"byte_value\\\" SMALLINT");
        await Assert.That(migration).Contains("\\\"character\\\" TEXT");
        await Assert.That(rowFactory).Contains(
            "r.IsDBNull(1) ? null : r.GetInt32(1)");
    }

    [Test]
    public async Task NullableProviderConverter_ReportsPalorm016_WithoutGeneration()
    {
        const string source = """
            using PalORM;

            public readonly record struct ExternalId(long Value);

            public sealed class NullableProviderConverter
                : IValueConverter<ExternalId, long?>
            {
                public long? ToProvider(ExternalId value) => value.Value;
                public ExternalId FromProvider(long? value) => new(value ?? 0);
            }

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("external_id")]
                [Converter(typeof(NullableProviderConverter))]
                public ExternalId ExternalId { get; set; }
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);
        string generated = string.Join("\n", result.GeneratedSources.Values);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM016"]);
        await Assert.That(generated).DoesNotContain("global::Item");
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task GenericAndNestedEntities_ReportPalorm015_AndAreNotGenerated()
    {
        const string source = """
            using PalORM;

            [Table("generic_entities")]
            public sealed partial class GenericEntity<T>
            {
            }

            public static class Container
            {
                [Table("nested_entities")]
                public sealed partial class NestedEntity
                {
                }
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);
        string generated = string.Join("\n", result.GeneratedSources.Values);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM015", "PALORM015"]);
        await Assert.That(generated).DoesNotContain("GenericEntity");
        await Assert.That(generated).DoesNotContain("NestedEntity");
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task UnsupportedPortableProviderTypes_ReportPalorm016()
    {
        const string source = """
            using PalORM;

            public readonly record struct BinaryId(byte[] Value);

            public sealed class BinaryIdConverter
                : IValueConverter<BinaryId, byte[]>
            {
                public byte[] ToProvider(BinaryId value) => value.Value;
                public BinaryId FromProvider(byte[] value) => new(value);
            }

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("payload")] public byte[] Payload { get; set; } = [];
                [Column("duration")] public System.TimeSpan Duration { get; set; }
                [Column("external_id")]
                [Converter(typeof(BinaryIdConverter))]
                public BinaryId ExternalId { get; set; }
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM016", "PALORM016", "PALORM016"]);
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task NullableReferenceProviderConverter_ReportsPalorm016()
    {
        const string source = """
            #nullable enable
            using PalORM;

            public readonly record struct ExternalId(string Value);

            public sealed class NullableStringConverter
                : IValueConverter<ExternalId, string?>
            {
                public string? ToProvider(ExternalId value) => value.Value;
                public ExternalId FromProvider(string? value) => new(value ?? "");
            }

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("external_id")]
                [Converter(typeof(NullableStringConverter))]
                public ExternalId ExternalId { get; set; }
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM016"]);
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task ExplicitInterfaceConverter_GeneratesCompilableInterfaceCalls()
    {
        const string source = """
            using PalORM;

            public readonly record struct ExternalId(string Value);

            public sealed class ExplicitConverter
                : IValueConverter<ExternalId, string>
            {
                string IValueConverter<ExternalId, string>.ToProvider(ExternalId value)
                    => value.Value;

                ExternalId IValueConverter<ExternalId, string>.FromProvider(string value)
                    => new(value);
            }

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("external_id")]
                [Converter(typeof(ExplicitConverter))]
                public ExternalId ExternalId { get; set; }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string generated = string.Join("\n", result.GeneratedSources.Values);

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        // v4.0 优化 A：CommandFactory 也走单例字段——显式接口实现仍可通过类级字段调用。
        await Assert.That(generated).Contains(
            "IValueConverter<global::ExternalId, string> _conv_ExternalId = new global::ExplicitConverter()");
        await Assert.That(generated).Contains("_conv_ExternalId.ToProvider(entity.ExternalId)");
    }

    [Test]
    public async Task NotMappedProperties_DoNotSatisfyMappedEntityContracts()
    {
        const string source = """
            using PalORM;

            [SoftDelete]
            [Table("items")]
            public sealed partial class Item
            {
                [NotMapped]
                [Key]
                public long Id { get; set; }

                [NotMapped]
                [Column("deleted_at")]
                public string? DeletedAt { get; set; }

                [NotMapped]
                [ConcurrencyCheck]
                public string Version { get; set; } = "";

                [NotMapped]
                [OwnedJson]
                public object Payload { get; set; } = new();

                [NotMapped]
                [ForeignKey("missing", "id")]
                public long ParentId { get; set; }
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);

        // ITM-587/590 后 PALORM001/014 触发顺序变化（分析器注册结构改为 CompilationStartAction），
        // 用 Contains 替代 IsEquivalentTo 避免对执行顺序敏感——语义是"两者都触发"。
        // v5.0 PALORM026：[NotMapped]+映射特性互斥——此测试故意构造 4 个冲突属性，每个触发 PALORM026。
        // 不再断言 errorIds.Count（随 PALORM026 触发数变化，测试语义改为"两个核心规则都触发"）。
        var errorIds = diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id)
            .ToList();
        await Assert.That(errorIds).Contains("PALORM001");
        await Assert.That(errorIds).Contains("PALORM014");
        // v5.0：[NotMapped]+映射特性冲突应触发 PALORM026（至少 4 次——Column/ConcurrencyCheck/OwnedJson/ForeignKey）
        await Assert.That(errorIds.Count(id => id == "PALORM026")).IsGreaterThanOrEqualTo(4);
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task OwnedJsonWithConverter_ReportsPalorm016_WithoutGeneration()
    {
        const string source = """
            using PalORM;

            public sealed class PayloadConverter
                : IValueConverter<string, string>
            {
                public string ToProvider(string value) => value;
                public string FromProvider(string value) => value;
            }

            [Table("items")]
            public sealed partial class Item
            {
                [Key] public long Id { get; set; }
                [Column("payload")]
                [OwnedJson]
                [Converter(typeof(PayloadConverter))]
                public string Payload { get; set; } = "";
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);

        // v5.0：PALORM027（[Converter]+[OwnedJson] 互斥）也会触发——消息比 PALORM016 更精准
        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM016", "PALORM027"]);
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task UnsupportedEntityShape_ReportsPalorm015_WithoutGeneratedErrors()
    {
        const string source = """
            using PalORM;

            [Table("abstract_items")]
            public abstract partial class AbstractItem
            {
                [Key] public long Id { get; set; }
            }

            [Table("private_ctor_items")]
            public sealed partial class PrivateConstructorItem
            {
                private PrivateConstructorItem() { }
                [Key] public long Id { get; set; }
            }

            [Table("readonly_items")]
            public sealed partial class ReadonlyItem
            {
                [Key] public long Id { get; }
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM015", "PALORM015", "PALORM015"]);
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task ProviderSpecificDdl_UsesDialectTypesAndQuotedIdentifiers()
    {
        const string source = """
            using PalORM;

            [Table("order")]
            public sealed partial class ReservedEntity
            {
                [Key] [Required] public long Id { get; set; }
                [Column("select")] public string Value { get; set; } = "";
                [Column("created_at")] public System.DateTimeOffset CreatedAt { get; set; }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string migration = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("Migration_", StringComparison.Ordinal)).Value;
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(migration).Contains("CreateTableSqlite");
        await Assert.That(migration).Contains("\\\"order\\\"");
        await Assert.That(migration).Contains(
            "\\\"Id\\\" INTEGER PRIMARY KEY AUTOINCREMENT");
        await Assert.That(migration).Contains("CreateTablePostgreSql");
        await Assert.That(migration).Contains(
            "\\\"Id\\\" BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY");
        await Assert.That(migration).Contains("TIMESTAMPTZ");
        await Assert.That(migration).Contains("CreateTableMySql");
        await Assert.That(migration).Contains("`order`");
        await Assert.That(migration).Contains("`Id` BIGINT AUTO_INCREMENT PRIMARY KEY");
        await Assert.That(migration).Contains("DATETIME(6)");
        await Assert.That(registry).Contains("CreateTableSqlByDialect");
    }

    [Test]
    public async Task GeneratedCrudSql_QuotesReservedIdentifiersForEachDialect()
    {
        const string source = """
            using PalORM;

            [Table("order")]
            public sealed partial class ReservedEntity
            {
                [Key] public string Id { get; set; } = "";
                [Column("select")] public string Value { get; set; } = "";
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(registry).Contains("CommandSqlsByDialect");
        await Assert.That(registry).Contains(
            "INSERT INTO \\\"order\\\" (\\\"Id\\\", \\\"select\\\")");
        await Assert.That(registry).Contains(
            "UPDATE \\\"order\\\" SET \\\"select\\\" = @p0 WHERE \\\"Id\\\" = @p1");
        await Assert.That(registry).Contains(
            "DELETE FROM \\\"order\\\" WHERE \\\"Id\\\" = @p0");
        await Assert.That(registry).Contains(
            "INSERT INTO `order` (`Id`, `select`)");
        await Assert.That(registry).Contains(
            "UPDATE `order` SET `select` = @p0 WHERE `Id` = @p1");
        await Assert.That(registry).Contains(
            "DELETE FROM `order` WHERE `Id` = @p0");
    }

    [Test]
    public async Task IdentifierLiterals_AreEncodedWithoutChangingValues()
    {
        const string source = """
            using PalORM;

            [Table("order\"archive\\2026")]
            public sealed partial class EscapedEntity
            {
                [Key]
                [Column("id`\"value\\part")]
                public string Id { get; set; } = "";
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];
        string commandFactory = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("CommandFactory_", StringComparison.Ordinal)).Value;

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(registry).Contains("order\\\"archive\\\\2026");
        await Assert.That(registry).Contains("id`\\\"value\\\\part");
        await Assert.That(commandFactory).Contains("order\\\"archive\\\\2026");
        await Assert.That(commandFactory).Contains("id`\\\"value\\\\part");
    }

    [Test]
    public async Task NonNumericPrimaryKeys_AreNotMarkedAsGenerated()
    {
        const string source = """
            using PalORM;

            public readonly record struct ExternalId(string Value);

            public sealed class ExternalIdConverter
                : IValueConverter<ExternalId, string>
            {
                public string ToProvider(ExternalId value) => value.Value;
                public ExternalId FromProvider(string value) => new(value);
            }

            [Table("string_entities")]
            public sealed partial class StringEntity
            {
                [Key] public string Id { get; set; } = "";
                [Column("name")] public string Name { get; set; } = "";
            }

            [Table("converted_entities")]
            public sealed partial class ConvertedEntity
            {
                [Key]
                [Converter(typeof(ExternalIdConverter))]
                public ExternalId Id { get; set; }

                [Column("name")] public string Name { get; set; } = "";
            }

            [Table("generated_entities")]
            public sealed partial class GeneratedEntity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];

        string setIdDelegates = registry[
            registry.IndexOf("SetIdDelegates =", StringComparison.Ordinal)..];

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(setIdDelegates).DoesNotContain("global::StringEntity");
        await Assert.That(setIdDelegates).DoesNotContain("global::ConvertedEntity");
        await Assert.That(setIdDelegates).Contains("global::GeneratedEntity");
    }

    [Test]
    public async Task IndexAnnotations_GenerateDialectIndexDdlAndRegistryEntry()
    {
        const string source = """
            using PalORM;
            [Table("idx_items")]
            [Index("idx_items_status_total", "status", "total")]
            public sealed partial class IndexedItem
            {
                [Key] public long Id { get; set; }
                [Column("status")] public string Status { get; set; } = "";
                [Column("total")] public long Total { get; set; }
                [Column("code")]
                [Unique]
                public string Code { get; set; } = "";
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string migration = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("Migration_", StringComparison.Ordinal)).Value;
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        // 复合索引三方言：SQLite/PG 带 IF NOT EXISTS，MySQL 不带（幂等由运行时兜底）
        await Assert.That(migration).Contains(
            "CREATE INDEX IF NOT EXISTS \\\"idx_items_status_total\\\" ON \\\"idx_items\\\" (\\\"status\\\", \\\"total\\\")");
        await Assert.That(migration).Contains(
            "CREATE INDEX `idx_items_status_total` ON `idx_items` (`status`, `total`)");
        await Assert.That(migration).DoesNotContain("CREATE INDEX IF NOT EXISTS `");
        // [Unique] 升为单列唯一索引
        await Assert.That(migration).Contains(
            "CREATE UNIQUE INDEX IF NOT EXISTS \\\"ux_idx_items_code\\\" ON \\\"idx_items\\\" (\\\"code\\\")");
        // 注册链接入
        await Assert.That(registry).Contains("CreateIndexSqlByDialect");
        await Assert.That(registry).Contains("CreateIndexesSqlite");
    }

    [Test]
    public async Task EntityWithoutIndexes_GeneratesEmptyIndexArraysAndNoRegistryEntry()
    {
        const string source = """
            using PalORM;
            [Table("plain_items")]
            public sealed partial class PlainItem
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string migration = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("Migration_", StringComparison.Ordinal)).Value;
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(migration).Contains("CreateIndexesSqlite = global::System.Array.Empty<string>()");
        // 无索引实体不进入 CreateIndexSqlByDialect 字典（可选键）
        await Assert.That(registry).DoesNotContain("[typeof(global::PlainItem)] = new global::PalORM.CreateIndexSqlSet");
    }

    [Test]
    public async Task DecimalColumn_EmitsExplicitPrecision_AllDialects()
    {
        // ITM-303：MySQL 裸 DECIMAL 即 DECIMAL(10,0)，小数静默截断
        const string source = """
            using PalORM;

            [Table("prices")]
            public sealed partial class Price
            {
                [Key] public long Id { get; set; }
                [Column("amount")] public decimal Amount { get; set; }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string migration = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("Migration_", StringComparison.Ordinal)).Value;

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        // 三方言（列名带引用符）统一显式精度；legacy CreateTable 走 DbTypeName 不在断言范围
        string dialectSection = ExtractMethod(migration, "CreateTableSqlite", "CreateIndexesSqlite");
        await Assert.That(dialectSection).Contains("DECIMAL(18,6)");
        await Assert.That(dialectSection).DoesNotContain("DECIMAL ");
    }

    [Test]
    public async Task MySqlIndexedStringColumn_EmitsVarchar_NotText()
    {
        // ITM-201：MySQL 对 TEXT 列建索引报 1170（非 1061，不被幂等兜底吞）
        const string source = """
            using PalORM;

            [Table("indexed_items")]
            [Index("ux_indexed_items_sku", "sku", Unique = true)]
            public sealed partial class IndexedItem
            {
                [Key] public long Id { get; set; }
                [Column("sku")] [Required] public string Sku { get; set; } = "";
                [Column("note")] public string? Note { get; set; }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string migration = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("Migration_", StringComparison.Ordinal)).Value;

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        // MySQL 方言：被索引列 VARCHAR(255)，未被索引列保持 TEXT（MySQL 列名用反引号引用）
        string mysqlSection = ExtractMethod(migration, "CreateTableMySql", "CreateIndexesSqlite");
        await Assert.That(mysqlSection).Contains("`sku` VARCHAR(255)");
        await Assert.That(mysqlSection).Contains("`note` TEXT");
    }

    [Test]
    public async Task NonNullableAnnotatedColumn_EmitsNotNull_WithoutRequired()
    {
        // ITM-312：DDL NOT NULL 与 RowFactory 的 IsDBNull 守卫必须共用可空性语义源——
        // 非空注解列若建成可空，外部写入 NULL 后物化直接抛异常
        const string source = """
            using PalORM;

            [Table("annotated_items")]
            public sealed partial class AnnotatedItem
            {
                [Key] public long Id { get; set; }
                [Column("plain_name")] public string PlainName { get; set; } = "";
                [Column("optional_note")] public string? OptionalNote { get; set; }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string migration = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("Migration_", StringComparison.Ordinal)).Value;

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(migration).Contains("\\\"plain_name\\\" TEXT NOT NULL");
        await Assert.That(migration).DoesNotContain("\\\"optional_note\\\" TEXT NOT NULL");
    }

    [Test]
    public async Task SqlTemplate_MultipleTemplates_PartialClassAndCleanSqlConstant()
    {
        // ITM-305：① 非 partial 固定类名使第二个模板 CS0101；② SQL 提取混入边界引号
        const string source = """
            using PalORM;

            public static class Queries
            {
                [SqlTemplate("ActiveUsers")]
                public static System.FormattableString ActiveUsersTemplate()
                    => $"SELECT * FROM users WHERE active = 1";

                [SqlTemplate("RecentOrders")]
                public static System.FormattableString RecentOrdersTemplate()
                    => $"SELECT * FROM orders ORDER BY id DESC";
            }
            """;

        GeneratorResult result = RunGenerator(source);
        var templates = result.GeneratedSources
            .Where(pair => pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal))
            .ToList();

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(templates.Count).IsEqualTo(2);
        string combined = string.Join("\n", templates.Select(pair => pair.Value));
        await Assert.That(combined).Contains("public static partial class SqlTemplates");
        // SQL 常量不得含边界引号字符（ITM-411 后生成物原样搬运插值表达式语法）
        await Assert.That(combined).Contains(@"ActiveUsers = $""SELECT * FROM users WHERE active = 1"";");
        await Assert.That(combined).Contains(@"RecentOrders = $""SELECT * FROM orders ORDER BY id DESC"";");
    }

    [Test]
    public async Task SqlTemplate_CommentContainingDollarQuote_DoesNotConfuseExtraction()
    {
        // ITM-411 负向：方法前注释含 $" 时文本扫描会取错内容——语法树提取免疫
        const string source = """
            using PalORM;

            public static class Queries
            {
                // 旧格式示例: $"SELECT wrong FROM comment"
                [SqlTemplate("RealQuery")]
                public static System.FormattableString RealQueryTemplate()
                    => $"SELECT id FROM real_table";
            }
            """;

        GeneratorResult result = RunGenerator(source);
        string combined = string.Join("\n", result.GeneratedSources
            .Where(pair => pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal))
            .Select(pair => pair.Value));

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(combined).Contains("real_table");
        await Assert.That(combined).DoesNotContain("comment");
    }

    [Test]
    public async Task SqlTemplate_EscapedQuoteInSql_GeneratesValidCode()
    {
        // ITM-411 负向：SQL 含转义引号时文本扫描按 \" 截断——语法树原样搬运保持合法
        const string source = """
            using PalORM;

            public static class Queries
            {
                [SqlTemplate("Quoted")]
                public static System.FormattableString QuotedTemplate()
                    => $"SELECT \"col\" FROM t";
            }
            """;

        GeneratorResult result = RunGenerator(source);

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task SqlTemplate_ReferencingMethodParameter_IsSkippedInsteadOfCs0103()
    {
        // ITM-411 负向：插值洞引用方法参数——提升为静态字段会 CS0103，正确行为是跳过生成
        const string source = """
            using PalORM;

            public static class Queries
            {
                [SqlTemplate("ByStatus")]
                public static System.FormattableString ByStatusTemplate(string status)
                    => $"SELECT * FROM orders WHERE status = {status}";
            }
            """;

        GeneratorResult result = RunGenerator(source);

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(result.GeneratedSources.Any(pair =>
            pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal)
            && pair.Value.Contains("ByStatus", StringComparison.Ordinal))).IsFalse();
    }

    private static GeneratorResult RunGenerator(string source)
        => RunGenerator(CreateCompilation(source));

    private static GeneratorResult RunGenerator(CSharpCompilation compilation)
    {
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.Single().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new PalORMGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation outputCompilation,
            out _);

        var generatedSources = driver.GetRunResult().Results
            .SelectMany(static result => result.GeneratedSources)
            .ToDictionary(
                static source => source.HintName,
                static source => source.SourceText.ToString(),
                StringComparer.Ordinal);
        return new GeneratorResult((CSharpCompilation)outputCompilation, generatedSources);
    }

    private static CSharpCompilation CreateCompilation(
        string source,
        IEnumerable<MetadataReference>? additionalReferences = null,
        string assemblyName = "GeneratorPhase2Consumer")
    {
        string[] trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<MetadataReference> references = trustedAssemblies
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location));
        if (additionalReferences is not null)
            references = references.Concat(additionalReferences);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        return CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references.DistinctBy(static reference => reference.Display, StringComparer.Ordinal),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    private static string FormatErrors(Compilation compilation)
        => string.Join("\n", compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString()));

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        return source[start..end];
    }

    private sealed record GeneratorResult(
        CSharpCompilation OutputCompilation,
        IReadOnlyDictionary<string, string> GeneratedSources);

    [Test]
    public async Task SqlTemplate_UnqualifiedStaticField_DoesNotGenerate()
    {
        // r11.5-D1/r12-A1 锁定：非限定静态引用（{TablePrefix}）在生成类 SqlTemplates 内
        // CS0103 不可解析——拒绝生成（ITM-573 家族）；限定引用（Repos.X）放行对照
        // r12-A1 修正：非限定静态的真实形态是"方法所在类自己的静态字段"（用户源码合法，
        // 生成到 namespace 级 SqlTemplates 才 CS0103）——跨类引用在用户源码就编译不过
        const string source = """
            using PalORM;
            public static class C
            {
                public static string TablePrefix = "t";
                [SqlTemplate("bad")]
                public static System.FormattableString Bad()
                    => $"SELECT * FROM {TablePrefix}";
                [SqlTemplate("good")]
                public static System.FormattableString Good()
                    => $"SELECT * FROM {C.TablePrefix}";
            }
            """;

        GeneratorResult result = RunGenerator(source);
        var templates = result.GeneratedSources
            .Where(pair => pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal))
            .ToList();

        // 限定引用可编译生成；非限定被拒（不产生 CS0103 的非法生成物）
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        string combined = string.Join(System.Environment.NewLine, templates.Select(pair => pair.Value));
        await Assert.That(combined.Contains("{TablePrefix}", StringComparison.Ordinal)).IsFalse();
        await Assert.That(combined.Contains("C.TablePrefix", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SqlTemplate_UnqualifiedStaticMethod_DoesNotGenerate()
    {
        // r14-S1/r13-A 锁定：非限定静态方法 {M()} 与字段同族拒绝
        const string source = """
            using PalORM;
            public static class C
            {
                public static long NextId() => 1;
                [SqlTemplate("m")]
                public static System.FormattableString M()
                    => $"SELECT {NextId()}";
            }
            """;
        GeneratorResult result = RunGenerator(source);
        string combined = string.Join(System.Environment.NewLine, result.GeneratedSources
            .Where(pair => pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal))
            .Select(pair => pair.Value));
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(combined.Contains("{NextId()}", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task SqlTemplate_QualifiedMathReference_CompilesWithoutImplicitUsings()
    {
        // r14-S1/r13-B 锁定：{Math.PI} 限定引用在无 ImplicitUsings 宿主可解析（global using System）
        const string source = """
            using PalORM;
            public static class C
            {
                [SqlTemplate("pi")]
                public static System.FormattableString Pi()
                    => $"SELECT {System.Math.PI}";
            }
            """;
        GeneratorResult result = RunGenerator(source);
        // 生成物编译零错（CS0246 已被 global using System 消除）
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
    }
}
