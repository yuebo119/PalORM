using System.Linq.Expressions;

namespace PalORM;

/// <summary>实体成员表达式解析——从 Lambda 表达式提取属性名、列名、限定列名。
/// <para>从 QueryBuilder 提取，集中表达式解析逻辑。</para></summary>
internal static class MemberResolver
{
    /// <summary>从表达式提取属性名（MemberExpression 或 UnaryExpression 包装的 MemberExpression）。</summary>
    internal static string GetMemberName<TEntity, TKey>(Expression<Func<TEntity, TKey>> member)
    {
        if (member.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        if (member.Body is UnaryExpression { Operand: MemberExpression unaryExpression }) return unaryExpression.Member.Name;
        throw new InvalidOperationException($"Cannot resolve member name from {member.Body}");
    }

    /// <summary>属性名 → 列名（查 PalORM_Runtime.PropertyToColumn 映射，未注册则返回属性名）。</summary>
    internal static string GetColumnName<TEntity, TKey>(Expression<Func<TEntity, TKey>> member)
    {
        string propertyName = GetMemberName(member);
        return PalORM_Runtime.PropertyToColumn.TryGetValue(typeof(TEntity), out var mapping)
            && mapping.TryGetValue(propertyName, out string? columnName)
            ? columnName
            : propertyName;
    }

    /// <summary>限定列名 = quoteIdentifier(tableOrCte) + "." + quoteIdentifier(columnName)。</summary>
    internal static string GetQualifiedColumnName<TEntity, TKey>(
        Expression<Func<TEntity, TKey>> member,
        string tableOrCteName, Func<string, string> quoteIdentifier)
        => $"{quoteIdentifier(tableOrCteName)}.{quoteIdentifier(GetColumnName(member))}";
}
