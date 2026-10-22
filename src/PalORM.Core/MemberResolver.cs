using System.Linq.Expressions;

namespace PalORM;

/// <summary>实体成员表达式解析——从 Lambda 表达式提取属性名、列名、限定列名。
/// <para>从 QueryBuilder 提取，集中表达式解析逻辑。</para></summary>
internal static class MemberResolver
{
    /// <summary>每类型属性→列映射的单次缓存——映射表注册后单调不可变（Register 拒绝重复注册），
    /// 命中时省一次全局 FrozenDictionary 查找。未注册类型的失败不缓存。
    /// S2743/S2326/S2223 抑制理由同 EntityInfoCache：按 T 分片是缓存目的，可变静态槽是惰性缓存本体。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S2326",
        Justification = "T 不出现在字段类型中是刻意的：分片键是 T 的闭合类型本身。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S2743",
        Justification = "静态字段按 T 分片正是本缓存的目的，非跨泛型实例共享事故。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S2223",
        Justification = "可变静态字段是惰性缓存的语义本体；写入只在注册表查找成功后发生一次。")]
    private static class ColumnMapPer<TEntity>
    {
        internal static IReadOnlyDictionary<string, string>? Map;
    }

    /// <summary>从表达式提取属性名（MemberExpression 或 UnaryExpression 包装的 MemberExpression）。
    /// <para>ITM-616：仅接受根为参数表达式的直接成员（<c>x =&gt; x.Prop</c>）——嵌套成员
    /// （<c>x =&gt; x.Nav.Prop</c>）静默取叶名会生成引用不存在列的 SQL，显式拒绝。</para></summary>
    internal static string GetMemberName<TEntity, TKey>(Expression<Func<TEntity, TKey>> member)
    {
        if (member.Body is MemberExpression { Expression: ParameterExpression } memberExpression) return memberExpression.Member.Name;
        if (member.Body is UnaryExpression { Operand: MemberExpression { Expression: ParameterExpression } unaryExpression }) return unaryExpression.Member.Name;
        throw new InvalidOperationException(
            $"Cannot resolve a direct member of '{typeof(TEntity).Name}' from {member.Body}. " +
            "Nested member expressions (x => x.Nav.Prop) are not supported; reference a direct property.");
    }

    /// <summary>属性名 → 列名（查 PalORM_Runtime.PropertyToColumn 映射）。
    /// ITM-815（r23 实修）：映射缺失时响亮失败——静默回退属性名会生成引用不存在列的 SQL，
    /// 报服务端语法错而非 PalORM 诊断（对照 GridReader 的"未注册即抛"口径）；属性名恰等于
    /// 列名时该形态永不自查得出。调用面（QueryBuilder 的 GetColumnName/GetQualifiedColumnName）
    /// 恒以已注册实体 T 为约束（From&lt;T&gt; 入口已校验注册）。</summary>
    internal static string GetColumnName<TEntity, TKey>(Expression<Func<TEntity, TKey>> member)
    {
        string propertyName = GetMemberName(member);
        IReadOnlyDictionary<string, string>? mapping = ColumnMapPer<TEntity>.Map;
        if (mapping is null
            && PalORM_Runtime.PropertyToColumn.TryGetValue(typeof(TEntity), out var loaded))
        {
            mapping = loaded;
            ColumnMapPer<TEntity>.Map = loaded;
        }
        if (mapping is null || !mapping.TryGetValue(propertyName, out string? columnName))
        {
            throw new InvalidOperationException(
                $"Property '{propertyName}' on type '{typeof(TEntity).Name}' is not a mapped column " +
                "(missing [Column] mapping or excluded by [NotMapped]); the generated SQL would reference a nonexistent column.");
        }
        return columnName;
    }

    /// <summary>限定列名 = quoteIdentifier(tableOrCte) + "." + quoteIdentifier(columnName)。</summary>
    internal static string GetQualifiedColumnName<TEntity, TKey>(
        Expression<Func<TEntity, TKey>> member,
        string tableOrCteName, Func<string, string> quoteIdentifier)
        => $"{quoteIdentifier(tableOrCteName)}.{quoteIdentifier(GetColumnName(member))}";
}
