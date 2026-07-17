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

        return type.GetMembers().OfType<IPropertySymbol>()
            .Where(static property => !IsNotMapped(property))
            .All(static property => !property.IsStatic
                && !property.IsIndexer
                && property.SetMethod is
                {
                    DeclaredAccessibility: Accessibility.Public
                        or Accessibility.Internal
                });
    }

    internal static bool CanGenerateEntity(INamedTypeSymbol type)
    {
        if (!IsSupportedEntity(type))
            return false;

        foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (IsNotMapped(property))
                continue;

            AttributeData? ownedJson = property.GetAttributes().FirstOrDefault(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "PalORM.OwnedJsonAttribute");
            if (ownedJson is not null && property.Type.SpecialType != SpecialType.System_String)
            {
                if (ownedJson.ConstructorArguments.FirstOrDefault().Value is not INamedTypeSymbol contextType
                    || contextType.IsGenericType
                    || contextType.ContainingType is not null
                    || !IsValidOwnedJsonContext(contextType, property.Type))
                {
                    return false;
                }
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

    internal static bool IsNotMapped(IPropertySymbol property)
        => property.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.Name
                is "NotMappedAttribute" or "NotMapped");

    private static bool IsSupportedProviderType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol)
            return false;

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
