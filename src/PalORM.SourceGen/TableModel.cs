using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalORM.SourceGen;

/// <summary>源生成器数据模型——从 [Table] 注解提取的编译时元数据。</summary>
// ITM-539：IsView / Schema / Database 目前恒为 false/null（FromContext 未填充，无 DDL 消费），
// 属预留字段。保留而非删除——它们是 record 位置参数，测试（DialectSymmetryTests）显式构造引用，
// 删除会破坏构造签名与 EquatableArray 增量缓存键；待 Schema 限定表功能落地后再填充。
internal sealed record TableModel(
    string Namespace,
    string ClassName,
    string EntityTypeName,
    string GeneratedTypeSuffix,
    string TableName,
    bool IsView,
    bool IsSoftDelete,
    bool IsTenantAware,
    EquatableArray<ColumnModel> Columns,
    EquatableArray<IndexModel> Indexes,
    EquatableArray<ForeignKeyModel> ForeignKeys,
    string? Schema,
    string? Database)
{
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
            string? computedExpression = prop.GetAttributes()
                .FirstOrDefault(static attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "PalORM.ComputedAttribute")?
                .ConstructorArguments.FirstOrDefault().Value as string;
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
                foreignKeys.Add(new ForeignKeyModel(
                    prop.Name,
                    fkAttr.ConstructorArguments[0].Value as string ?? "",
                    fkAttr.ConstructorArguments[1].Value as string ?? "",
                    0));
            }

            columns.Add(new ColumnModel(
                prop.Name, columnName,
                prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                providerType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                MapToDbType(providerType), isKey, isAutoIncrement,
                prop.NullableAnnotation == NullableAnnotation.Annotated, isRequired,
                null, null, null, null,
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
            tableName, false, isSoftDelete, isTenantAware,
            new EquatableArray<ColumnModel>(columns.ToArray()),
            new EquatableArray<IndexModel>(indexes.ToArray()),
            new EquatableArray<ForeignKeyModel>(foreignKeys.ToArray()),
            null, null);
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
    bool IsRequired, int? Length, int? Precision, int? Scale, string? DefaultExpression,
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

/// <summary>外键删除行为（与 Core 中 DeleteAction 枚举值对齐）。</summary>
internal enum DeleteAction { NoAction = 0, Cascade = 1, SetNull = 2, Restrict = 3 }

/// <summary>值相等数组——支持 foreach 和 record 的 Equals/GetHashCode。</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>> where T : IEquatable<T>
{
    private readonly T[] _items;
    public EquatableArray(T[] items) => _items = items;
    public EquatableArray(System.Collections.Immutable.ImmutableArray<T> items) : this(items.AsSpan().ToArray()) { }
    public ReadOnlySpan<T> AsSpan() => _items;
    public T[] ToArray() => _items;
    public bool Equals(EquatableArray<T> other) => AsSpan().SequenceEqual(other.AsSpan());
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);
    public override int GetHashCode()
    {
        // ITM-544：default(EquatableArray<T>) 的 _items 为 null，直接 foreach 会 NRE——归一化空数组
        int hash = 17;
        foreach (var item in _items ?? Array.Empty<T>()) hash = hash * 31 + (item?.GetHashCode() ?? 0);
        return hash;
    }
    // ITM-544：default 实例枚举同样归一化，避免 _items 为 null 时 MoveNext/Current 抛 NRE
    public Enumerator GetEnumerator() => new(_items ?? Array.Empty<T>());
    public ref struct Enumerator
    {
        private readonly T[] _items;
        private int _index;
        internal Enumerator(T[] items) { _items = items; _index = -1; }
        public T Current => _items[_index];
        public bool MoveNext() => ++_index < _items.Length;
    }
}
