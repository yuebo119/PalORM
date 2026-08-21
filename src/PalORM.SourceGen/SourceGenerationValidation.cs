using Microsoft.CodeAnalysis;

namespace PalORM.SourceGen;

internal static class SourceGenerationValidation
{
    internal static bool IsSupportedEntity(INamedTypeSymbol type)
    {
        if (type.IsGenericType
            || type.ContainingType is not null
            || type.IsAbstract
            || type.IsStatic
            || !HasPublicParameterlessConstructor(type))
        {
            return false;
        }

        // ITM-617：setter 校验走基类链（与列收集同源）——声明成员判定会让基类 get-only/
        // protected-set 属性流入 ColumnModel，生成 `entity.Prop = ...` 的 RowFactory 不可编译。
        return EnumerateMappedProperties(type)
            .All(static property => property.SetMethod is
            {
                DeclaredAccessibility: Accessibility.Public
                    or Accessibility.Internal
            });
    }

    internal static bool CanGenerateEntity(INamedTypeSymbol type)
    {
        if (!IsSupportedEntity(type))
            return false;

        // 恰好一个 [Key]（ITM-311）：无主键会生成 "DELETE FROM t WHERE " 畸形 SQL；
        // 复合主键的 BindDelete 会把同一 key 绑到所有参数——两者都不能只依赖可被
        // .editorconfig 降级的 PALORM001/019，生成器必须自守卫。
        // ITM-559：计数走基类链（与 TableModel.GetMappableProperties 的列收集一致）——
        // 只查声明成员会让基类 [Key] 实体被跳过且 PALORM001 误报"无 [Key]"。
        List<IPropertySymbol> keyProperties = EnumerateMappedProperties(type)
            .Where(static property => property.GetAttributes().Any(static attribute =>
                IsPalORMAttribute(attribute, "Key")))  // ITM-512
            .ToList();
        if (keyProperties.Count != 1)
            return false;

        // 主键属性必须可被生成代码正常赋值（ITM-504）：自增主键 SetId 生成 `entity.Id = id`，
        // init-only setter 会产生 CS8852（生成物编译失败，用户难定位）。要求 PK setter 非 init。
        // ITM-560：可空值类型主键（long?/Guid?）会让 BindDelete 的精确拆箱分支生成
        // `((long)key) is null` 等不编译代码（CS0037/CS8117）——一并拒绝。
        // 两种形态均由 PALORM022 在编译期给出定位诊断（此前静默跳过，运行期才报 not registered）。
        IPropertySymbol keyProperty = keyProperties[0];
        if (keyProperty.SetMethod is null || keyProperty.SetMethod.IsInitOnly)
            return false;
        if (keyProperty.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            return false;

        // ITM-617：OwnedJson/值映射校验走基类链（同上）——声明成员判定会漏检基类的
        // int[]/TimeSpan/非法 OwnedJson 属性，使其未经校验流入列收集。
        foreach (IPropertySymbol property in EnumerateMappedProperties(type))
        {
            if (IsNotMapped(property))
                continue;

            AttributeData? ownedJson = property.GetAttributes().FirstOrDefault(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "PalORM.OwnedJsonAttribute");
            if (ownedJson is not null
                && property.Type.SpecialType != SpecialType.System_String
                && (ownedJson.ConstructorArguments.FirstOrDefault().Value is not INamedTypeSymbol contextType
                    || contextType.IsGenericType
                    || contextType.ContainingType is not null
                    || !IsValidOwnedJsonContext(contextType, property.Type)))
            {
                return false;
            }

            if (!HasValidValueMapping(property))
                return false;
        }

        return true;
    }

    internal static bool HasValidValueMapping(IPropertySymbol property)
    {
        bool isOwnedJson = property.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString()
                == "PalORM.OwnedJsonAttribute");
        AttributeData? converter = GetConverterAttribute(property);
        if (isOwnedJson)
            return converter is null;

        if (converter is null)
            return IsSupportedProviderType(UnwrapNullable(property.Type));

        return TryGetConverterTypes(
                property,
                converter,
                out _,
                out ITypeSymbol? providerType)
            && providerType is not null
            && providerType.NullableAnnotation != NullableAnnotation.Annotated
            && SymbolEqualityComparer.Default.Equals(
                providerType,
                UnwrapNullable(providerType))
            && IsSupportedProviderType(providerType);
    }

    internal static bool TryGetConverterTypes(
        IPropertySymbol property,
        AttributeData converter,
        out INamedTypeSymbol? converterType,
        out ITypeSymbol? providerType)
    {
        converterType = converter.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;
        providerType = null;
        bool isSameAssembly = converterType is not null
            && SymbolEqualityComparer.Default.Equals(
                converterType.ContainingAssembly,
                property.ContainingAssembly);
        if (converterType is null
            || converterType.IsAbstract
            || converterType.IsGenericType
            || converterType.ContainingType is not null
            || converterType.DeclaredAccessibility != Accessibility.Public
                && !(isSameAssembly
                    && converterType.DeclaredAccessibility
                        == Accessibility.Internal))
        {
            return false;
        }

        bool hasAccessibleConstructor = converterType.IsValueType
            || converterType.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == 0
                && (constructor.DeclaredAccessibility
                        == Accessibility.Public
                    || isSameAssembly
                        && constructor.DeclaredAccessibility
                            == Accessibility.Internal));
        if (!hasAccessibleConstructor)
            return false;

        INamedTypeSymbol? contract = converterType.AllInterfaces.FirstOrDefault(interfaceType =>
            interfaceType.OriginalDefinition.MetadataName == "IValueConverter`2"
            && interfaceType.OriginalDefinition.ContainingNamespace.ToDisplayString() == "PalORM"
            && SymbolEqualityComparer.Default.Equals(interfaceType.TypeArguments[0], property.Type));
        if (contract is null)
            return false;

        providerType = contract.TypeArguments[1];
        return true;
    }

    internal static AttributeData? GetConverterAttribute(IPropertySymbol property)
        => property.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "PalORM.ConverterAttribute");

    internal static ITypeSymbol UnwrapNullable(ITypeSymbol type)
        => type is INamedTypeSymbol namedType
            && namedType.OriginalDefinition.SpecialType
                == SpecialType.System_Nullable_T
            && namedType.TypeArguments.Length == 1
                ? namedType.TypeArguments[0]
                : type;

    // ITM-512：注解匹配必须校验命名空间为 PalORM，否则混挂 EF Core/DataAnnotations 的
    // 同名 [Table]/[Column]/[Key]/[ForeignKey]/[Required] 等会被误判为 PalORM 注解。
    // shortName 传短名（如 "Table"）——同时接受 "Table" 与 "TableAttribute" 两种写法。
    internal static bool IsPalORMAttribute(AttributeData? attribute, string shortName)
        => attribute?.AttributeClass is { } cls
            && (cls.Name == shortName || cls.Name == shortName + "Attribute")
            && cls.ContainingNamespace?.ToDisplayString() == "PalORM";

    internal static bool IsNotMapped(IPropertySymbol property)
        => property.GetAttributes().Any(static attribute =>
            IsPalORMAttribute(attribute, "NotMapped"));

    /// <summary>沿基类链枚举可映射属性（ITM-559，与 TableModel.GetMappableProperties 同一
    /// 隐藏语义：派生类同名属性覆盖基类）。排除 static/索引器/[NotMapped]。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "EnumerateMappedProperties 走基类链 + 派生类覆盖语义，循环内多分支是必要的"
            + "（跳过 static/索引器/[NotMapping]/覆盖同名属性）。复杂度源于 Roslyn 符号模型遍历本身。")]
    internal static IEnumerable<IPropertySymbol> EnumerateMappedProperties(INamedTypeSymbol type)
    {
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
                if (IsNotMapped(property)) continue;
                yield return property;
            }
        }
    }

    private static bool IsSupportedProviderType(ITypeSymbol type)
    {
        // 仅放行元素为 System.Byte 的一维数组——四环契约（读取 GetFieldValue/参数绑定/
        // 三方言 BLOB DDL/BulkInsert）已全部可证明；其余数组（int[]/string[]/多维数组）
        // 无 BLOB 语义与 DDL 契约，仍拒绝。
        if (type is IArrayTypeSymbol arrayType)
            return arrayType.ElementType.SpecialType == SpecialType.System_Byte
                && arrayType.Rank == 1;

        if (type is INamedTypeSymbol namedType
            && namedType.OriginalDefinition.SpecialType
                == SpecialType.System_Nullable_T)
        {
            return false;
        }

        if (type.SpecialType is
            SpecialType.System_Int64
            or SpecialType.System_Int32
            or SpecialType.System_Int16
            or SpecialType.System_Byte
            or SpecialType.System_String
            or SpecialType.System_Char
            or SpecialType.System_Boolean
            or SpecialType.System_Decimal
            or SpecialType.System_Double
            or SpecialType.System_Single
            or SpecialType.System_DateTime)
        {
            return true;
        }

        return type.ToDisplayString() is
            "System.Guid"
            or "System.DateTimeOffset"
            or "System.DateOnly"
            or "System.TimeOnly";
    }

    private static bool HasPublicParameterlessConstructor(
        INamedTypeSymbol type)
        => type.InstanceConstructors.Any(static constructor =>
            constructor.Parameters.Length == 0
            && constructor.DeclaredAccessibility == Accessibility.Public);

    internal static bool IsValidOwnedJsonContext(
        INamedTypeSymbol contextType,
        ITypeSymbol propertyType)
    {
        bool derivesFromJsonContext = false;
        for (INamedTypeSymbol? current = contextType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext")
            {
                derivesFromJsonContext = true;
                break;
            }
        }

        return derivesFromJsonContext && contextType.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString()
                == "System.Text.Json.Serialization.JsonSerializableAttribute"
            && attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol registeredType
            && SymbolEqualityComparer.Default.Equals(registeredType, propertyType));
    }
}
