using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PalORM.SourceGen;

/// <summary>PalORM 编译时验证——PALORM001-005, 008-040 诊断规则（006/007 已删，038/039 备用留空）。
/// PALORM006/007 已删除——006 由 SqlFileEmitter 的 Obsolete-error 机制承担，
/// 007 无 schema 对照数据源。编号不复用，避免历史引用混淆。
/// v5.0 扩充（PALORM023-027 实体级硬规则 + PALORM031-033 调用级 + PALORM034-037/040 防静默错误）。</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PalORMAnalyzer : DiagnosticAnalyzer
{
    // PALORM001: Missing primary key
    public static readonly DiagnosticDescriptor MissingPrimaryKey = new(
        "PALORM001", "Type has [Table] but no [Key] property",
        "Type '{0}' has [Table] attribute but no property with [Key] attribute", "PalORM", DiagnosticSeverity.Error, true);

    // F2（消息精准化）：原消息"does not match table schema"语义错位——实际是"建议加 [Column]"，不是 schema 错误。
    public static readonly DiagnosticDescriptor ColumnNameMismatch = new(
        "PALORM002", "Property has no [Column] attribute",
        "Property '{0}' has no [Column] attribute, so it maps to column '{0}' by name convention", "PalORM", DiagnosticSeverity.Warning, true);

    // ITM-585: 表名扫描仅覆盖本程序集（GetAssemblyTableNames）——多程序集实体布局下
    // 引用他程序集表会误报。Analyzer 按类型增量执行、跨编译引用聚合不可靠，多程序集
    // 场景可 .editorconfig 降级本诊断（dotnet_diagnostic.PALORM003.severity = suggestion）。
    // F6（消息精准化）：补"如实体在引用程序集可降级"提示。
    public static readonly DiagnosticDescriptor UnknownTable = new(
        "PALORM003", "Foreign key references unknown table",
        "[ForeignKey] references table '{0}' but no [Table] attribute is found in the current assembly", "PalORM", DiagnosticSeverity.Error, true);

    // ITM-525：FK 约束 DDL 当前不由 MigrateAsync 生成（ForeignKeys 收集为未来兼容保留），
    // 故本诊断只提示 OnDelete 声明是为未来 FK DDL 预留，当前不产生任何运行时约束效果。
    public static readonly DiagnosticDescriptor MissingForeignKey = new(
        "PALORM004", "Foreign key does not declare OnDelete behavior",
        "[ForeignKey] on '{0}' (type '{1}') does not set OnDelete; note FK constraint DDL is not generated in the current version, so OnDelete is recorded for future compatibility only and has no runtime effect yet", "PalORM", DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor NPlusOneDetected = new(
        "PALORM005", "Potential N+1 query pattern detected",
        "Database call inside a loop may cause N+1 queries. Consider using JOIN, WhereIn, or a single bulk operation.", "PalORM", DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor MissingOwnedJsonContext = new(
        "PALORM008", "Object OwnedJson requires a source-generated JSON context",
        "Property '{0}' uses [OwnedJson] with object type '{1}' but does not specify a JsonSerializerContext",
        "PalORM", DiagnosticSeverity.Error, true);

    // F8（消息精准化）：补 partial sealed 要求说明——STJ 源生成器要求 context 为 partial sealed，
    // 非 partial 时 STJ 不生成元数据，运行期抛 TypeInitializationException。
    public static readonly DiagnosticDescriptor InvalidOwnedJsonContext = new(
        "PALORM009", "OwnedJson context is not valid for the property type",
        "OwnedJson context '{0}' for property '{1}' must be a partial sealed class deriving from JsonSerializerContext and declaring [JsonSerializable(typeof({2}))]", "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedOwnedJsonDeclaration = new(
        "PALORM010", "OwnedJson declaration is not supported by source generation",
        "Property '{0}' uses [OwnedJson] in unsupported declaration '{1}'; entities and JSON contexts must be non-generic top-level types",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedQualifiedTable = new(
        "PALORM011", "Schema and database-qualified tables are not supported",
        "Type '{0}' configures Schema or Database, but qualified table names are not supported by the generated CRUD and migration pipeline",
        "PalORM", DiagnosticSeverity.Error, true);

    // F5（消息精准化）：补"emitter 用 ++ 自增，需整型"理由说明。
    public static readonly DiagnosticDescriptor InvalidConcurrencyTokenType = new(
        "PALORM012", "Concurrency token type is not supported",
        "[ConcurrencyCheck] property '{0}' must be non-nullable int or long because the source generator emits '++' increment", "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MultipleConcurrencyTokens = new(
        "PALORM013", "Multiple concurrency tokens are not supported",
        "Type '{0}' declares multiple [ConcurrencyCheck] properties; exactly zero or one is supported",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MissingSoftDeleteColumn = new(
        "PALORM014", "Soft-delete entity requires a deleted_at column",
        "Type '{0}' uses [SoftDelete] but does not map a property to column 'deleted_at'",
        "PalORM", DiagnosticSeverity.Error, true);

    // F4（消息精准化）：原消息"writable mapped properties"含义模糊，修订为单句原因清单。
    public static readonly DiagnosticDescriptor UnsupportedEntityDeclaration = new(
        "PALORM015", "Entity declaration is not supported by source generation",
        "Type '{0}' cannot be processed by source generation: the entity is generic/nested/abstract/static, lacks a public parameterless constructor, or has a property with init-only or non-public/internal setter", "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidValueMapping = new(
        "PALORM016", "Property value mapping is not supported",
        "Property '{0}' has unsupported type '{1}' or an invalid [Converter]; use a supported provider type and an accessible parameterless converter implementing IValueConverter<{1}, TProvider>",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor AnnotationNotAppliedToDdl = new(
        "PALORM017", "Annotation does not participate in DDL generation",
        "{0} on '{1}' does not participate in migration DDL generation in the current version; MigrateAsync will not create the corresponding schema object",
        "PalORM", DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor MissingTenantColumn = new(
        "PALORM018", "Tenant-aware entity requires a tenant_id column",
        "Type '{0}' uses [TenantAware] but does not map a property to column 'tenant_id'; WithTenant filtering would reference a non-existent column",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor CompositePrimaryKey = new(
        "PALORM019", "Composite primary keys are not supported",
        "Type '{0}' declares {1} [Key] properties; PalORM supports exactly one primary key per entity",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidIndexDeclaration = new(
        "PALORM020", "Index declaration is invalid or conflicting",
        "{0}", "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor DuplicateColumnName = new(
        "PALORM021", "Multiple properties map to the same column name",
        "Properties '{0}' and '{1}' on type '{2}' both map to column '{3}'; generated INSERT/UPDATE would reference the column twice and fail at runtime",
        "PalORM", DiagnosticSeverity.Error, true);

    // PALORM022（ITM-560/561）：主键声明形态生成器无法支持——init-only setter 生成 CS8852，
    // 可空值类型生成 CS0037/CS8117；此前生成器静默跳过、运行期才报 not registered。
    public static readonly DiagnosticDescriptor InvalidKeyDeclaration = new(
        "PALORM022", "Primary key declaration is not supported by source generation",
        "[Key] property '{0}' on type '{1}' {2}; the entity would be silently skipped by source generation",
        "PalORM", DiagnosticSeverity.Error, true);

    // === v5.0 扩充：实体级硬规则（PALORM023-027）===

    // PALORM023：实体无可插入列——运行期 DataSession.Crud.cs:79 / MultiValueBulkInsert.cs:43 throw。
    public static readonly DiagnosticDescriptor NoInsertableColumns = new(
        "PALORM023", "Entity has no insertable columns",
        "Type '{0}' has no insertable columns (all properties are [IgnoreOnInsert]/[Key(AutoIncrement)]/[Computed]/[Timestamp])", "PalORM", DiagnosticSeverity.Error, true);

    // PALORM024：实体无可更新列——运行期 DataSession.Crud.cs:183 / DataSession_Bulk.cs:260 throw。
    public static readonly DiagnosticDescriptor NoUpdatableColumns = new(
        "PALORM024", "Entity has no updatable columns",
        "Type '{0}' has no updatable columns (all non-PK properties are [IgnoreOnInsert]/[Computed]/[Timestamp])", "PalORM", DiagnosticSeverity.Error, true);

    // PALORM025：[Timestamp] 标在非时间类型——ITM-402：MigrationEmitter 仅对 DateTime/DateTimeOffset
    // 生成 DEFAULT CURRENT_TIMESTAMP，其它类型 NOT NULL 无 DEFAULT 每次插入必失败。
    public static readonly DiagnosticDescriptor UnsupportedTimestampType = new(
        "PALORM025", "[Timestamp] property type is not supported",
        "[Timestamp] property '{0}' on type '{1}' has type '{2}', but only DateTime/DateTimeOffset are supported", "PalORM", DiagnosticSeverity.Error, true);

    // PALORM026：[NotMapped] 与映射特性互斥——EnumerateMappedProperties 跳过 [NotMapped]，
    // [Key]+[NotMapped] 会让 AnalyzePrimaryKey 误报 PALORM001"无 Key"。
    public static readonly DiagnosticDescriptor NotMappedConflict = new(
        "PALORM026", "[NotMapped] conflicts with a mapping attribute",
        "Property '{0}' has [NotMapped] alongside [{1}], but [NotMapped] excludes the property from generation, making [{1}] silently ineffective", "PalORM", DiagnosticSeverity.Error, true);

    // PALORM027：[Converter] 与 [OwnedJson] 互斥——HasValidValueMapping:87 拒绝，
    // CanGenerateEntity 返回 false → PALORM015 兜底但消息笼统。
    public static readonly DiagnosticDescriptor ConverterOwnedJsonConflict = new(
        "PALORM027", "[Converter] and [OwnedJson] are mutually exclusive",
        "Property '{0}' has both [Converter] and [OwnedJson], but these are mutually exclusive", "PalORM", DiagnosticSeverity.Error, true);

    // === v5.0 扩充：调用级 API 误用（PALORM031-033）===

    // PALORM031：BulkUpdateBatchAsync<T> 对 [ConcurrencyCheck] 实体调用——
    // DataSession_Bulk.cs:200-204 throw NotSupportedException，每次必崩。
    public static readonly DiagnosticDescriptor BulkUpdateBatchOnVersionedEntity = new(
        "PALORM031", "BulkUpdateBatchAsync on [ConcurrencyCheck] entity always throws",
        "BulkUpdateBatchAsync<{0}> is called but {0} has [ConcurrencyCheck], which always throws NotSupportedException at runtime", "PalORM", DiagnosticSeverity.Error, true);

    // PALORM032：Include/Join 引用未注册实体——QueryBuilder.cs:857-861 throw。
    // Warning 级：多程序集场景同 PALORM003 局限。
    public static readonly DiagnosticDescriptor JoinReferencesUnregisteredEntity = new(
        "PALORM032", "Join/Include references an entity without [Table]",
        "Generic argument '{0}' of {1}() has no [Table] attribute, which throws InvalidOperationException at runtime", "PalORM", DiagnosticSeverity.Warning, true);

    // PALORM033：实体查询后 Select(projection).ToListAsync()——QueryBuilderExtensions.cs:36 throw。
    public static readonly DiagnosticDescriptor SelectProjectionWithToList = new(
        "PALORM033", "Select(projection) followed by ToListAsync/FirstAsync always throws",
        "Select(projection) followed by ToListAsync/FirstAsync/SingleAsync always throws NotSupportedException at runtime", "PalORM", DiagnosticSeverity.Error, true);

    // === v5.0 扩充：防静默错误（PALORM034-037, 040）——前版误剔的修正 ===

    // PALORM034：[Key] 非默认初始值——HasDefaultKey 永远 false，SaveAsync 永远走 Update，数据静默丢失。
    public static readonly DiagnosticDescriptor KeyWithNonDefaultValue = new(
        "PALORM034", "[Key] property has a non-default initial value",
        "[Key] property '{0}' on type '{1}' has a non-default initial value, so SaveAsync always routes to Update and never Inserts", "PalORM", DiagnosticSeverity.Warning, true);

    // PALORM035：[ConcurrencyCheck]+[IgnoreOnInsert]——version 列保持默认 0，
    // 首次并发更新永远成功，乐观锁静默失效。
    public static readonly DiagnosticDescriptor ConcurrencyCheckWithIgnoreOnInsert = new(
        "PALORM035", "[ConcurrencyCheck] with [IgnoreOnInsert] bypasses optimistic locking",
        "Property '{0}' has [ConcurrencyCheck] and [IgnoreOnInsert], leaving the version at default (0) after insert and bypassing the optimistic lock", "PalORM", DiagnosticSeverity.Warning, true);

    // PALORM036：#nullable disable 下引用类型不生成 IsDBNull 守卫——
    // RowFactoryEmitter.cs:92-96 注释自承：DB 列为 NULL 时 GetString 抛 SqlNullValueException。
    public static readonly DiagnosticDescriptor NullableContextDisabled = new(
        "PALORM036", "Entity is in a #nullable disable context",
        "Type '{0}' is in a #nullable disable context, so nullable reference type properties will not generate IsDBNull guards", "PalORM", DiagnosticSeverity.Warning, true);

    // PALORM037：[Required] + 可空引用类型矛盾——DDL 生成 NOT NULL（MigrationEmitter.cs:92），
    // 但 RowFactoryEmitter 因 IsNullable=true 仍生成 IsDBNull 守卫，行为不一致。
    public static readonly DiagnosticDescriptor RequiredWithNullableAnnotation = new(
        "PALORM037", "[Required] conflicts with nullable reference type annotation",
        "Property '{0}' has [Required] but is annotated as nullable ('{1}?'), which makes DDL and reader behavior inconsistent", "PalORM", DiagnosticSeverity.Warning, true);

    // PALORM040：[TenantAware] 实体 tenant_id 列可空或无 [Required]——
    // 租户隔离完全靠实体 tenant_id 值承载（DataSession.cs:356-359），
    // NULL tenant_id 让跨租户数据可见，多租户安全漏洞比崩溃更严重。
    public static readonly DiagnosticDescriptor TenantColumnNullable = new(
        "PALORM040", "[TenantAware] entity's tenant_id column is nullable",
        "[TenantAware] entity's tenant_id column (property '{0}' on '{1}') is nullable or lacks [Required], bypassing tenant isolation", "PalORM", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [MissingPrimaryKey, ColumnNameMismatch, UnknownTable, MissingForeignKey,
         NPlusOneDetected, MissingOwnedJsonContext,
         InvalidOwnedJsonContext, UnsupportedOwnedJsonDeclaration, UnsupportedQualifiedTable,
         InvalidConcurrencyTokenType, MultipleConcurrencyTokens, MissingSoftDeleteColumn,
         UnsupportedEntityDeclaration, InvalidValueMapping, AnnotationNotAppliedToDdl,
         MissingTenantColumn, CompositePrimaryKey, InvalidIndexDeclaration, DuplicateColumnName,
         InvalidKeyDeclaration,
         NoInsertableColumns, NoUpdatableColumns, UnsupportedTimestampType, NotMappedConflict,
         ConverterOwnedJsonConflict,
         BulkUpdateBatchOnVersionedEntity, JoinReferencesUnregisteredEntity, SelectProjectionWithToList,
         KeyWithNonDefaultValue, ConcurrencyCheckWithIgnoreOnInsert, NullableContextDisabled,
         RequiredWithNullableAnnotation, TenantColumnNullable];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "Roslyn 分析器的 Initialize 是注册调度中心——多个 RegisterSymbolAction/SyntaxNodeAction "
            + "串行注册不可避免。每个 lambda 已被拆到独立静态方法（AnalyzeEntityDiagnostics 等）；"
            + "Initialize 体的复杂度来自诊断注册数量本身，进一步拆分会损害可读性。")]
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // PALORM001: Missing primary key
        context.RegisterSymbolAction(ctx =>
        {
            if (ctx.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } type
                || !SourceGenerationValidation.IsSupportedEntity(type)
                || !type.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "Table")))  // ITM-512
                return;
            AnalyzePrimaryKey(ctx, type);
        }, SymbolKind.NamedType);

        // PALORM002 + PALORM003 + PALORM004: 表级验证
        // ITM-590: 通过 CompilationStartAction 注册，让 assemblyTables 缓存跨类型复用
        // （此前每类型 SymbolAction 各自 lazy 收集一次，O(N) 实体 × O(N) 扫描 = O(N²)）。
        context.RegisterCompilationStartAction(startContext =>
        {
            HashSet<string>? assemblyTables = null;
            startContext.RegisterSymbolAction(ctx =>
            {
                if (ctx.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } type) return;
                var tableAttribute = type.GetAttributes().FirstOrDefault(a => SourceGenerationValidation.IsPalORMAttribute(a, "Table"));
                if (tableAttribute is null) return;
                AnalyzeEntityDiagnostics(ctx, type, ref assemblyTables);
            }, SymbolKind.NamedType);
        });

        // PALORM005: N+1 检测 — 循环中 From<T>() / ORM 调用
        // ITM-574：语法名匹配会误报 EF Core/MongoDB 等第三方库的同名方法（ToListAsync 等），
        // TreatWarningsAsErrors 项目直接阻断——语义模型确认接收者/方法归属 PalORM 后才报。
        context.RegisterSyntaxNodeAction(ctx =>
        {
            var invocation = (InvocationExpressionSyntax)ctx.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) return;
            if (!IsPalORMQueryMethod(ma.Name.Identifier.Text)) return;
            if (!IsPalORMInvocation(ctx, invocation)) return;
            if (TryFindEnclosingLoop(invocation) is { } loopLocation)
                ctx.ReportDiagnostic(Diagnostic.Create(NPlusOneDetected, loopLocation));
        }, SyntaxKind.InvocationExpression);

        // PALORM031: BulkUpdateBatchAsync<T> 对 [ConcurrencyCheck] 实体调用——必崩
        // PALORM032: Include/Join/ThenInclude 引用未注册实体
        context.RegisterSyntaxNodeAction(ctx =>
        {
            var invocation = (InvocationExpressionSyntax)ctx.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) return;
            string methodName = ma.Name.Identifier.Text;

            if (methodName is "BulkUpdateBatchAsync")
            {
                CheckBulkUpdateBatchConcurrency(ctx, ma, invocation);
            }
            else if (methodName is "Include" or "InnerJoin" or "LeftJoin" or "RightJoin" or "ThenInclude")
            {
                CheckJoinUnregisteredEntity(ctx, ma, invocation, methodName);
            }
            else if (methodName is "Select")
            {
                CheckSelectProjection(ctx, ma, invocation);
            }
        }, SyntaxKind.InvocationExpression);
    }

    /// <summary>PALORM031：BulkUpdateBatchAsync&lt;T&gt; 调用，T 含 [ConcurrencyCheck] 属性。</summary>
    private static void CheckBulkUpdateBatchConcurrency(
        SyntaxNodeAnalysisContext ctx, MemberAccessExpressionSyntax ma, InvocationExpressionSyntax invocation)
    {
        // ITM-614：语义层取泛型实参（IMethodSymbol.TypeArguments）——推断式调用
        // （session.BulkUpdateBatchAsync(list)）的 ma.Name 是 IdentifierNameSyntax 而非
        // GenericNameSyntax，语法层判定漏报（探针实证，AnalyzerDiagnosticsTests 推断式用例）。
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol
            is not IMethodSymbol { IsGenericMethod: true } method)
            return;
        if (method.TypeArguments.Length < 1) return;
        if (method.TypeArguments[0] is not INamedTypeSymbol entityType) return;

        bool hasConcurrency = SourceGenerationValidation.EnumerateMappedProperties(entityType)
            .Any(p => p.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "ConcurrencyCheck")));
        if (hasConcurrency)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(BulkUpdateBatchOnVersionedEntity,
                invocation.GetLocation(), entityType.Name));
        }
    }

    /// <summary>PALORM032：Include&lt;TChild&gt;/InnerJoin&lt;TJoin&gt; 等引用未注册实体。</summary>
    private static void CheckJoinUnregisteredEntity(
        SyntaxNodeAnalysisContext ctx, MemberAccessExpressionSyntax ma,
        InvocationExpressionSyntax invocation, string methodName)
    {
        // ITM-614：同 CheckBulkUpdateBatchConcurrency——语义层 TypeArguments 覆盖推断式调用
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol
            is not IMethodSymbol { IsGenericMethod: true } method)
            return;
        if (method.TypeArguments.Length < 1) return;
        if (method.TypeArguments[0] is not INamedTypeSymbol entityType) return;

        bool hasTable = entityType.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Table"));
        if (!hasTable)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(JoinReferencesUnregisteredEntity,
                invocation.GetLocation(), entityType.Name, methodName));
        }
    }

    /// <summary>PALORM033：Select(projection) 后接 ToListAsync/FirstAsync——运行期必崩。
    /// 策略：同一 IdentifierName 变量上 Select 后立即跟 To/First/Single 调用。
    /// 已知限制：仅覆盖多语句模式（builder.Select(...); builder.ToListAsync();），
    /// 链式调用（session.From&lt;T&gt;().Select(...).ToListAsync()）不报告——
    /// 因 ma.Expression 是 InvocationExpressionSyntax 不是 IdentifierNameSyntax。
    /// 链式追踪复杂度高且易误报，留作未来增强。</summary>
    private static void CheckSelectProjection(
        SyntaxNodeAnalysisContext ctx, MemberAccessExpressionSyntax ma, InvocationExpressionSyntax invocation)
    {
        // ITM-634：语法预筛先行（零成本）——GetSymbolInfo 语义查询原在其后，全项目每个
        // Select 调用（含 LINQ Select）都白付一次语义解析。
        if (ma.Expression is not IdentifierNameSyntax varRef) return;

        // Select 必须属于 PalORM——从 QueryBuilder<T> 类型
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol
            is not IMethodSymbol methodSymbol
            || methodSymbol.ContainingType?.Name is not ("QueryBuilder" or "QueryBuilder`1")
            || methodSymbol.ContainingNamespace?.ToDisplayString() is not { } ns
            || !ns.StartsWith("PalORM", StringComparison.Ordinal))
            return;

        // 追踪 Select 的接收者变量名
        string varName = varRef.Identifier.Text;

        // 向后扫描：同一语句或 ExpressionStatement 后续是否存在 varName.ToListAsync/FirstAsync 调用
        // 简化：找后续 ExpressionStatement 链中的同变量调用
        SyntaxNode? parent = invocation.Parent;
        while (parent is not null and not BlockSyntax and not StatementSyntax)
            parent = parent.Parent;
        if (parent is not ExpressionStatementSyntax currentStmt) return;

        // ITM-634：向后扫描加上限——原无距离上限，中间隔任意多语句仍误报（变量可能已重赋值）
        SyntaxNode? sibling = currentStmt;
        int scanned = 0;
        while (sibling is not null && scanned < 5)
        {
            scanned++;
            sibling = sibling.Parent?.ChildNodes()
                .FirstOrDefault(n => n.SpanStart > sibling.SpanStart);
            if (sibling is ExpressionStatementSyntax exprStmt
                && exprStmt.DescendantNodes()
                    .OfType<MemberAccessExpressionSyntax>()
                    .Any(m => m.Expression is IdentifierNameSyntax id
                              && id.Identifier.Text == varName
                              && m.Name.Identifier.Text is "ToListAsync" or "FirstAsync" or "SingleAsync"))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(SelectProjectionWithToList, invocation.GetLocation()));
                return;
            }
            if (sibling is not ExpressionStatementSyntax) break;
        }
    }

    /// <summary>判断方法名是否为 PalORM N+1 检测目标的查询方法。
    /// F1 修复：原清单遗漏 Bulk/Save/Get——循环内 BulkInsertAsync/SaveAsync/GetAsync 是典型 N+1。
    /// 不加 BulkUpdateBatchAsync（本身批量，循环内分批合理）、SeedAsync（启动期一次性）。</summary>
    private static bool IsPalORMQueryMethod(string methodName)
        => methodName is "From" or "InsertAsync" or "UpdateAsync" or "DeleteAsync"
            or "ToListAsync" or "FirstAsync" or "SingleAsync"
            or "SaveAsync" or "GetAsync" or "GetAllAsync"
            or "BulkInsertAsync" or "BulkUpdateAsync" or "BulkDeleteAsync" or "BulkMergeAsync";

    /// <summary>语义模型确认方法归属 PalORM 命名空间（ITM-574：避免误报 EF Core/MongoDB 等同名方法）。</summary>
    private static bool IsPalORMInvocation(SyntaxNodeAnalysisContext ctx, InvocationExpressionSyntax invocation)
    {
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol
                is not IMethodSymbol methodSymbol
            || methodSymbol.ContainingNamespace?.ToDisplayString() is not { } containingNs)
            return false;
        return containingNs == "PalORM"
            || containingNs.StartsWith("PalORM.", StringComparison.Ordinal);
    }

    /// <summary>向上查找最近的循环语法节点；遇到函数边界（lambda/局部函数/匿名方法）停止。
    /// 返回循环内首次调用点的 Location；不在循环内返回 null。
    /// ITM-574：循环体内定义、循环外执行的 lambda/局部函数不是 N+1。</summary>
    private static Location? TryFindEnclosingLoop(InvocationExpressionSyntax invocation)
    {
        SyntaxNode? parent = invocation.Parent;
        while (parent is not null)
        {
            if (parent is LambdaExpressionSyntax or LocalFunctionStatementSyntax
                or AnonymousMethodExpressionSyntax)
                return null;  // 函数边界——非 N+1
            if (parent is ForStatementSyntax or ForEachStatementSyntax
                or WhileStatementSyntax or DoStatementSyntax)
                return invocation.GetLocation();
            parent = parent.Parent;
        }
        return null;
    }

    /// <summary>分类主键声明的合法性。返回 null 表示通过；否则返回诊断 reason（ITM-589）。
    /// 顺序优先级：setter 可写性 &gt; 类型非 Nullable &gt; AutoIncrement 类型匹配。</summary>
    private static string? ClassifyKeyValidity(IPropertySymbol key, bool autoIncrementEnabled)
    {
        if (key.SetMethod is null or { IsInitOnly: true })
            return "has an init-only or missing setter (generated ID backfill requires a writable setter)";

        if (key.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            return "is a nullable value type (generated key binding cannot compile)";

        if (autoIncrementEnabled
            && key.Type.SpecialType is not (SpecialType.System_Int64 or SpecialType.System_Int32))
        {
            return $"declares [Key(AutoIncrement = true)] but type is '{key.Type.Name}' — "
                + "only int/long keys support auto-increment; "
                + "use [Key(AutoIncrement = false)] for application-assigned keys (snowflake/Guid/string)";
        }

        return null;
    }

    /// <summary>PALORM001/019/022 主键诊断调度。
    /// ITM-559：计数走基类链——只查声明成员会对"基类声明 [Key]"的实体误报"无 [Key]"。
    /// ITM-614：reason 链按 init-only → nullable value type → 非整型+AutoIncrement 三分支首中即报。</summary>
    private static void AnalyzePrimaryKey(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        List<IPropertySymbol> keyProperties = SourceGenerationValidation
            .EnumerateMappedProperties(type)
            .Where(static property => property.GetAttributes().Any(attribute =>
                SourceGenerationValidation.IsPalORMAttribute(attribute, "Key")))  // ITM-512
            .ToList();

        if (keyProperties.Count == 0)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(MissingPrimaryKey, type.Locations[0], type.Name));
            return;
        }

        // PALORM019: 复合主键——BindDelete 单 key 语义无法表达，明确拒绝（ITM-311）
        if (keyProperties.Count > 1)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(CompositePrimaryKey, type.Locations[0], type.Name, keyProperties.Count));
            return;
        }

        // PALORM022（ITM-560/561）: 生成器 CanGenerateEntity 会静默跳过的主键形态在此定位报错。
        IPropertySymbol key = keyProperties[0];
        // ITM-589: [Key(AutoIncrement = true)] 配合非整型主键（Guid/string 等）时，
        // TableModel.isAutoIncrement 仅识别 Int64/Int32（TableModel.cs:74），用户意图被静默
        // 忽略——编译期无诊断，运行时 InsertAsync 会失败或抛奇怪错误。在此显式拒绝。
        bool autoIncrementEnabled = key.GetAttributes()
            .FirstOrDefault(static a => SourceGenerationValidation.IsPalORMAttribute(a, "Key"))?
            .NamedArguments.FirstOrDefault(static na => na.Key == "AutoIncrement").Value.Value is not false;
        string? reason = ClassifyKeyValidity(key, autoIncrementEnabled);
        if (reason is not null)
            ctx.ReportDiagnostic(Diagnostic.Create(InvalidKeyDeclaration,
                key.Locations.FirstOrDefault() ?? type.Locations[0], key.Name, type.Name, reason));
    }

    /// <summary>实体级诊断调度器——把原 CC 153 的巨型 lambda 拆分为按诊断规则分组的子方法。
    /// 子方法均为 static、各自处理一类诊断（PALORM002/003/004/014/017/018/019/020/021/022），
    /// 主方法只负责按"实体级 → 属性级"顺序调度。</summary>
    private static void AnalyzeEntityDiagnostics(
        SymbolAnalysisContext ctx, INamedTypeSymbol type, ref HashSet<string>? assemblyTables)
    {
        if (!SourceGenerationValidation.IsSupportedEntity(type))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                UnsupportedEntityDeclaration,
                type.Locations[0],
                type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            return;
        }

        CheckQualifiedTable(ctx, type);
        CheckSoftDeleteColumn(ctx, type);     // PALORM014
        CheckTenantColumn(ctx, type);         // PALORM018
        CheckTenantColumnNullable(ctx, type); // PALORM040
        ValidateIndexDeclarations(ctx, type); // PALORM020
        CheckColumnUniqueness(ctx, type);     // PALORM021
        CheckConcurrencyTokens(ctx, type);    // PALORM013/012
        CheckInsertableColumns(ctx, type);    // PALORM023
        CheckUpdatableColumns(ctx, type);     // PALORM024
        CheckNullableContext(ctx, type);      // PALORM036
        CheckPropertyLevelDiagnostics(ctx, type, ref assemblyTables); // PALORM002/003/004/017/019/025/026/027/034/035/037
    }

    /// <summary>PALORM013：带 Schema/Database 限定符的 [Table] 不被支持。</summary>
    private static void CheckQualifiedTable(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        var tableAttribute = type.GetAttributes().FirstOrDefault(a => SourceGenerationValidation.IsPalORMAttribute(a, "Table"));
        bool hasQualifiedTable = tableAttribute!.NamedArguments.Any(argument =>
                argument.Key is "Schema" or "Database" && argument.Value.Value is string)
            || type.GetAttributes().Any(attribute =>
                SourceGenerationValidation.IsPalORMAttribute(attribute, "Schema")
                || SourceGenerationValidation.IsPalORMAttribute(attribute, "Database"));  // ITM-512
        if (hasQualifiedTable)
            ctx.ReportDiagnostic(Diagnostic.Create(UnsupportedQualifiedTable, type.Locations[0], type.Name));
    }

    /// <summary>PALORM014：[SoftDelete] 实体必须映射 deleted_at 列。
    /// ITM-587：走基类链（与 TableModel.GetMappableProperties 口径一致）——派生类继承
    /// AuditBase 把 deleted_at 放基类时，type.GetMembers() 只查声明类型会误报。</summary>
    private static void CheckSoftDeleteColumn(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        bool isSoftDelete = type.GetAttributes().Any(attribute =>
            SourceGenerationValidation.IsPalORMAttribute(attribute, "SoftDelete"));  // ITM-512
        if (!isSoftDelete) return;

        bool hasSoftDeleteColumn = SourceGenerationValidation.EnumerateMappedProperties(type)
            .Any(static property => property.GetAttributes().Any(attribute =>
                SourceGenerationValidation.IsPalORMAttribute(attribute, "Column")
                && attribute.ConstructorArguments.FirstOrDefault().Value is "deleted_at"));  // ITM-512
        if (!hasSoftDeleteColumn)
            ctx.ReportDiagnostic(Diagnostic.Create(MissingSoftDeleteColumn, type.Locations[0], type.Name));
    }

    /// <summary>PALORM018：[TenantAware] 实体必须映射 tenant_id 列。
    /// ITM-588：同 ITM-587——走基类链覆盖 TenantBase 放 tenant_id 的派生类。</summary>
    private static void CheckTenantColumn(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        bool isTenantAware = type.GetAttributes().Any(attribute =>
            SourceGenerationValidation.IsPalORMAttribute(attribute, "TenantAware"));  // ITM-512
        if (!isTenantAware) return;

        bool hasTenantColumn = SourceGenerationValidation.EnumerateMappedProperties(type)
            .Any(static property => property.GetAttributes().Any(attribute =>
                SourceGenerationValidation.IsPalORMAttribute(attribute, "Column")
                && attribute.ConstructorArguments.FirstOrDefault().Value is "tenant_id"));  // ITM-512
        if (!hasTenantColumn)
            ctx.ReportDiagnostic(Diagnostic.Create(MissingTenantColumn, type.Locations[0], type.Name));
    }

    /// <summary>PALORM040：[TenantAware] 实体的 tenant_id 列可空或无 [Required]。
    /// 租户隔离完全靠实体 tenant_id 值承载（DataSession.cs:356-359），NULL tenant_id 让跨租户数据可见。
    /// 注：非可空值类型（long/int/Guid）天然不可能 null，不要求 [Required]；
    /// 仅引用类型（string）需要 [Required] 或非可空 NRT 注解。</summary>
    private static void CheckTenantColumnNullable(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        bool isTenantAware = type.GetAttributes().Any(attribute =>
            SourceGenerationValidation.IsPalORMAttribute(attribute, "TenantAware"));
        if (!isTenantAware) return;

        foreach (IPropertySymbol property in SourceGenerationValidation.EnumerateMappedProperties(type))
        {
            var colAttr = property.GetAttributes().FirstOrDefault(a =>
                SourceGenerationValidation.IsPalORMAttribute(a, "Column"));
            if (colAttr?.ConstructorArguments.FirstOrDefault().Value is not "tenant_id") continue;

            // 仅引用类型需要 [Required]——值类型（long/Guid 等）天然不可 null
            bool isReferenceType = property.Type.IsReferenceType
                || property.Type.SpecialType == SpecialType.System_String;
            bool isNullableAnnotation = property.NullableAnnotation == NullableAnnotation.Annotated
                || property.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
            bool hasRequired = property.GetAttributes().Any(a =>
                SourceGenerationValidation.IsPalORMAttribute(a, "Required"));

            // 报告条件：可空注解（string?/long?）或 引用类型无 [Required]
            if (isNullableAnnotation || (isReferenceType && !hasRequired))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(TenantColumnNullable,
                    property.Locations.FirstOrDefault() ?? type.Locations[0],
                    property.Name, type.Name));
            }
            return;  // 只有一个 tenant_id 列
        }
    }

    /// <summary>PALORM036：实体处于 #nullable disable 上下文——RowFactoryEmitter 不生成 IsDBNull 守卫。
    /// 检测策略：编译期 Options.NullableContextOptions 不是 Enable 时报告（项目级未启用 NRT）。
    /// 文件级 #nullable enable 无法在 SymbolAction 中可靠检测（GetNullableContext 是 internal API），
    /// 退而检测项目级——绝大多数场景项目级 NRT 状态决定文件级行为。</summary>
    private static void CheckNullableContext(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        if (ctx.Compilation is not CSharpCompilation csc) return;
        // NullableContextOptions 是 [Flags]：Enable=1, Warnings=2, Annotations=4
        // 仅当含 Enable 标记时才视为启用了 NRT
        if ((csc.Options.NullableContextOptions & NullableContextOptions.Enable) != 0) return;

        // ITM-634：纯值类型实体不受 NRT 语义影响（IsDBNull 守卫只对可空引用/值类型生成）——
        // 无引用类型属性时报告只是噪音。string 属引用类型（可空 string 同样受影响），保留判定。
        bool hasReferenceTypeProperty = SourceGenerationValidation.EnumerateMappedProperties(type)
            .Any(static p => p.Type.IsReferenceType);
        if (!hasReferenceTypeProperty) return;

        ctx.ReportDiagnostic(Diagnostic.Create(NullableContextDisabled,
            type.Locations[0], type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    /// <summary>PALORM023：实体无可插入列——InsertAsync/BulkInsertAsync 运行期必崩。
    /// IsInsertable 等价 TableModel.cs:248-249：!IgnoreOnInsert and !IsAutoIncrement and ComputedExpression is null and !IsTimestamp</summary>
    private static void CheckInsertableColumns(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        bool hasInsertable = false;
        foreach (IPropertySymbol property in SourceGenerationValidation.EnumerateMappedProperties(type))
        {
            if (IsPropertyInsertable(property))
            {
                hasInsertable = true;
                break;
            }
        }
        if (!hasInsertable)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(NoInsertableColumns,
                type.Locations[0], type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    /// <summary>PALORM024：实体无可更新列——UpdateAsync/BulkUpdateAsync 运行期必崩。
    /// IsUpsertable 等价 TableModel.cs:251-252：!IgnoreOnInsert and ComputedExpression is null and !IsTimestamp
    /// 注：非主键属性——主键在 UPDATE 中是 WHERE 条件，不计入 SET 列。</summary>
    private static void CheckUpdatableColumns(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        bool hasUpdatable = false;
        foreach (IPropertySymbol property in SourceGenerationValidation.EnumerateMappedProperties(type))
        {
            if (IsPrimaryKey(property)) continue;  // PK 是 WHERE 条件非 SET
            if (IsPropertyUpdatable(property))
            {
                hasUpdatable = true;
                break;
            }
        }
        if (!hasUpdatable)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(NoUpdatableColumns,
                type.Locations[0], type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    private static bool IsPrimaryKey(IPropertySymbol property)
        => property.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "Key"));

    private static bool IsPropertyInsertable(IPropertySymbol property)
    {
        bool ignoreOnInsert = property.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "IgnoreOnInsert"));
        // Key 的 AutoIncrement 默认 true（Annotations.cs:60）——未显式设置时也视为 true
        var keyAttr = property.GetAttributes()
            .FirstOrDefault(a => SourceGenerationValidation.IsPalORMAttribute(a, "Key"));
        bool isAutoIncrement = keyAttr is null
            ? false
            : !keyAttr.NamedArguments.Any(na => na.Key == "AutoIncrement")  // 未显式设置→默认 true
                || keyAttr.NamedArguments.Any(na => na.Key == "AutoIncrement" && na.Value.Value is true);
        bool hasComputed = property.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Computed"));
        bool isTimestamp = property.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Timestamp"));
        return !ignoreOnInsert && !isAutoIncrement && !hasComputed && !isTimestamp;
    }

    private static bool IsPropertyUpdatable(IPropertySymbol property)
    {
        bool ignoreOnInsert = property.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "IgnoreOnInsert"));
        bool hasComputed = property.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Computed"));
        bool isTimestamp = property.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Timestamp"));
        return !ignoreOnInsert && !hasComputed && !isTimestamp;
    }

    /// <summary>PALORM021：列名大小写不敏感唯一性——重复 [Column] 生成 INSERT INTO t (x, x) 运行期才炸（ITM-409）。
    /// ITM-585 决策登记：大小写不敏感取最严方言口径（同 ITM-510 索引名）。
    /// ITM-601：走基类链（同 PALORM014/018）——派生类用 new 隐藏基类同名属性时，
    /// EnumerateMappedProperties 的 seen HashSet 会按"派生优先"自动跳过基类版本。</summary>
    private static void CheckColumnUniqueness(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        var columnOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in SourceGenerationValidation.EnumerateMappedProperties(type))
        {
            var colAttr = member.GetAttributes().FirstOrDefault(a =>
                SourceGenerationValidation.IsPalORMAttribute(a, "Column"));  // ITM-512
            string columnName = colAttr?.ConstructorArguments.FirstOrDefault().Value as string ?? member.Name;
            if (columnOwners.TryGetValue(columnName, out string? firstOwner))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(DuplicateColumnName,
                    member.Locations.FirstOrDefault() ?? type.Locations[0],
                    firstOwner, member.Name, type.Name, columnName));
            }
            else
            {
                columnOwners.Add(columnName, member.Name);
            }
        }
    }

    /// <summary>PALORM022：[ConcurrencyCheck] 多令牌拒绝 + 类型/可空性校验。
    /// ITM-607：走基类链——派生类继承 AuditBase.Version（基类 [ConcurrencyCheck]）+ 自身
    /// RowVer 时，type.GetMembers() 只查声明类型会漏掉基类令牌。</summary>
    private static void CheckConcurrencyTokens(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        var concurrencyTokens = SourceGenerationValidation.EnumerateMappedProperties(type)
            .Where(member => member.GetAttributes().Any(attribute =>
                SourceGenerationValidation.IsPalORMAttribute(attribute, "ConcurrencyCheck")))  // ITM-512
            .ToArray();
        if (concurrencyTokens.Length > 1)
            ctx.ReportDiagnostic(Diagnostic.Create(MultipleConcurrencyTokens, type.Locations[0], type.Name));

        foreach (IPropertySymbol concurrencyToken in concurrencyTokens)
        {
            // R7 修复：init-only setter 的 [ConcurrencyCheck] 属性无法被 IncrementVersion emit 修改（CS8852）
            if (concurrencyToken.SetMethod?.IsInitOnly == true)
            {
                // ITM-634：setter 问题补消息上下文——描述符文案讲的是类型限制，
                // init-only 场景原文案误导（"must be non-nullable int or long"答非所问）
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidConcurrencyTokenType,
                    concurrencyToken.Locations.FirstOrDefault() ?? type.Locations[0],
                    $"{concurrencyToken.Name}: init-only setter cannot be modified by the generated increment — use a set accessor"));
            }
            if (concurrencyToken.NullableAnnotation == NullableAnnotation.Annotated
                || concurrencyToken.Type.SpecialType is not SpecialType.System_Int32
                    and not SpecialType.System_Int64)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidConcurrencyTokenType,
                    concurrencyToken.Locations.FirstOrDefault() ?? type.Locations[0], concurrencyToken.Name));
            }
        }
    }

    /// <summary>属性级诊断集合——PALORM002/003/004/017/019 单遍遍历检查。
    /// 表名集合按需惰性收集：全程序集遍历是 O(类型数)，对每个 [Table] 无条件执行即 O(N²)（ITM-321）；
    /// 仅在实体确有 [ForeignKey] 引用校验需求时触发。
    /// ITM-590：assemblyTables 由 CompilationStartAction 闭包持有——首次 FK 检查时填充，
    /// 后续所有类型共享同一份缓存（O(N) 而非 O(N²)）。
    /// ITM-607：走基类链（同 PALORM001/002/013/014/018/021）。
    /// v5.0：PALORM025/026/027/034/035/037 同遍加入，PALORM026 用 type.GetMembers() 在
    /// EnumerateMappedProperties 过滤前扫一遍（否则 [NotMapped] 被滤掉检测不到）。</summary>
    private static void CheckPropertyLevelDiagnostics(
        SymbolAnalysisContext ctx, INamedTypeSymbol type, ref HashSet<string>? assemblyTables)
    {
        // PALORM026 必须用 type.GetMembers() 而非 EnumerateMappedProperties——
        // 后者跳过 [NotMapped]，检测不到 [NotMapped]+[Key] 冲突。
        CheckNotMappedConflicts(ctx, type);

        foreach (var member in SourceGenerationValidation.EnumerateMappedProperties(type))
        {
            var memberLocation = member.Locations.FirstOrDefault() ?? type.Locations[0];

            CheckAnnotationNotApplied(ctx, member, memberLocation);              // PALORM017
            CheckOwnedJson(ctx, type, member);                                    // PALORM019
            CheckValueMapping(ctx, member);                                       // 值映射
            CheckColumnNameMismatch(ctx, member, type);                           // PALORM002
            CheckForeignKey(ctx, member, type, memberLocation, ref assemblyTables); // PALORM003/004
            CheckTimestampType(ctx, type, member);                                // PALORM025
            CheckConverterOwnedJsonConflict(ctx, type, member);                   // PALORM027
            CheckKeyNonDefaultValue(ctx, type, member);                           // PALORM034
            CheckConcurrencyCheckWithIgnoreOnInsert(ctx, type, member);           // PALORM035
            CheckRequiredWithNullableAnnotation(ctx, type, member);               // PALORM037
        }
    }

    /// <summary>PALORM026：[NotMapped] 与映射特性互斥。
    /// 用 type.GetMembers()（非 EnumerateMappedProperties）——后者跳过 [NotMapped] 属性。
    /// 互斥清单 14 个：Key/Column/Required/ForeignKey/Unique/Index/ConcurrencyCheck/Timestamp/
    /// Computed/OwnedJson/Converter/SensitiveData/DefaultValue/IgnoreOnInsert。</summary>
    private static void CheckNotMappedConflicts(SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        // 走基类链，但用 GetMembers 而非 EnumerateMappedProperties——保留 [NotMapped] 属性
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (INamedTypeSymbol? current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers())
            {
                if (member is not IPropertySymbol property) continue;
                if (property.IsStatic || property.IsIndexer || property.IsImplicitlyDeclared) continue;
                if (!seen.Add(property.Name)) continue;

                bool isNotMapped = property.GetAttributes().Any(a =>
                    SourceGenerationValidation.IsPalORMAttribute(a, "NotMapped"));
                if (!isNotMapped) continue;

                // 扫描 14 个互斥特性，命中第一个即报
                string[] mappingAttributes =
                    ["Key", "Column", "Required", "ForeignKey", "Unique", "Index",
                     "ConcurrencyCheck", "Timestamp", "Computed", "OwnedJson",
                     "Converter", "SensitiveData", "DefaultValue", "IgnoreOnInsert"];
                foreach (string attrName in mappingAttributes)
                {
                    if (property.GetAttributes().Any(a =>
                        SourceGenerationValidation.IsPalORMAttribute(a, attrName)))
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(NotMappedConflict,
                            property.Locations.FirstOrDefault() ?? type.Locations[0],
                            property.Name, attrName));
                        break;  // 同属性只报一次首个冲突
                    }
                }
            }
        }
    }

    /// <summary>PALORM025：[Timestamp] 标在非时间类型——仅 DateTime/DateTimeOffset 合法。</summary>
    private static void CheckTimestampType(
        SymbolAnalysisContext ctx, INamedTypeSymbol type, IPropertySymbol member)
    {
        bool isTimestamp = member.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Timestamp"));
        if (!isTimestamp) return;

        string typeName = member.Type.ToDisplayString();
        if (typeName is not "System.DateTime" and not "System.DateTimeOffset")
        {
            ctx.ReportDiagnostic(Diagnostic.Create(UnsupportedTimestampType,
                member.Locations.FirstOrDefault() ?? type.Locations[0],
                member.Name, type.Name, typeName));
        }
    }

    /// <summary>PALORM027：[Converter] 与 [OwnedJson] 互斥。</summary>
    private static void CheckConverterOwnedJsonConflict(
        SymbolAnalysisContext ctx, INamedTypeSymbol type, IPropertySymbol member)
    {
        bool hasConverter = member.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Converter"));
        bool hasOwnedJson = member.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "OwnedJson"));
        if (hasConverter && hasOwnedJson)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(ConverterOwnedJsonConflict,
                member.Locations.FirstOrDefault() ?? type.Locations[0], member.Name));
        }
    }

    /// <summary>PALORM034：[Key] 属性有非默认初始值——SaveAsync 永远走 Update 分支。
    /// 例外：[Key(AutoIncrement = false)]（雪花 ID/string key）允许任意初值，不报告。
    /// 例外：string 类型的 "" / null 等同默认值。</summary>
    private static void CheckKeyNonDefaultValue(
        SymbolAnalysisContext ctx, INamedTypeSymbol type, IPropertySymbol member)
    {
        var keyAttr = member.GetAttributes().FirstOrDefault(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Key"));
        if (keyAttr is null) return;

        // AutoIncrement=false 表示用户自定义 ID 生成策略，初值合法
        bool autoIncrementFalse = keyAttr.NamedArguments
            .Any(na => na.Key == "AutoIncrement" && na.Value.Value is false);
        if (autoIncrementFalse) return;

        // 检测属性声明是否有初始化器（= ...;）
        var propertyDecl = member.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault();
        if (propertyDecl?.Initializer is null) return;

        // 初始值文本：对值类型 default/0/null 不报；对 string ""/null 不报
        // ITM-634：白名单补常用等价写法（default! 抑制 NRT / Guid.Empty / MinValue哨兵 / 常量内插）
        string initText = propertyDecl.Initializer.Value.ToString();
        if (initText is "default" or "default!" or "default(long)" or "default(int)" or "default(Guid)"
            or "0" or "0L" or "0l" or "null" or "\"\"" or "string.Empty"
            or "Guid.Empty" or "int.MinValue" or "long.MinValue" or "0u" or "0UL") return;

        ctx.ReportDiagnostic(Diagnostic.Create(KeyWithNonDefaultValue,
            member.Locations.FirstOrDefault() ?? type.Locations[0],
            member.Name, type.Name));
    }

    /// <summary>PALORM035：[ConcurrencyCheck] + [IgnoreOnInsert]——乐观锁基线为 0。</summary>
    private static void CheckConcurrencyCheckWithIgnoreOnInsert(
        SymbolAnalysisContext ctx, INamedTypeSymbol type, IPropertySymbol member)
    {
        bool hasConcurrency = member.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "ConcurrencyCheck"));
        bool hasIgnoreOnInsert = member.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "IgnoreOnInsert"));
        if (hasConcurrency && hasIgnoreOnInsert)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(ConcurrencyCheckWithIgnoreOnInsert,
                member.Locations.FirstOrDefault() ?? type.Locations[0], member.Name));
        }
    }

    /// <summary>PALORM037：[Required] + 可空引用类型注解矛盾。</summary>
    private static void CheckRequiredWithNullableAnnotation(
        SymbolAnalysisContext ctx, INamedTypeSymbol type, IPropertySymbol member)
    {
        bool hasRequired = member.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Required"));
        if (!hasRequired) return;

        bool isNullable = member.NullableAnnotation == NullableAnnotation.Annotated
            || member.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
        if (isNullable)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(RequiredWithNullableAnnotation,
                member.Locations.FirstOrDefault() ?? type.Locations[0],
                member.Name, member.Type.ToDisplayString()));
        }
    }

    /// <summary>PALORM017：不参与迁移 DDL 的属性级注解——消除"标注了但静默无效"。
    /// ADR-B 后 [Index]/[Unique] 已参与索引 DDL，停报；FK/DefaultValue/Column 架构参数仍告警。</summary>
    private static void CheckAnnotationNotApplied(
        SymbolAnalysisContext ctx, IPropertySymbol member, Location memberLocation)
    {
        if (member.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "DefaultValue")))  // ITM-512
            ctx.ReportDiagnostic(Diagnostic.Create(AnnotationNotAppliedToDdl, memberLocation, "[DefaultValue]", member.Name));

        var columnWithSchemaArgs = member.GetAttributes().FirstOrDefault(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Column")  // ITM-512
            && a.NamedArguments.Any(na => na.Key is "Length" or "Precision" or "Scale" or "TypeName" or "StoreAs"));
        if (columnWithSchemaArgs is not null)
            ctx.ReportDiagnostic(Diagnostic.Create(AnnotationNotAppliedToDdl, memberLocation, "[Column] schema arguments (Length/Precision/Scale/TypeName/StoreAs)", member.Name));
    }

    /// <summary>PALORM019：[OwnedJson] 必须是 string 属性 + 有效的 JsonSerializerContext。
    /// 三种失败：声明位置不合法（泛型/嵌套）、缺少上下文、上下文无效。</summary>
    private static void CheckOwnedJson(SymbolAnalysisContext ctx, INamedTypeSymbol type, IPropertySymbol member)
    {
        var ownedJsonAttr = member.GetAttributes().FirstOrDefault(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "OwnedJson"));  // ITM-512
        if (ownedJsonAttr is null || member.Type.SpecialType == SpecialType.System_String) return;

        var location = member.Locations.FirstOrDefault() ?? type.Locations[0];
        var contextType = ownedJsonAttr.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;

        if (type.IsGenericType || type.ContainingType is not null
            || contextType is { IsGenericType: true }
            || contextType?.ContainingType is not null)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(UnsupportedOwnedJsonDeclaration,
                location, member.Name, type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
        else if (contextType is null)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(MissingOwnedJsonContext,
                location, member.Name,
                member.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
        else if (!SourceGenerationValidation.IsValidOwnedJsonContext(contextType, member.Type))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(InvalidOwnedJsonContext,
                location,
                contextType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                member.Name,
                member.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    /// <summary>值映射校验：CLR 类型到 provider 类型的映射必须合法。</summary>
    private static void CheckValueMapping(SymbolAnalysisContext ctx, IPropertySymbol member)
    {
        if (SourceGenerationValidation.HasValidValueMapping(member)) return;

        ctx.ReportDiagnostic(Diagnostic.Create(
            InvalidValueMapping,
            member.Locations.FirstOrDefault() ?? member.ContainingType.Locations[0],
            member.Name,
            member.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    /// <summary>PALORM002：属性无 [Column] 注解时建议添加（除 Id 主键外）。</summary>
    private static void CheckColumnNameMismatch(SymbolAnalysisContext ctx, IPropertySymbol member, INamedTypeSymbol type)
    {
        bool hasColumn = member.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Column"));  // ITM-512
        if (!hasColumn && member.Name != "Id")
        {
            var loc = member.Locations.FirstOrDefault() ?? type.Locations[0];
            ctx.ReportDiagnostic(Diagnostic.Create(ColumnNameMismatch, loc, member.Name));
        }
    }

    /// <summary>PALORM003/004：[ForeignKey] 引用合法性 + OnDelete 必填。
    /// F3 修复：移除 FK 的 PALORM017 无条件报告——[ForeignKey] 在 QueryBuilder.Include 中
    /// 实际有效（JOIN 语义），不构成"静默无效"。PALORM003（引用表存在）+ PALORM004（OnDelete
    /// 缺失）保留——这两条有实际校验价值。
    /// ITM-612：Interlocked.CompareExchange 避免并发下 BuildAssemblyTableNames 被多线程重复调用。</summary>
    private static void CheckForeignKey(
        SymbolAnalysisContext ctx, IPropertySymbol member, INamedTypeSymbol type,
        Location memberLocation, ref HashSet<string>? assemblyTables)
    {
        var fkAttr = member.GetAttributes().FirstOrDefault(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "ForeignKey"));  // ITM-512
        if (fkAttr is null) return;

        // PALORM003：引用不存在的表
        if (fkAttr.ConstructorArguments.Length >= 2)
        {
            string? refTable = fkAttr.ConstructorArguments[0].Value as string;
            if (assemblyTables is null)
                Interlocked.CompareExchange(
                    ref assemblyTables,
                    BuildAssemblyTableNames(type.ContainingAssembly),
                    null);
            if (refTable is not null && !assemblyTables!.Contains(refTable))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(UnknownTable,
                    member.Locations.FirstOrDefault() ?? type.Locations[0], refTable));
            }
        }

        // PALORM004：OnDelete 未显式设置
        if (fkAttr.NamedArguments.All(na => na.Key != "OnDelete"))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(MissingForeignKey,
                member.Locations.FirstOrDefault() ?? type.Locations[0],
                member.Name, type.Name));
        }
    }

    /// <summary>PALORM020：空列 [Index]、同实体重名索引、[Unique] 派生名 ux_ 与显式 [Index] 名冲突。
    /// 跨实体同名索引（SQLite/PG schema 级命名空间下 IF NOT EXISTS 静默跳过）由运行时
    /// MigrateAsync 的重复告警日志兜底——Analyzer 按类型增量执行，跨实体聚合不可靠。</summary>
    private static void ValidateIndexDeclarations(
        SymbolAnalysisContext ctx, INamedTypeSymbol type)
    {
        var tableAttr = type.GetAttributes().FirstOrDefault(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Table"));  // ITM-512
        string tableName = tableAttr?.ConstructorArguments.FirstOrDefault().Value as string ?? type.Name;

        // 索引名冲突按大小写不敏感判定（ITM-510）：MySQL 索引名大小写不敏感，
        // ix_Foo/ix_foo 迁移时报 1061 被 IsDuplicateSchemaObject 幂等吞掉→第二索引静默丢失。
        // 用最严格的方言口径统一，保证三方言下均无碰撞。
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ITM-565：索引列必须存在于实体列集（含 [Column] 重命名与基类链）——列名拼错
        // 此前编译期静默，MigrateAsync 运行期才报"列不存在"（1170 类错误不被幂等兜底吞）。
        var knownColumns = BuildKnownColumnNames(type);

        // [Unique] 派生索引名先占位（与 TableModel 的 ux_{table}_{column} 命名一致）
        ReserveUniqueDerivedIndexNames(ctx, type, tableName, seenNames);

        ValidateIndexAttributes(ctx, type, seenNames, knownColumns);
    }

    /// <summary>构建实体已知列名集（含 [Column] 重命名与基类链，ITM-607）。</summary>
    private static HashSet<string> BuildKnownColumnNames(INamedTypeSymbol type)
    {
        var knownColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IPropertySymbol property in SourceGenerationValidation.EnumerateMappedProperties(type))
        {
            var propColumnAttr = property.GetAttributes().FirstOrDefault(a =>
                SourceGenerationValidation.IsPalORMAttribute(a, "Column"));  // ITM-512
            knownColumns.Add(propColumnAttr?.ConstructorArguments.FirstOrDefault().Value as string
                ?? property.Name);
        }
        return knownColumns;
    }

    /// <summary>[Unique] 派生索引名占位——与 TableModel 的 ux_{table}_{column} 命名一致。
    /// ITM-607: 走基类链（同其他 PALORM 检查）——基类 [Unique] 属性同样需占位检查。</summary>
    private static void ReserveUniqueDerivedIndexNames(
        SymbolAnalysisContext ctx, INamedTypeSymbol type, string tableName, HashSet<string> seenNames)
    {
        foreach (var member in SourceGenerationValidation.EnumerateMappedProperties(type))
        {
            if (!member.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "Unique")))  // ITM-512
                continue;
            var columnAttr = member.GetAttributes().FirstOrDefault(a =>
                SourceGenerationValidation.IsPalORMAttribute(a, "Column"));  // ITM-512
            string columnName = columnAttr?.ConstructorArguments.FirstOrDefault().Value as string ?? member.Name;
            string derivedName = $"ux_{tableName}_{columnName}";
            if (!seenNames.Add(derivedName))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidIndexDeclaration,
                    member.Locations.FirstOrDefault() ?? type.Locations[0],
                    $"[Unique] on '{member.Name}' derives index name '{derivedName}' which is already used on this entity"));
            }
        }
    }

    /// <summary>验证所有 [Index] 属性——名称/列/已知列引用/同名冲突。</summary>
    private static void ValidateIndexAttributes(
        SymbolAnalysisContext ctx, INamedTypeSymbol type,
        HashSet<string> seenNames, HashSet<string> knownColumns)
    {
        foreach (var indexAttr in type.GetAttributes().Where(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Index")))  // ITM-512
        {
            var location = indexAttr.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation()
                ?? type.Locations[0];

            if (indexAttr.ConstructorArguments.Length < 2
                || indexAttr.ConstructorArguments[0].Value is not string indexName
                || string.IsNullOrWhiteSpace(indexName))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidIndexDeclaration, location,
                    $"[Index] on '{type.Name}' has no valid name; declare [Index(\"name\", \"col1\", ...)]"));
                continue;
            }

            if (!TryGetIndexColumns(indexAttr, out string[] columns))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidIndexDeclaration, location,
                    $"[Index(\"{indexName}\")] on '{type.Name}' declares no columns; it would be silently dropped"));
                continue;
            }

            CheckIndexColumnReferences(ctx, type, location, indexName, columns, knownColumns);

            if (!seenNames.Add(indexName))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidIndexDeclaration, location,
                    $"Index name '{indexName}' is declared more than once on '{type.Name}'"));
            }
        }
    }

    /// <summary>提取 [Index] 的列名数组；返回 false 表示列名为空。</summary>
    private static bool TryGetIndexColumns(AttributeData indexAttr, out string[] columns)
    {
        columns = indexAttr.ConstructorArguments[1].Values
            .Select(static v => v.Value as string)
            .Where(static v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToArray();
        return columns.Length > 0;
    }

    /// <summary>校验索引引用的列都在 knownColumns 中（ITM-565）。</summary>
    private static void CheckIndexColumnReferences(
        SymbolAnalysisContext ctx, INamedTypeSymbol type, Location location,
        string indexName, string[] columns, HashSet<string> knownColumns)
    {
        foreach (string column in columns)
        {
            if (!knownColumns.Contains(column))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidIndexDeclaration, location,
                    $"[Index(\"{indexName}\")] on '{type.Name}' references column '{column}' " +
                    "which does not exist on the entity; CREATE INDEX would fail at MigrateAsync"));
            }
        }
    }

    // ITM-590: 跨类型共享程序集表名缓存——此前 GetAssemblyTableNames 每类型独立扫描整个
    // 程序集（O(N×M)），大型项目编译期可感知。改为编译期内通过 CompilationStartAction
    // 注册的局部缓存（由 Roslyn 管理生命周期，符合 RS1008：不存储编译期符号到分析器字段）。
    private static HashSet<string> BuildAssemblyTableNames(IAssemblySymbol assembly)
    {
        var names = new HashSet<string>();
        foreach (var module in assembly.Modules)
        {
            foreach (var type in GetAllTypes(module.GlobalNamespace))
            {
                var tableAttr = type.GetAttributes().FirstOrDefault(a =>
                    SourceGenerationValidation.IsPalORMAttribute(a, "Table"));  // ITM-512
                if (tableAttr?.ConstructorArguments.FirstOrDefault().Value is string name)
                    names.Add(name);
            }
        }
        return names;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            yield return type;
        foreach (var childNs in ns.GetNamespaceMembers())
            foreach (var type in GetAllTypes(childNs))
                yield return type;
    }
}
