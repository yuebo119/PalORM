using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalORM.SourceGen;

/// <summary>PalORM 源生成器入口——IIncrementalGenerator。
/// <para><b>为什么用 IIncrementalGenerator 而非 ISourceGenerator</b>: 增量编译只重新生成变更的实体类型,
/// 避免全量重建——大型项目(100+实体)构建时间从 30s→2s。</para>
/// <para><b>Pipeline 设计</b>: ForAttributeWithMetadataName 收集所有 [Table] 类→逐模型独立生成→
/// Collect() 聚合生成 Registry(ModuleInitializer)。每个 Emitter 只处理自己的代码生成逻辑。</para>
/// <para><b>netstandard2.0 限制</b>: RS1041 强制源生成器目标 netstandard2.0——不能使用
/// AdditionalTexts(SqlFile改用 [SqlFile] 特性替代)、不能使用 net8.0+ API。</para></summary>
[Generator]
public sealed class PalORMGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ── 实体表模型 ──
        var tableModels = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PalORM.TableAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => TableModel.FromContext(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        // RowFactory (Phase 1)
        context.RegisterSourceOutput(tableModels, static (spc, model) =>
        {
            spc.AddSource(CreateStableHintName("RowFactory", model.EntityTypeName), RowFactoryEmitter.Generate(model));
        });

        // TypeMapper (Phase 1)
        context.RegisterSourceOutput(tableModels, static (spc, model) =>
        {
            string source = TypeMapperEmitter.Generate(model);
            if (!string.IsNullOrEmpty(source))
                spc.AddSource(CreateStableHintName("TypeMapper", model.EntityTypeName), source);
        });

        // CommandFactory (Phase 2)
        context.RegisterSourceOutput(tableModels, static (spc, model) =>
        {
            spc.AddSource(CreateStableHintName("CommandFactory", model.EntityTypeName), CommandFactoryEmitter.Generate(model));
        });

        // Migration DDL (Phase 2)
        context.RegisterSourceOutput(tableModels, static (spc, model) =>
        {
            spc.AddSource(CreateStableHintName("Migration", model.EntityTypeName), MigrationEmitter.Generate(model));
        });

        // Registry: ModuleInitializer (Phase 1)
        context.RegisterSourceOutput(tableModels.Collect(), static (spc, models) =>
        {
            spc.AddSource("PalORM_Registry.g.cs", RegistryEmitter.Generate(new EquatableArray<TableModel>(models)));
        });

        // ── SqlFile: [SqlFile("path.sql")] 特性 → 编译时嵌入 SQL (Phase 4) ──
        // 无需 AdditionalTexts API——通过特性参数读取文件路径，任何 Roslyn 版本均可用
        var sqlFileMethods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PalORM.SqlFileAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => CreateGeneratedSource("SqlFile", ctx, SqlFileEmitter.Generate(ctx, ct)))
            .Where(static s => s is not null);

        context.RegisterSourceOutput(sqlFileMethods, static (spc, source) =>
        {
            spc.AddSource(source!.Value.HintName, source.Value.Source);
        });

        // ── SqlTemplate: [SqlTemplate("name")] → 预编译 SQL 常量 (Phase 5) ──
        var sqlTemplates = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PalORM.SqlTemplateAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => CreateGeneratedSource("SqlTemplate", ctx, SqlTemplateEmitter.Generate(ctx, ct)))
            .Where(static s => s is not null);

        context.RegisterSourceOutput(sqlTemplates, static (spc, source) =>
        {
            spc.AddSource(source!.Value.HintName, source.Value.Source);
        });
    }

    private static GeneratedSource? CreateGeneratedSource(
        string prefix,
        GeneratorAttributeSyntaxContext context,
        string? source)
    {
        if (source is null || context.TargetSymbol is not IMethodSymbol method)
            return null;

        string identity = method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new GeneratedSource(CreateStableHintName(prefix, identity), source);
    }

    internal static string CreateStableHintName(string prefix, string symbolIdentity)
        => $"{prefix}_{SanitizeHintName(symbolIdentity)}_{ComputeStableHash(symbolIdentity):x8}.g.cs";

    internal static string CreateGeneratedTypeSuffix(string symbolIdentity)
        => $"{SanitizeHintName(symbolIdentity)}_{ComputeStableHash(symbolIdentity):x8}";

    private static string SanitizeHintName(string identity)
    {
        var result = new System.Text.StringBuilder(identity.Length);
        foreach (char character in identity)
            result.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        return result.ToString();
    }

    private static uint ComputeStableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        uint hash = offsetBasis;
        foreach (char character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }

    private readonly record struct GeneratedSource(string HintName, string Source);
}
