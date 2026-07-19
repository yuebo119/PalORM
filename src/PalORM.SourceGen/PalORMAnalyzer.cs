using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PalORM.SourceGen;

/// <summary>PalORM 编译时验证——V1-V6 诊断规则。</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PalORMAnalyzer : DiagnosticAnalyzer
{
    // PALORM001: Missing primary key
    public static readonly DiagnosticDescriptor MissingPrimaryKey = new(
        "PALORM001", "Type has [Table] but no [Key] property",
        "Type '{0}' has [Table] attribute but no property with [Key] attribute", "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor ColumnNameMismatch = new(
        "PALORM002", "Column name does not match table schema",
        "Property '{0}' column name does not match table schema", "PalORM", DiagnosticSeverity.Warning, true);

    // ITM-585: 表名扫描仅覆盖本程序集（GetAssemblyTableNames）——多程序集实体布局下
    // 引用他程序集表会误报。Analyzer 按类型增量执行、跨编译引用聚合不可靠，多程序集
    // 场景可 .editorconfig 降级本诊断（dotnet_diagnostic.PALORM003.severity = suggestion）。
    public static readonly DiagnosticDescriptor UnknownTable = new(
        "PALORM003", "Foreign key references unknown table",
        "[ForeignKey] references table '{0}' but no [Table] attribute found for it", "PalORM", DiagnosticSeverity.Error, true);

    // ITM-525：FK 约束 DDL 当前不由 MigrateAsync 生成（ForeignKeys 收集为未来兼容保留），
    // 故本诊断只提示 OnDelete 声明是为未来 FK DDL 预留，当前不产生任何运行时约束效果。
    public static readonly DiagnosticDescriptor MissingForeignKey = new(
        "PALORM004", "Foreign key does not declare OnDelete behavior",
        "[ForeignKey] on '{0}' (type '{1}') does not set OnDelete; note FK constraint DDL is not generated in the current version, so OnDelete is recorded for future compatibility only and has no runtime effect yet", "PalORM", DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor NPlusOneDetected = new(
        "PALORM005", "Potential N+1 query pattern detected",
        "From<T>() called inside a loop may cause N+1 queries. Consider using JOIN or WhereIn.", "PalORM", DiagnosticSeverity.Warning, true);

    // ITM-581: PALORM006/007 为零报告点的占位描述符——006 由 SqlFileEmitter 的
    // Obsolete-error 机制实际承担，007 无 schema 对照数据源。保留占位防编号复用歧义；
    // 3.0 与其它死成员一并裁决（AnalyzerDiagnosticsTests 已固化现状）。
    public static readonly DiagnosticDescriptor SqlFileNotFound = new(
        "PALORM006", "Referenced SQL file does not exist",
        "[SqlFile] references '{0}' but the file was not found", "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor SchemaMismatch = new(
        "PALORM007", "Column type mismatch between entity and database",
        "Column '{0}' type mismatch between entity and database schema", "PalORM", DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor MissingOwnedJsonContext = new(
        "PALORM008", "Object OwnedJson requires a source-generated JSON context",
        "Property '{0}' uses [OwnedJson] with object type '{1}' but does not specify a JsonSerializerContext",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidOwnedJsonContext = new(
        "PALORM009", "OwnedJson context is not valid for the property type",
        "OwnedJson context '{0}' for property '{1}' must derive from JsonSerializerContext and declare [JsonSerializable(typeof({2}))]",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedOwnedJsonDeclaration = new(
        "PALORM010", "OwnedJson declaration is not supported by source generation",
        "Property '{0}' uses [OwnedJson] in unsupported declaration '{1}'; entities and JSON contexts must be non-generic top-level types",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedQualifiedTable = new(
        "PALORM011", "Schema and database-qualified tables are not supported",
        "Type '{0}' configures Schema or Database, but qualified table names are not supported by the generated CRUD and migration pipeline",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidConcurrencyTokenType = new(
        "PALORM012", "Concurrency token type is not supported",
        "Property '{0}' uses [ConcurrencyCheck], but only non-nullable int and long are supported",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MultipleConcurrencyTokens = new(
        "PALORM013", "Multiple concurrency tokens are not supported",
        "Type '{0}' declares multiple [ConcurrencyCheck] properties; exactly zero or one is supported",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MissingSoftDeleteColumn = new(
        "PALORM014", "Soft-delete entity requires a deleted_at column",
        "Type '{0}' uses [SoftDelete] but does not map a property to column 'deleted_at'",
        "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedEntityDeclaration = new(
        "PALORM015", "Entity declaration is not supported by source generation",
        "Type '{0}' uses [Table] in an unsupported declaration; entities must be concrete non-generic top-level classes with a public parameterless constructor and writable mapped properties",
        "PalORM", DiagnosticSeverity.Error, true);

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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [MissingPrimaryKey, ColumnNameMismatch, UnknownTable, MissingForeignKey,
         NPlusOneDetected, SqlFileNotFound, SchemaMismatch, MissingOwnedJsonContext,
         InvalidOwnedJsonContext, UnsupportedOwnedJsonDeclaration, UnsupportedQualifiedTable,
         InvalidConcurrencyTokenType, MultipleConcurrencyTokens, MissingSoftDeleteColumn,
         UnsupportedEntityDeclaration, InvalidValueMapping, AnnotationNotAppliedToDdl,
         MissingTenantColumn, CompositePrimaryKey, InvalidIndexDeclaration, DuplicateColumnName,
         InvalidKeyDeclaration];

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
            // ITM-559: 计数走基类链——只查声明成员会对"基类声明 [Key]"的实体误报"无 [Key]"，
            // 与列收集（GetMappableProperties 基类链）不一致
            List<IPropertySymbol> keyProperties = SourceGenerationValidation
                .EnumerateMappedProperties(type)
                .Where(static property => property.GetAttributes().Any(attribute =>
                    SourceGenerationValidation.IsPalORMAttribute(attribute, "Key")))  // ITM-512
                .ToList();
            if (keyProperties.Count == 0)
                ctx.ReportDiagnostic(Diagnostic.Create(MissingPrimaryKey, type.Locations[0], type.Name));
            // PALORM019: 复合主键——BindDelete 单 key 语义无法表达，明确拒绝（ITM-311）
            else if (keyProperties.Count > 1)
                ctx.ReportDiagnostic(Diagnostic.Create(CompositePrimaryKey, type.Locations[0], type.Name, keyProperties.Count));
            else
            {
                // PALORM022（ITM-560/561）: 生成器 CanGenerateEntity 会静默跳过的主键形态在此定位报错
                IPropertySymbol key = keyProperties[0];
                string? reason = key.SetMethod is null or { IsInitOnly: true }
                    ? "has an init-only or missing setter (generated ID backfill requires a writable setter)"
                    : key.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }
                        ? "is a nullable value type (generated key binding cannot compile)"
                        : null;
                if (reason is not null)
                    ctx.ReportDiagnostic(Diagnostic.Create(InvalidKeyDeclaration,
                        key.Locations.FirstOrDefault() ?? type.Locations[0], key.Name, type.Name, reason));
            }
        }, SymbolKind.NamedType);

        // PALORM002 + PALORM003 + PALORM004: 表级验证
        context.RegisterSymbolAction(ctx =>
        {
            if (ctx.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } type) return;
            var tableAttribute = type.GetAttributes().FirstOrDefault(a => SourceGenerationValidation.IsPalORMAttribute(a, "Table"));  // ITM-512
            if (tableAttribute is null) return;

            if (!SourceGenerationValidation.IsSupportedEntity(type))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedEntityDeclaration,
                    type.Locations[0],
                    type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                return;
            }

            bool hasQualifiedTable = tableAttribute.NamedArguments.Any(argument =>
                    argument.Key is "Schema" or "Database" && argument.Value.Value is string)
                || type.GetAttributes().Any(attribute =>
                    SourceGenerationValidation.IsPalORMAttribute(attribute, "Schema")
                    || SourceGenerationValidation.IsPalORMAttribute(attribute, "Database"));  // ITM-512
            if (hasQualifiedTable)
                ctx.ReportDiagnostic(Diagnostic.Create(UnsupportedQualifiedTable, type.Locations[0], type.Name));

            bool isSoftDelete = type.GetAttributes().Any(attribute =>
                SourceGenerationValidation.IsPalORMAttribute(attribute, "SoftDelete"));  // ITM-512
            // ITM-587: 走基类链（与 TableModel.GetMappableProperties 口径一致）——派生类继承
            // AuditBase 把 deleted_at 放基类时，type.GetMembers() 只查声明类型会误报 PALORM014。
            bool hasSoftDeleteColumn = SourceGenerationValidation.EnumerateMappedProperties(type)
                .Any(static property => property.GetAttributes().Any(attribute =>
                    SourceGenerationValidation.IsPalORMAttribute(attribute, "Column")
                    && attribute.ConstructorArguments.FirstOrDefault().Value is "deleted_at"));  // ITM-512
            if (isSoftDelete && !hasSoftDeleteColumn)
                ctx.ReportDiagnostic(Diagnostic.Create(MissingSoftDeleteColumn, type.Locations[0], type.Name));

            // PALORM018: [TenantAware] 必须映射 tenant_id 列（与 PALORM014 对齐）
            bool isTenantAware = type.GetAttributes().Any(attribute =>
                SourceGenerationValidation.IsPalORMAttribute(attribute, "TenantAware"));  // ITM-512
            // ITM-588: 同 ITM-587——走基类链覆盖 TenantBase 放 tenant_id 的派生类。
            bool hasTenantColumn = SourceGenerationValidation.EnumerateMappedProperties(type)
                .Any(static property => property.GetAttributes().Any(attribute =>
                    SourceGenerationValidation.IsPalORMAttribute(attribute, "Column")
                    && attribute.ConstructorArguments.FirstOrDefault().Value is "tenant_id"));  // ITM-512
            if (isTenantAware && !hasTenantColumn)
                ctx.ReportDiagnostic(Diagnostic.Create(MissingTenantColumn, type.Locations[0], type.Name));

            // PALORM020: 索引声明有效性——消除 TableModel 静默丢弃与 IF NOT EXISTS/1061 掩蔽（ITM-203）
            ValidateIndexDeclarations(ctx, type);

            // PALORM021: 列名唯一性——重复 [Column] 生成 INSERT INTO t (x, x) 运行期才炸（ITM-409）。
            // ITM-585 决策登记：大小写不敏感取最严方言口径（同 ITM-510 索引名）——MySQL 列名
            // 大小写不敏感，"Name"/"name" 两列在 MySQL 建表即冲突；PG 引号标识符虽区分大小写,
            // 但依赖大小写区分的两列是跨方言可移植性陷阱，统一拒绝。
            // ITM-601: 走基类链（同 PALORM014/018）——派生类用 new 隐藏基类同名属性时,
            // EnumerateMappedProperties 的 seen HashSet 会按"派生优先"自动跳过基类版本,
            // 与 TableModel 列收集口径一致。
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

            var concurrencyTokens = type.GetMembers().OfType<IPropertySymbol>()
                .Where(static property => !SourceGenerationValidation.IsNotMapped(property))
                .Where(member => member.GetAttributes().Any(attribute =>
                    SourceGenerationValidation.IsPalORMAttribute(attribute, "ConcurrencyCheck")))  // ITM-512
                .ToArray();
            if (concurrencyTokens.Length > 1)
                ctx.ReportDiagnostic(Diagnostic.Create(MultipleConcurrencyTokens, type.Locations[0], type.Name));
            foreach (IPropertySymbol concurrencyToken in concurrencyTokens)
            {
                if (concurrencyToken.NullableAnnotation == NullableAnnotation.Annotated
                    || concurrencyToken.Type.SpecialType is not SpecialType.System_Int32
                        and not SpecialType.System_Int64)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(InvalidConcurrencyTokenType,
                        concurrencyToken.Locations.FirstOrDefault() ?? type.Locations[0], concurrencyToken.Name));
                }
            }

            // 表名集合按需惰性收集：全程序集遍历是 O(类型数) 的，若对每个 [Table] 实体
            // 无条件执行即 O(N²)（ITM-321）；仅在实体确有 [ForeignKey] 引用校验需求时触发
            HashSet<string>? assemblyTables = null;

            foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (SourceGenerationValidation.IsNotMapped(member))
                    continue;

                // PALORM017: 不参与迁移 DDL 的属性级注解——消除"标注了但静默无效"
                // （ADR-B 后 [Index]/[Unique] 已参与索引 DDL，停报；FK/DefaultValue/Column 架构参数仍告警）
                var memberLocation = member.Locations.FirstOrDefault() ?? type.Locations[0];
                if (member.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "DefaultValue")))  // ITM-512
                    ctx.ReportDiagnostic(Diagnostic.Create(AnnotationNotAppliedToDdl, memberLocation, "[DefaultValue]", member.Name));
                var columnWithSchemaArgs = member.GetAttributes().FirstOrDefault(a =>
                    SourceGenerationValidation.IsPalORMAttribute(a, "Column")  // ITM-512
                    && a.NamedArguments.Any(na => na.Key is "Length" or "Precision" or "Scale" or "TypeName" or "StoreAs"));
                if (columnWithSchemaArgs is not null)
                    ctx.ReportDiagnostic(Diagnostic.Create(AnnotationNotAppliedToDdl, memberLocation, "[Column] schema arguments (Length/Precision/Scale/TypeName/StoreAs)", member.Name));

                // PALORM002: 属性无 [Column] 注解时建议添加
                bool hasColumn = member.GetAttributes().Any(a =>
                    SourceGenerationValidation.IsPalORMAttribute(a, "Column"));  // ITM-512
                var ownedJsonAttr = member.GetAttributes().FirstOrDefault(a =>
                    SourceGenerationValidation.IsPalORMAttribute(a, "OwnedJson"));  // ITM-512
                if (ownedJsonAttr is not null
                    && member.Type.SpecialType != SpecialType.System_String)
                {
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
                    else if (!SourceGenerationValidation.IsValidOwnedJsonContext(
                        contextType, member.Type))
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(InvalidOwnedJsonContext,
                            location,
                            contextType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            member.Name,
                            member.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                    }
                }

                if (!SourceGenerationValidation.HasValidValueMapping(member))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        InvalidValueMapping,
                        member.Locations.FirstOrDefault() ?? type.Locations[0],
                        member.Name,
                        member.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }

                if (!hasColumn && member.Name != "Id")
                {
                    var loc = member.Locations.FirstOrDefault() ?? type.Locations[0];
                    ctx.ReportDiagnostic(Diagnostic.Create(ColumnNameMismatch, loc, member.Name));
                }

                // PALORM003: [ForeignKey] 引用不存在的表
                var fkAttr = member.GetAttributes().FirstOrDefault(a =>
                    SourceGenerationValidation.IsPalORMAttribute(a, "ForeignKey"));  // ITM-512
                if (fkAttr is not null)
                {
                    // PALORM017: FK 约束 DDL 当前不被 MigrateAsync 执行
                    ctx.ReportDiagnostic(Diagnostic.Create(AnnotationNotAppliedToDdl,
                        memberLocation, "[ForeignKey]", member.Name));
                }
                if (fkAttr?.ConstructorArguments.Length >= 2)
                {
                    string? refTable = fkAttr.ConstructorArguments[0].Value as string;
                    assemblyTables ??= GetAssemblyTableNames(type.ContainingAssembly);
                    if (refTable is not null && !assemblyTables.Contains(refTable))
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(UnknownTable,
                            member.Locations.FirstOrDefault() ?? type.Locations[0], refTable));
                    }
                }

                // PALORM004: [ForeignKey] 但 OnDelete 未显式设置
                if (fkAttr is not null && fkAttr.NamedArguments.All(na => na.Key != "OnDelete"))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(MissingForeignKey,
                        member.Locations.FirstOrDefault() ?? type.Locations[0],
                        member.Name, type.Name));
                }
            }
        }, SymbolKind.NamedType);

        // PALORM005: N+1 检测 — 循环中 From<T>() / ORM 调用
        // ITM-574：语法名匹配会误报 EF Core/MongoDB 等第三方库的同名方法（ToListAsync 等），
        // TreatWarningsAsErrors 项目直接阻断——语义模型确认接收者/方法归属 PalORM 后才报。
        context.RegisterSyntaxNodeAction(ctx =>
        {
            var invocation = (InvocationExpressionSyntax)ctx.Node;
            if (invocation.Expression is MemberAccessExpressionSyntax ma)
            {
                string methodName = ma.Name.Identifier.Text;
                if (methodName is "From" or "InsertAsync" or "UpdateAsync" or "DeleteAsync"
                    or "ToListAsync" or "FirstAsync" or "SingleAsync")
                {
                    if (ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol
                            is not IMethodSymbol methodSymbol
                        || methodSymbol.ContainingNamespace?.ToDisplayString() is not { } containingNs
                        || (containingNs != "PalORM"
                            && !containingNs.StartsWith("PalORM.", StringComparison.Ordinal)))
                    {
                        return;
                    }
                    var parent = invocation.Parent;
                    while (parent is not null)
                    {
                        // ITM-574：循环体内定义、循环外执行的 lambda/局部函数不是 N+1——
                        // 遇到函数边界即停止向上找循环
                        if (parent is LambdaExpressionSyntax or LocalFunctionStatementSyntax
                            or AnonymousMethodExpressionSyntax)
                            break;
                        if (parent is ForStatementSyntax or ForEachStatementSyntax
                            or WhileStatementSyntax or DoStatementSyntax)
                        {
                            ctx.ReportDiagnostic(Diagnostic.Create(NPlusOneDetected, invocation.GetLocation()));
                            break;
                        }
                        parent = parent.Parent;
                    }
                }
            }
        }, SyntaxKind.InvocationExpression);
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
        var knownColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IPropertySymbol property in SourceGenerationValidation.EnumerateMappedProperties(type))
        {
            var propColumnAttr = property.GetAttributes().FirstOrDefault(a =>
                SourceGenerationValidation.IsPalORMAttribute(a, "Column"));  // ITM-512
            knownColumns.Add(propColumnAttr?.ConstructorArguments.FirstOrDefault().Value as string
                ?? property.Name);
        }

        // [Unique] 派生索引名先占位（与 TableModel 的 ux_{table}_{column} 命名一致）
        foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (SourceGenerationValidation.IsNotMapped(member)) continue;
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
            string[] columns = indexAttr.ConstructorArguments[1].Values
                .Select(static v => v.Value as string)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .ToArray();
            if (columns.Length == 0)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidIndexDeclaration, location,
                    $"[Index(\"{indexName}\")] on '{type.Name}' declares no columns; it would be silently dropped"));
                continue;
            }
            foreach (string column in columns)
            {
                if (!knownColumns.Contains(column))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(InvalidIndexDeclaration, location,
                        $"[Index(\"{indexName}\")] on '{type.Name}' references column '{column}' " +
                        "which does not exist on the entity; CREATE INDEX would fail at MigrateAsync"));
                }
            }
            if (!seenNames.Add(indexName))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidIndexDeclaration, location,
                    $"Index name '{indexName}' is declared more than once on '{type.Name}'"));
            }
        }
    }

    private static HashSet<string> GetAssemblyTableNames(IAssemblySymbol assembly)
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
