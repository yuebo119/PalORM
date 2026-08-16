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
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,  // r15-N1：analyzer 含 record，生成器漏收致 [Table] record 静默跳过
                transform: static (ctx, _) => TableModel.FromContext(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            // v5.0 优化：显式用值相等比较器——TableModel 是 sealed record（有 Equals/GetHashCode），
            // 但 Roslyn 增量管道默认用 ReferenceEqualityComparer，导致实体未变更时仍重新生成。
            // WithComparer 让管道用值相等判断，提高增量缓存命中率（大项目构建时间进一步降低）。
            .WithComparer(EqualityComparer<TableModel>.Default);

        // v5.0 优化：合并 3 次独立 RegisterSourceOutput 为 1 次（减少增量管道回调 3→1）。
        // RowFactory / CommandFactory / Migration 互不依赖，可在一个回调内生成全部源文件。
        context.RegisterSourceOutput(tableModels, static (spc, model) =>
        {
            spc.AddSource(CreateStableHintName("RowFactory", model.EntityTypeName), RowFactoryEmitter.Generate(model));
            spc.AddSource(CreateStableHintName("CommandFactory", model.EntityTypeName), CommandFactoryEmitter.Generate(model));
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
            .Where(static s => s is not null)
            // ITM-653(r4)：GeneratedSource 为 record struct（值相等）——Collect+WithComparer
            // 启用增量缓存按值命中（对齐 tableModels/terminalCalls 管道，防每次编辑全量重发）
            .WithComparer(EqualityComparer<GeneratedSource?>.Default)
            .Collect();

        context.RegisterSourceOutput(sqlFileMethods, static (spc, sources) =>
        {
            foreach (var source in sources)
                spc.AddSource(source!.Value.HintName, source.Value.Source);
        });

        // ── SqlTemplate: [SqlTemplate("name")] → 预编译 SQL 常量 (Phase 5) ──
        // ITM-573：Collect 后按 (Namespace, TemplateName) 去重——两个方法挂同名模板此前
        // 各自生成同名字段（双 partial class → CS0102，错误指向 .g.cs 难定位）。
        // ITM-662：重名现在发射 PALORM041 Error 诊断（不再静默丢弃）——下方 RegisterSourceOutput 执行。
        var sqlTemplates = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PalORM.SqlTemplateAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => SqlTemplateEmitter.Generate(ctx, ct))
            .Where(static m => m is not null)
            // ITM-653(r4)：SqlTemplateModel 为 record（值相等）——同上启用增量按值缓存
            .WithComparer(EqualityComparer<SqlTemplateEmitter.SqlTemplateModel?>.Default)
            .Collect();

        context.RegisterSourceOutput(sqlTemplates, static (spc, models) =>
        {
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in models
                .OfType<SqlTemplateEmitter.SqlTemplateModel>()
                .OrderBy(static m => m.MethodIdentity, StringComparer.Ordinal))
            {
                if (!emitted.Add($"{model.Namespace}.{model.TemplateName}"))
                {
                    // ITM-662：重名必须显式报错——静默 continue 让第二个模板的 SQL
                    // 永远不可用且用户不知情（拿错 SQL 族）。
                    spc.ReportDiagnostic(Diagnostic.Create(
                        SqlTemplateEmitter.DuplicateSqlTemplateName, Location.None,
                        model.TemplateName, model.Namespace));
                    continue;
                }
                spc.AddSource(
                    CreateStableHintName("SqlTemplate", model.MethodIdentity),
                    SqlTemplateEmitter.Render(model));
            }
        });

        // ── Auto Tagging Interceptor（6 个终态方法，opt-in）──
        // 4 个硬假设已验证通过（PoC 阶段）：
        //   A. CSharpExtensions.GetInterceptableLocation 在 Roslyn 5.6.0 可用（扩展方法，非 SemanticModel 实例方法）
        //   B. netstandard2.0 源生成器项目可调用该 API
        //   C. SyntaxProvider.CreateSyntaxProvider + 谓词正确检测调用点（targets=1 实测）
        //   D. net11 AOT publish 0 警告（Interceptors 与 NativeAOT 完全兼容）
        // 配置经 CompilerVisibleProperty ItemGroup 传递到 GlobalOptions["build_property.PalORMAutoTagging"]。
        var autoTaggingEnabled = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) =>
                provider.GlobalOptions.TryGetValue("build_property.PalORMAutoTagging", out string? v)
                && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase));

        // 检测 6 个终态方法调用点；InterceptionTarget 是 sealed record（值相等），增量缓存按值命中。
        var terminalCalls = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => AutoTaggingEmitter.IsTerminalCall(node),
            transform: static (ctx, ct) => AutoTaggingEmitter.ExtractTarget(ctx, ct))
            .Where(static t => t is not null)
            .WithComparer(EqualityComparer<AutoTaggingEmitter.InterceptionTarget?>.Default)
            .Collect();

        context.RegisterSourceOutput(
            autoTaggingEnabled.Combine(terminalCalls),
            static (spc, tuple) =>
            {
                if (!tuple.Left) return;  // 开关关闭：零生成物
                spc.AddSource("PalORM_AutoTagging.g.cs",
                    AutoTaggingEmitter.Generate(tuple.Right));
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
