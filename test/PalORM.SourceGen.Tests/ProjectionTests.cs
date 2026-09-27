using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// R1（v6.0）：[Projection] DTO 投影物化的编译期契约——PALORM049（×[Table] 互斥）、
/// PALORM050（[OwnedJson] 禁止）、生成物形状（只发 RowFactory + 物化注册，无 CommandFactory/
/// Migration）、无投影零生成物。真库物化 round-trip 由 Integration.Tests 的 ProjectionRoundTripTests 验证。
/// </summary>
public sealed class ProjectionTests
{
    [Test]
    public async Task PALORM049_ProjectionWithTable_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            [Projection]
            public sealed class E
            {
                [Key] public long Id { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM049")).IsTrue();
        await Assert.That(diagnostics.Single(d => d.Id == "PALORM049").Severity)
            .IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task PALORM050_ProjectionWithOwnedJson_Reports()
    {
        const string source = """
            using PalORM;
            using System.Text.Json.Serialization;
            public sealed class Payload { public string Value { get; set; } = ""; }
            [JsonSerializable(typeof(Payload))]
            public class Ctx : JsonSerializerContext { public static Ctx Default { get; } = new(null); public Ctx(System.Text.Json.JsonSerializerOptions? o) : base(o) { } public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type t) => null; protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => null; }
            [Projection]
            public sealed class P
            {
                public long Id { get; set; }
                [OwnedJson(typeof(Ctx))] public Payload Data { get; set; } = new();
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzerDiagnosticsTests.AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM050")).IsTrue();
    }

    [Test]
    public async Task Projection_GeneratesRowFactoryAndRegistryOnly()
    {
        const string source = """
            using PalORM;
            [Projection]
            public sealed class OrderSummary
            {
                public long Id { get; set; }
                public string CustomerName { get; set; } = "";
                public decimal Total { get; set; }
            }
            """;
        var result = GeneratorTestHost.RunGenerator(source, "ProjectionConsumer");

        // 生成物必须可编译（ITM-301 防线同款）
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();

        // 只读物化：RowFactory + 投影注册存在（hint 键控断言——空实体集也会发射空注册文件，
        // 其 RegistryDraft 字段声明含 BindInsert 等名字，不能做全生成物文本断言）
        await Assert.That(result.GeneratedSources.Keys
            .Any(static k => k.StartsWith("RowFactory_", System.StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.GeneratedSources.ContainsKey("PalORM_ProjectionRegistry.g.cs")).IsTrue();
        string projectionRegistry = result.GeneratedSources["PalORM_ProjectionRegistry.g.cs"];
        await Assert.That(projectionRegistry).Contains("[typeof(global::OrderSummary)] = RowFactory_");
        await Assert.That(projectionRegistry).Contains("FrozenDictionary<global::System.Type, string>.Empty");

        // 投影三无边界：无 CommandFactory/Migration 源文件、注册片段无写路径绑定/DDL
        await Assert.That(result.GeneratedSources.Keys
            .Any(static k => k.StartsWith("CommandFactory_", System.StringComparison.Ordinal))).IsFalse();
        await Assert.That(result.GeneratedSources.Keys
            .Any(static k => k.StartsWith("Migration_", System.StringComparison.Ordinal))).IsFalse();
        await Assert.That(projectionRegistry).DoesNotContain("CommandFactory_");
        await Assert.That(projectionRegistry).DoesNotContain("CREATE TABLE");
    }

    [Test]
    public async Task NoProjection_NoRegistryGenerated()
    {
        // 无 [Projection] 类型：不发射空注册文件（零生成物承诺）
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
            }
            """;
        var result = GeneratorTestHost.RunGenerator(source, "NoProjectionConsumer");
        string generated = string.Join("\n",
            result.OutputCompilation.SyntaxTrees
                .Where(static t => t.FilePath.EndsWith(".g.cs", System.StringComparison.Ordinal))
                .Select(static t => t.GetText().ToString()));
        await Assert.That(generated).DoesNotContain("PalORM_ProjectionRegistryInitializer");
    }

    [Test]
    public async Task Projection_WithConverter_CompileClean()
    {
        // [Converter] 在投影上合法（RowFactory 的 _conv_ 字段自包含，不依赖 CommandFactory）
        const string source = """
            using PalORM;
            public readonly record struct Ulid(string Value);
            public sealed class UlidConverter : IValueConverter<Ulid, string>
            {
                public string ToProvider(Ulid value) => value.Value;
                public Ulid FromProvider(string value) => new(value);
            }
            [Projection]
            public sealed class P
            {
                public long Id { get; set; }
                [Converter(typeof(UlidConverter))] public Ulid ExternalId { get; set; }
            }
            """;
        var result = GeneratorTestHost.RunGenerator(source, "ProjectionConverterConsumer");
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
        string generated = string.Join("\n",
            result.OutputCompilation.SyntaxTrees
                .Where(static t => t.FilePath.EndsWith(".g.cs", System.StringComparison.Ordinal))
                .Select(static t => t.GetText().ToString()));
        await Assert.That(generated).Contains("_conv_ExternalId");
    }
}
