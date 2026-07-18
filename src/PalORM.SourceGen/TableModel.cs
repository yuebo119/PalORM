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

        var tableAttr = typeSymbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name is "TableAttribute" or "Table");
        if (tableAttr is null) return null;

        string tableName = tableAttr.ConstructorArguments.FirstOrDefault().Value as string
            ?? typeSymbol.Name;

        var columns = new List<ColumnModel>();
        var indexes = new List<IndexModel>();
        var foreignKeys = new List<ForeignKeyModel>();

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.GetAttributes().Any(a => a.AttributeClass?.Name is "NotMappedAttribute" or "NotMapped"))
                continue;

            var columnAttr = prop.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name is "ColumnAttribute" or "Column");
            string columnName = columnAttr?.ConstructorArguments.FirstOrDefault().Value as string
                ?? prop.Name;

            // [Unique] → 单列唯一索引（ADR-B：属性级 Unique 升为唯一索引）
            if (prop.GetAttributes().Any(a => a.AttributeClass?.Name is "UniqueAttribute" or "Unique"))
            {
                indexes.Add(new IndexModel(
                    $"ux_{tableName}_{columnName}",
                    new EquatableArray<string>(new[] { columnName }),
                    Unique: true));
            }

            bool isKey = prop.GetAttributes().Any(a => a.AttributeClass?.Name is "KeyAttribute" or "Key");
            // [Key(AutoIncrement = false)] 关闭数值主键的自增推断（雪花 ID 等应用侧赋值主键）
            bool autoIncrementEnabled = prop.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name is "KeyAttribute" or "Key")?
                .NamedArguments.FirstOrDefault(na => na.Key == "AutoIncrement").Value.Value is not false;
            bool isAutoIncrement = isKey && autoIncrementEnabled
                && prop.Type.SpecialType is SpecialType.System_Int64 or SpecialType.System_Int32;
            bool ignoreOnInsert = prop.GetAttributes().Any(a => a.AttributeClass?.Name is "IgnoreOnInsertAttribute" or "IgnoreOnInsert");
            bool isConcurrencyToken = prop.GetAttributes().Any(a => a.AttributeClass?.Name is "ConcurrencyCheckAttribute" or "ConcurrencyCheck");
            bool isTimestamp = prop.GetAttributes().Any(a => a.AttributeClass?.Name is "TimestampAttribute" or "Timestamp");
            bool isRequired = prop.GetAttributes().Any(a => a.AttributeClass?.Name is "RequiredAttribute" or "Required");
            string? computedExpression = prop.GetAttributes()
                .FirstOrDefault(static attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "PalORM.ComputedAttribute")?
                .ConstructorArguments.FirstOrDefault().Value as string;
            var ownedJsonAttr = prop.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name is "OwnedJsonAttribute" or "OwnedJson");
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
                a.AttributeClass?.Name is "ForeignKeyAttribute" or "ForeignKey");
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
            a.AttributeClass?.Name is "SoftDeleteAttribute" or "SoftDelete");
        bool isTenantAware = typeSymbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name is "TenantAwareAttribute" or "TenantAware");

        // [Index("name", "col1", "col2", Unique = ...)] → 复合索引（ADR-B）
        // 无名/空列声明跳过生成——PALORM020 已在编译期报错，此处跳过防级联生成错误
        foreach (var indexAttr in typeSymbol.GetAttributes().Where(a =>
            a.AttributeClass?.Name is "IndexAttribute" or "Index"))
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
        int hash = 17;
        foreach (var item in _items) hash = hash * 31 + (item?.GetHashCode() ?? 0);
        return hash;
    }
    public Enumerator GetEnumerator() => new(_items);
    public ref struct Enumerator
    {
        private readonly T[] _items;
        private int _index;
        internal Enumerator(T[] items) { _items = items; _index = -1; }
        public T Current => _items[_index];
        public bool MoveNext() => ++_index < _items.Length;
    }
}
