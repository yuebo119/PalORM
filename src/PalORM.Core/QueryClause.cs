using System.Data.Common;

namespace PalORM;

internal enum QueryClauseKind
{
    Comment,
    CommonTableExpression,
    Window,
    Join,
    /// <summary>软删/租户默认过滤——与用户 Where 组恒以 AND 组合，用户 OR 无法绕过（ITM-401）。</summary>
    DefaultFilter,
    Where,
    GroupBy,
    Having,
    OrderBy,
    Set,
    Raw,
    Lock
}

/// <summary>子句参数的轻量载体——标量参数只存（名, 值），执行期才物化 <see cref="DbParameter"/>；
/// 数组/租户等构建期已确定的参数保留原对象（<see cref="Eager"/>）。</summary>
internal readonly struct ParameterBinding
{
    internal readonly string Name;
    internal readonly object? Value;
    internal readonly DbParameter? Eager;

    internal ParameterBinding(string name, object? value)
    {
        Name = name;
        Value = value;
        Eager = null;
    }

    internal ParameterBinding(DbParameter eager)
    {
        Name = eager.ParameterName;
        Value = eager.Value;
        Eager = eager;
    }
}

/// <summary>2026-10-22 终审 P1-3：<paramref name="QualifyColumn"/> 标记该子句的列引用由
/// 表达式生成（OrderBy/GroupBy/WhereIn/Keyset/Include），构建时若已 With(CTE) 则把实体表
/// 前缀重限定为 CTE 名；用户手写 Where/Raw/Join 恒为 false，原样保留。</summary>
internal readonly record struct QueryClause(
    QueryClauseKind Kind,
    string Sql,
    IReadOnlyList<ParameterBinding> Parameters,
    bool QualifyColumn = false);

/// <summary>子句持久化链表节点（cons list）——AddClause 每次只分配一个节点、零复制。
/// <para>QueryBuilder 是 struct，副本共享链引用；链不可变（节点只读、追加即新建节点），
/// 因此副本隔离天然成立——这正是原 List 写时复制（COW）要保证的性质，
/// 但 COW 每个子句要复制整个列表（列表对象 + 内部数组 + 逐元素拷贝）。</para>
/// <para>遍历：链是"尾指向头"的逆序结构，<c>MaterializeClauses</c> 负责倒排成数组
/// （每次执行一次，之后由 builder 的缓存数组供所有只读遍历复用）。</para>
/// <para><b>链上哈希（2026-10-31 极致性能轮）</b>：节点构造时把子句 Sql 累积进
/// <see cref="ClausesHash"/>——链不可变使哈希随节点一次算定，<c>BuildSql</c> 不再每次
/// 遍历子句串重新哈希（O(Σ子句长度) 的字符串哈希退化为链头一次 int 合并）。哈希只用于
/// 选桶，命中核对仍是全量子句比较，分布变化只影响首轮冷缓存，不影响正确性。</para></summary>
internal sealed class ClauseNode
{
    public readonly ClauseNode? Previous;
    public readonly QueryClause Clause;
    public readonly int Count;
    /// <summary>追加序的子句文本累积哈希——<c>HashCode.Combine(previous?.ClausesHash ?? 0, clause.Sql)</c>。</summary>
    public readonly int ClausesHash;

    public ClauseNode(ClauseNode? previous, in QueryClause clause)
    {
        Previous = previous;
        Clause = clause;
        Count = previous?.Count + 1 ?? 1;
        ClausesHash = System.HashCode.Combine(previous?.ClausesHash ?? 0, clause.Sql);
    }
}
