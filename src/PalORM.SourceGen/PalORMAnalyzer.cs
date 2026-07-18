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

    public static readonly DiagnosticDescriptor UnknownTable = new(
        "PALORM003", "Foreign key references unknown table",
        "[ForeignKey] references table '{0}' but no [Table] attribute found for it", "PalORM", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MissingForeignKey = new(
        "PALORM004", "Foreign key does not declare OnDelete behavior",
        "[ForeignKey] on '{0}' (type '{1}') does not explicitly set OnDelete; declare the delete behavior to avoid dialect-dependent defaults", "PalORM", DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor NPlusOneDetected = new(
        "PALORM005", "Potential N+1 query pattern detected",
        "From<T>() called inside a loop may cause N+1 queries. Consider using JOIN or WhereIn.", "PalORM", DiagnosticSeverity.Warning, true);

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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [MissingPrimaryKey, ColumnNameMismatch, UnknownTable, MissingForeignKey,
         NPlusOneDetected, SqlFileNotFound, SchemaMismatch, MissingOwnedJsonContext,
         InvalidOwnedJsonContext, UnsupportedOwnedJsonDeclaration, UnsupportedQualifiedTable,
         InvalidConcurrencyTokenType, MultipleConcurrencyTokens, MissingSoftDeleteColumn,
         UnsupportedEntityDeclaration, InvalidValueMapping, AnnotationNotAppliedToDdl,
         MissingTenantColumn, CompositePrimaryKey];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // PALORM001: Missing primary key
        context.RegisterSymbolAction(ctx =>
        {
            if (ctx.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } type
                || !SourceGenerationValidation.IsSupportedEntity(type)
                || !type.GetAttributes().Any(a => a.AttributeClass?.Name is "TableAttribute" or "Table"))
                return;
            int keyCount = type.GetMembers().OfType<IPropertySymbol>()
                .Where(static property => !SourceGenerationValidation.IsNotMapped(property))
                .Count(static property => property.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.Name is "KeyAttribute" or "Key"));
            if (keyCount == 0)
                ctx.ReportDiagnostic(Diagnostic.Create(MissingPrimaryKey, type.Locations[0], type.Name));
            // PALORM019: 复合主键——BindDelete 单 key 语义无法表达，明确拒绝（ITM-311）
            else if (keyCount > 1)
                ctx.ReportDiagnostic(Diagnostic.Create(CompositePrimaryKey, type.Locations[0], type.Name, keyCount));
        }, SymbolKind.NamedType);

        // PALORM002 + PALORM003 + PALORM004: 表级验证
        context.RegisterSymbolAction(ctx =>
        {
            if (ctx.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } type) return;
            var tableAttribute = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "TableAttribute" or "Table");
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
                    attribute.AttributeClass?.Name is "SchemaAttribute" or "Schema" or "DatabaseAttribute" or "Database");
            if (hasQualifiedTable)
                ctx.ReportDiagnostic(Diagnostic.Create(UnsupportedQualifiedTable, type.Locations[0], type.Name));

            bool isSoftDelete = type.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.Name is "SoftDeleteAttribute" or "SoftDelete");
            bool hasSoftDeleteColumn = type.GetMembers().OfType<IPropertySymbol>()
                .Where(static property => !SourceGenerationValidation.IsNotMapped(property))
                .Any(static property => property.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.Name is "ColumnAttribute" or "Column"
                    && attribute.ConstructorArguments.FirstOrDefault().Value is "deleted_at"));
            if (isSoftDelete && !hasSoftDeleteColumn)
                ctx.ReportDiagnostic(Diagnostic.Create(MissingSoftDeleteColumn, type.Locations[0], type.Name));

            // PALORM018: [TenantAware] 必须映射 tenant_id 列（与 PALORM014 对齐）
            bool isTenantAware = type.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.Name is "TenantAwareAttribute" or "TenantAware");
            bool hasTenantColumn = type.GetMembers().OfType<IPropertySymbol>()
                .Where(static property => !SourceGenerationValidation.IsNotMapped(property))
                .Any(static property => property.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.Name is "ColumnAttribute" or "Column"
                    && attribute.ConstructorArguments.FirstOrDefault().Value is "tenant_id"));
            if (isTenantAware && !hasTenantColumn)
                ctx.ReportDiagnostic(Diagnostic.Create(MissingTenantColumn, type.Locations[0], type.Name));

            var concurrencyTokens = type.GetMembers().OfType<IPropertySymbol>()
                .Where(static property => !SourceGenerationValidation.IsNotMapped(property))
                .Where(member => member.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.Name is "ConcurrencyCheckAttribute" or "ConcurrencyCheck"))
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
                if (member.GetAttributes().Any(a => a.AttributeClass?.Name is "DefaultValueAttribute" or "DefaultValue"))
                    ctx.ReportDiagnostic(Diagnostic.Create(AnnotationNotAppliedToDdl, memberLocation, "[DefaultValue]", member.Name));
                var columnWithSchemaArgs = member.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.Name is "ColumnAttribute" or "Column"
                    && a.NamedArguments.Any(na => na.Key is "Length" or "Precision" or "Scale" or "TypeName" or "StoreAs"));
                if (columnWithSchemaArgs is not null)
                    ctx.ReportDiagnostic(Diagnostic.Create(AnnotationNotAppliedToDdl, memberLocation, "[Column] schema arguments (Length/Precision/Scale/TypeName/StoreAs)", member.Name));

                // PALORM002: 属性无 [Column] 注解时建议添加
                bool hasColumn = member.GetAttributes().Any(a =>
                    a.AttributeClass?.Name is "ColumnAttribute" or "Column");
                var ownedJsonAttr = member.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.Name is "OwnedJsonAttribute" or "OwnedJson");
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
                    a.AttributeClass?.Name is "ForeignKeyAttribute" or "ForeignKey");
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
        context.RegisterSyntaxNodeAction(ctx =>
        {
            var invocation = (InvocationExpressionSyntax)ctx.Node;
            if (invocation.Expression is MemberAccessExpressionSyntax ma)
            {
                string methodName = ma.Name.Identifier.Text;
                if (methodName is "From" or "InsertAsync" or "UpdateAsync" or "DeleteAsync"
                    or "ToListAsync" or "FirstAsync" or "SingleAsync")
                {
                    var parent = invocation.Parent;
                    while (parent is not null)
                    {
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

    private static HashSet<string> GetAssemblyTableNames(IAssemblySymbol assembly)
    {
        var names = new HashSet<string>();
        foreach (var module in assembly.Modules)
        {
            foreach (var type in GetAllTypes(module.GlobalNamespace))
            {
                var tableAttr = type.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.Name is "TableAttribute" or "Table");
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
