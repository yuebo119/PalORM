using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalORM.SourceGen;

/// <summary>源生成器数据模型——从 [Table] 注解提取的编译时元数据。</summary>
internal sealed record TableModel(
    string Namespace,
    string ClassName,
    string EntityTypeName,
    string GeneratedTypeSuffix,
    string TableName,
    bool IsSoftDelete,
    bool IsTenantAware,
    EquatableArray<ColumnModel> Columns,
    EquatableArray<IndexModel> Indexes,
    EquatableArray<ForeignKeyModel> ForeignKeys)
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "TableModel 一次性收集所有注解元数据（Column/Key/ForeignKey/Index/Converter/Computed）。"
            + "大 foreach 内的多分支是必然——按注解类型拆 BuildColumn/BuildForeignKey/BuildCompositeIndex "
            + "会把单列构建拆到 3 个方法，调用关系复杂。当前结构按属性顺序线性阅读，更清晰。")]
    public static TableModel? FromContext(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol) return null;
        if (typeSymbol.TypeKind != TypeKind.Class) return null;
        if (!SourceGenerationValidation.CanGenerateEntity(typeSymbol)) return null;

        // ITM-512：注解匹配全程校验命名空间为 PalORM（IsPalORMAttribute），
        // 避免混挂 EF Core/System.ComponentModel.DataAnnotations 同名注解时误判。
        var tableAttr = typeSymbol.GetAttributes().FirstOrDefault(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Table"));
        if (tableAttr is null) return null;

        string tableName = tableAttr.ConstructorArguments.FirstOrDefault().Value as string
            ?? typeSymbol.Name;

        List<ColumnModel> columns = [];
        List<IndexModel> indexes = [];
        List<ForeignKeyModel> foreignKeys = [];

        foreach (var member in GetMappableProperties(typeSymbol))
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "NotMapped")))
                continue;

            var columnAttr = prop.GetAttributes().FirstOrDefault(a =>
                SourceGenerationValidation.IsPalORMAttribute(a, "Column"));
            string columnName = columnAttr?.ConstructorArguments.FirstOrDefault().Value as string
                ?? prop.Name;
            // ITM-553 待实现：[Column(StoreAs=...)] 的枚举存储策略（AsInt32/AsInt64/AsString）在此未被读取，
            // enum 列恒走默认 TEXT 存储。用户已由 PALORM017（PalORMAnalyzer.cs AnnotationNotAppliedToDdl，
            // 谓词含 "StoreAs"）在编译期告警"标注但静默无效"，故无静默错误风险。完整接入需扩展类型映射
            // （provider 类型选择 + 读/写表达式 + DDL 列类型）面较大，留待专门迭代，此处仅记录限制。

            // [Unique] → 单列唯一索引（ADR-B：属性级 Unique 升为唯一索引）
            if (prop.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "Unique")))
            {
                indexes.Add(new IndexModel(
                    $"ux_{tableName}_{columnName}",
                    new EquatableArray<string>(new[] { columnName }),
                    Unique: true));
            }

            bool isKey = prop.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "Key"));
            // [Key(AutoIncrement = false)] 关闭数值主键的自增推断（雪花 ID 等应用侧赋值主键）
            bool autoIncrementEnabled = prop.GetAttributes()
                .FirstOrDefault(a => SourceGenerationValidation.IsPalORMAttribute(a, "Key"))?
                .NamedArguments.FirstOrDefault(na => na.Key == "AutoIncrement").Value.Value is not false;
            bool isAutoIncrement = isKey && autoIncrementEnabled
                && prop.Type.SpecialType is SpecialType.System_Int64 or SpecialType.System_Int32;
            bool ignoreOnInsert = prop.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "IgnoreOnInsert"));
            bool isConcurrencyToken = prop.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "ConcurrencyCheck"));
            bool isTimestamp = prop.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "Timestamp"));
            bool isRequired = prop.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, "Required"));
            // ITM-554：改用本文件 helper（ITM-512 引入），与其余注解判定一致，避免裸串命名空间比对
            string? computedExpression = prop.GetAttributes()
                .FirstOrDefault(static attribute =>
                    SourceGenerationValidation.IsPalORMAttribute(attribute, "Computed"))?
                .ConstructorArguments.FirstOrDefault().Value as string;
            // ITM-584：[Computed] 表达式是编译期常量（Raw 同级信任），但 NUL/未配对括号会生成
    // 非法 DDL 延迟到 MigrateAsync 才炸——快检拒绝，实体整体跳过（PALORM015 兜底提示）
            if (computedExpression is not null
                && (computedExpression.Contains('\0')
                    || computedExpression.Count(static c => c == '(')
                        != computedExpression.Count(static c => c == ')')))
                return null;
            var ownedJsonAttr = prop.GetAttributes().FirstOrDefault(a =>
                SourceGenerationValidation.IsPalORMAttribute(a, "OwnedJson"));
            bool isOwnedJson = ownedJsonAttr is not null;
            string? ownedJsonContextTypeName = ownedJsonAttr?.ConstructorArguments.FirstOrDefault().Value is INamedTypeSymbol contextType
                ? contextType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null;

            AttributeData? converterAttr = SourceGenerationValidation.GetConverterAttribute(prop);
            INamedTypeSymbol? converterType = null;
            ITypeSymbol providerType =
                SourceGenerationValidation.UnwrapNullable(prop.Type);
            if (converterAttr is not null)
            {
                _ = SourceGenerationValidation.TryGetConverterTypes(
                    prop,
                    converterAttr,
                    out converterType,
                    out ITypeSymbol? mappedProviderType);
                providerType = mappedProviderType!;
            }

            var fkAttr = prop.GetAttributes().FirstOrDefault(a =>
                SourceGenerationValidation.IsPalORMAttribute(a, "ForeignKey"));  // ITM-512
            if (fkAttr is not null && fkAttr.ConstructorArguments.Length >= 2)
            {
                // ITM-602: ForeignKeyModel.OnDelete 此前恒写 0（NoAction），用户 [ForeignKey(OnDelete=...)]
                // 设置完全丢弃——PALORM004 注释承认"FK 不生成 DDL"，但字段语义应与用户声明一致，
                // 便于未来启用 FK DDL 时无需重新解析。从命名参数提取 OnDelete 整数值，默认 NoAction。
                int onDelete = 0;  // DeleteAction.NoAction
                var onDeleteArg = fkAttr.NamedArguments.FirstOrDefault(static na => na.Key == "OnDelete");
                if (onDeleteArg.Value.Value is int value)
                    onDelete = value;
                foreignKeys.Add(new ForeignKeyModel(
                    prop.Name,
                    fkAttr.ConstructorArguments[0].Value as string ?? "",
                    fkAttr.ConstructorArguments[1].Value as string ?? "",
                    onDelete));
            }

            columns.Add(new ColumnModel(
                prop.Name, columnName,
                prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                providerType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                MapToDbType(providerType), isKey, isAutoIncrement,
                prop.NullableAnnotation == NullableAnnotation.Annotated, isRequired,
                ignoreOnInsert, isConcurrencyToken, isTimestamp, computedExpression, isOwnedJson,
                ownedJsonContextTypeName,
                converterType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }

        bool isSoftDelete = typeSymbol.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "SoftDelete"));  // ITM-512
        bool isTenantAware = typeSymbol.GetAttributes().Any(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "TenantAware"));  // ITM-512

        // [Index("name", "col1", "col2", Unique = ...)] → 复合索引（ADR-B）
        // 无名/空列声明跳过生成——PALORM020 已在编译期报错，此处跳过防级联生成错误
        foreach (var indexAttr in typeSymbol.GetAttributes().Where(a =>
            SourceGenerationValidation.IsPalORMAttribute(a, "Index")))  // ITM-512
        {
            if (indexAttr.ConstructorArguments.Length < 2) continue;
            if (indexAttr.ConstructorArguments[0].Value is not string indexName) continue;
            string[] indexColumns = indexAttr.ConstructorArguments[1].Values
                .Select(static v => v.Value as string)
                .Where(static v => v is not null)
                .Cast<string>()
                .ToArray();
            if (indexColumns.Length == 0) continue;
            bool unique = indexAttr.NamedArguments
                .FirstOrDefault(static na => na.Key == "Unique").Value.Value is true;
            indexes.Add(new IndexModel(indexName, new EquatableArray<string>(indexColumns), unique));
        }

        string entityTypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new TableModel(
            typeSymbol.ContainingNamespace.ToDisplayString(),
            typeSymbol.Name,
            entityTypeName,
            PalORMGenerator.CreateGeneratedTypeSuffix(entityTypeName),
            tableName, isSoftDelete, isTenantAware,
            new EquatableArray<ColumnModel>(columns.ToArray()),
            new EquatableArray<IndexModel>(indexes.ToArray()),
            new EquatableArray<ForeignKeyModel>(foreignKeys.ToArray()));
    }

    /// <summary>收集实体自身及基类链上的可映射属性（ITM-502：GetMembers 不含继承成员，
    /// 继承 AuditBase 的实体基类列会静默丢失）。派生类同名属性覆盖基类（override/new）；
    /// 从最派生类向基类遍历，声明序保持"派生优先、同层原序"以稳定 ordinal 映射。</summary>
    private static IEnumerable<ISymbol> GetMappableProperties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ISymbol>();
        for (INamedTypeSymbol? current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            var layer = new List<ISymbol>();
            foreach (ISymbol member in current.GetMembers())
            {
                if (member is not IPropertySymbol prop) continue;
                if (prop.IsStatic || prop.IsImplicitlyDeclared || !prop.CanBeReferencedByName) continue;
                if (!seen.Add(prop.Name)) continue; // 派生类已声明同名属性 → 基类版本被隐藏
                layer.Add(member);
            }
            // 基类属性排在派生属性之后（声明序：派生类先声明的列在前，符合"子类扩展基类"直觉）
            ordered.AddRange(layer);
        }
        return ordered;
    }

    private static string MapToDbType(ITypeSymbol type)
        => type.SpecialType switch
        {
            SpecialType.System_Int64 => "BIGINT",
            SpecialType.System_Int32 => "INTEGER",
            SpecialType.System_Int16 => "SMALLINT",
            SpecialType.System_Byte => "SMALLINT",
            SpecialType.System_String => "TEXT",
            SpecialType.System_Char => "TEXT",
            SpecialType.System_Boolean => "BOOLEAN",
            SpecialType.System_Decimal => "DECIMAL",
            SpecialType.System_Double => "DOUBLE",
            SpecialType.System_Single => "FLOAT",
            SpecialType.System_DateTime => "TIMESTAMP",
            _ => type.Name switch
            {
                "DateTimeOffset" => "TIMESTAMPTZ",
                "Guid" => "UUID",
                "DateOnly" => "DATE",
                "TimeOnly" => "TIME",
                _ => "TEXT"
            }
        };
}

internal sealed record ColumnModel(
    string PropertyName, string ColumnName, string ClrTypeName, string ProviderClrTypeName,
    string DbTypeName, bool IsPrimaryKey, bool IsAutoIncrement, bool IsNullable,
    bool IsRequired,
    bool IgnoreOnInsert, bool IsConcurrencyToken, bool IsTimestamp, string? ComputedExpression,
    bool IsOwnedJson, string? OwnedJsonContextTypeName, string? ConverterTypeName)
{
    internal bool IsInsertable =>
        !IgnoreOnInsert && !IsAutoIncrement && ComputedExpression is null && !IsTimestamp;

    internal bool IsUpsertable =>
        !IgnoreOnInsert && ComputedExpression is null && !IsTimestamp;
}

internal sealed record IndexModel(string Name, EquatableArray<string> Columns, bool Unique);

internal sealed record ForeignKeyModel(
    string PropertyName, string ReferencedTable, string ReferencedColumn, int OnDelete);

/// <summary>外键删除行为（与 Core 中 DeleteAction 枚举值对齐）。
/// ITM-613: SourceGen 是 netstandard2.0 不能引用 Core，故两侧数值靠约定对齐——
/// 任一侧改顺序必须同步另一侧：<c>src/PalORM.Core/Annotations.cs</c> 的
/// <c>DeleteAction</c> 与本枚举。当前 FK 不生成 DDL（ITM-525），运行时无影响；
/// 3.0 启用 FK DDL 前必须改为强约束（如代码生成器直接消费 Core enum 类型符号）。</summary>
internal enum DeleteAction { NoAction = 0, Cascade = 1, SetNull = 2, Restrict = 3 }

