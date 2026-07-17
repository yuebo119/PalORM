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
        string bindInsert = ExtractMethod(commandFactory, "BindInsert(", "BindUpsert(");
        string bindInsertValues = ExtractMethod(commandFactory, "BindInsertValues(", "SetId(");

        await Assert.That(errors).IsEmpty();
        await Assert.That(commandFactory).Contains(
            "InsertSql = \"INSERT INTO items (name) VALUES (@p0)\"");
        await Assert.That(bindInsert).Contains("entity.Name");
        await Assert.That(bindInsert).DoesNotContain("entity.Ignored");
        await Assert.That(bindInsert).DoesNotContain("entity.Timestamp");
        await Assert.That(bindInsert).DoesNotContain("entity.Computed");
        await Assert.That(bindInsertValues).Contains("entity.Name");
        await Assert.That(bindInsertValues).DoesNotContain("entity.Ignored");
        await Assert.That(bindInsertValues).DoesNotContain("entity.Timestamp");
        await Assert.That(bindInsertValues).DoesNotContain("entity.Computed");
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
        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(migration).Contains(
            "computed TEXT GENERATED ALWAYS AS (\\\"name\\\" || '-computed') STORED");
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
        await Assert.That(commandFactory).Contains(
            "IValueConverter<global::Ulid, string>)new global::UlidConverter()).ToProvider(entity.ExternalId)");
        await Assert.That(rowFactory).Contains(
            "IValueConverter<global::Ulid, string>)new global::UlidConverter()).FromProvider(r.GetString(1))");
        await Assert.That(migration).Contains("external_id TEXT");
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
            }
            """;

        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        GeneratorResult result = RunGenerator(compilation);
        string generated = string.Join("\n", result.GeneratedSources.Values);

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
        string bindDelete = ExtractMethod(commandFactory, "BindDelete(", "BindInsertValues(");

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(bindDelete).Contains(
            "IValueConverter<global::Ulid, string>)new global::UlidConverter()).ToProvider((global::Ulid)key)");
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
        await Assert.That(migration).Contains("optional_count INTEGER");
        await Assert.That(migration).Contains("small_value SMALLINT");
        await Assert.That(migration).Contains("byte_value SMALLINT");
        await Assert.That(migration).Contains("character TEXT");
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
        await Assert.That(generated).Contains(
            "((global::PalORM.IValueConverter<global::ExternalId, string>)new global::ExplicitConverter())");
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

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM001", "PALORM014"]);
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

        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id))
            .IsEquivalentTo(["PALORM016"]);
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
}
