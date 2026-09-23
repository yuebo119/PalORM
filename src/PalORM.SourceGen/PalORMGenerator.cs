using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalORM.SourceGen;

/// <summary>PalORM 源生成器入口——IIncrementalGenerator。
/// <para><b>为什么用 IIncrementalGenerator 而非 ISourceGenerator</b>: 增量编译只重新生成变更的实体类型,
/// 避免全量重建——大型项目(100+实体)构建时间从 30s→2s。</para>
/// <para><b>Pipeline 设计</b>: ForAttributeWithMetadataName 收集所有 [Table] 类→逐模型独立生成→
/// Collect() 聚合生成 Registry(ModuleInitializer)。每个 Emitter 只处理自己的代码生成逻辑。</para>
/// <para><b>SqlFile 文件内容</b>: .sql 文件经 AdditionalFiles（targets 自动注入 **/*.sql）进入
/// 管线，内容成为增量缓存键——仅编辑 .sql 也重新生成（评审 2026-09-02，消除 ITM-585）。
/// 注：RS1041 只约束生成器 TFM，AdditionalTextsProvider 在 netstandard2.0 可用——旧注释
/// "RS1041 故不能用 AdditionalTexts"系误注，已随本重构更正。</para></summary>
[Generator]
public sealed class PalORMGenerator : IIncrementalGenerator
{
    /// <summary>PALORM045（评审 2026-09-02）：生成器 transform 失败面的兜底诊断。
    /// 正常构建下失败面对应的分析器诊断（PALORM015/016/022/042/043/044）多为 Error——
    /// 编译已失败，本 Warning 不改变结果；当分析器规则被 .editorconfig/ruleset 降级或关闭时，
    /// 实体被生成器静默跳过、编译期零反馈直到运行期 not registered——本诊断是该场景的
    /// 唯一编译期线索，故固定发射、不可关闭。位置为 None：兜底提示按实体名检索，
    /// 精确定位属分析器诊断职责（增量缓存键不能携带 Location）。</summary>
    internal static readonly DiagnosticDescriptor EntitySkippedByGenerator = new(
        id: "PALORM045",
        title: "Entity was skipped by the PalORM source generator",
        messageFormat: "Entity '{0}' was skipped by the PalORM source generator: {1}. "
            + "If no PALORM diagnostic marks the cause, an analyzer rule was probably suppressed via .editorconfig or a ruleset, and the entity would fail at runtime with 'not registered'.",
        category: "PalORM",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ── 实体表模型 ──
        // 评审 2026-09-02：transform 返回 EntityModelResult（含失败原因）——失败实体经
        // PALORM045 兜底诊断显式呈现，成功模型进入下方 tableModels 管线（原行为不变）。
        var entityModels = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PalORM.TableAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,  // r15-N1：analyzer 含 record，生成器漏收致 [Table] record 静默跳过
                transform: static (ctx, _) => TableModel.FromContext(ctx))
            .WithComparer(EqualityComparer<EntityModelResult>.Default);

        var tableModels = entityModels
            .Where(static r => r.Model is not null)
            .Select(static (r, _) => r.Model!)
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

        // ── 方言选择性发射（GEN-008，2026-09-23）──
        // build_property.PalORMTargetDialects（逗号分隔：postgresql/mysql/sqlite）；缺省/空 = 三方言全发射。
        // 经 CompilerVisibleProperty 传入（同 PalORMAutoTagging）。未目标方言不发射 SQL 载荷，
        // 运行时取到即响亮失败（CommandSqlByDialect.Get 守卫）。
        var dialectSelection = context.AnalyzerConfigOptionsProvider.Select(
            static (provider, _) => DialectSelection.Parse(
                provider.GlobalOptions.TryGetValue("build_property.PalORMTargetDialects", out string? v)
                    ? v
                    : null));

        // Registry: ModuleInitializer (Phase 1)
        context.RegisterSourceOutput(tableModels.Collect().Combine(dialectSelection), static (spc, input) =>
        {
            spc.AddSource("PalORM_Registry.g.cs",
                RegistryEmitter.Generate(new EquatableArray<TableModel>(input.Left), input.Right));
        });

        // PALORM045 兜底（评审 2026-09-02）：transform 失败面显式呈现——正常构建下对应
        // 分析器诊断（多为 Error）先行阻断编译，本 Warning 无感；分析器规则被抑制时它是
        // 唯一编译期线索（否则实体静默不注册，运行期才报 not registered）。
        context.RegisterSourceOutput(
            entityModels.Where(static r => r.Model is null),
            static (spc, failure) => spc.ReportDiagnostic(Diagnostic.Create(
                EntitySkippedByGenerator, Location.None,
                failure.EntityDisplayName ?? "<unknown>",
                failure.FailureReason ?? "unknown reason")));

        // ── SqlFile: [SqlFile("path.sql")] 特性 → 编译时嵌入 SQL (Phase 4) ──
        // 评审 2026-09-02：两阶段管线——ExtractMethodModel（transform，零 IO、缓存键=语法+符号）
        // 与 Render（RegisterSourceOutput，路径校验 + AdditionalFiles 内容查找）。.sql 内容经
        // AdditionalFiles 进缓存键：仅编辑 .sql 也重新生成（消除 ITM-585 陈旧缓存）。
        var sqlFileMethods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PalORM.SqlFileAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => SqlFileEmitter.ExtractMethodModel(ctx))
            .Where(static m => m is not null)
            // ITM-653(r4)：模型为 record（值相等）——WithComparer+Collect 启用增量按值命中
            .WithComparer(EqualityComparer<SqlFileEmitter.SqlFileMethodModel?>.Default)
            .Collect();

        var sqlFileTexts = context.AdditionalTextsProvider
            .Where(static t => t.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(static (text, ct) => SqlFileEmitter.SqlFileContent.FromAdditionalText(text, ct))
            .Collect();

        var palormProjectDir = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) =>
                provider.GlobalOptions.TryGetValue("build_property.ProjectDir", out string? dir)
                    ? dir : null);

        context.RegisterSourceOutput(
            sqlFileMethods.Combine(sqlFileTexts).Combine(palormProjectDir),
            static (spc, tuple) =>
            {
                (ImmutableArray<SqlFileEmitter.SqlFileMethodModel?> methods,
                    ImmutableArray<SqlFileEmitter.SqlFileContent> texts) = tuple.Left;
                string? projectDir = tuple.Right;
                foreach (SqlFileEmitter.SqlFileMethodModel? model in methods)
                {
                    if (model is null) continue;
                    string? source = SqlFileEmitter.Render(model, texts, projectDir);
                    if (source is null) continue;
                    spc.AddSource(model.HintName, source);
                }
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
                // ITM-719(r20)：声明不受支持（关键字名/宿主形状/带参泛型）——报 PALORM046
                // 且不生成字段（生成物会不可编译，错误指向 .g.cs）。
                if (model.InvalidReason is not null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        SqlTemplateEmitter.InvalidSqlTemplateDeclaration, Location.None,
                        model.MethodIdentity, model.InvalidReason));
                    continue;
                }
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

        // 检测 6 个终态方法调用点；InterceptionTarget 的 Location 成员是引用相等（见其
        // 注释，GEN-014 修正）——缓存命中退化为准引用级，候选一变即重渲染（过度失效方向安全）。
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
}
