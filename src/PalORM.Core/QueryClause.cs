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

internal readonly record struct QueryClause(
    QueryClauseKind Kind,
    string Sql,
    IReadOnlyList<DbParameter> Parameters);

/// <summary>子句持久化链表节点（cons list）——AddClause 每次只分配一个节点、零复制。
/// <para>QueryBuilder 是 struct，副本共享链引用；链不可变（节点只读、追加即新建节点），
/// 因此副本隔离天然成立——这正是原 List 写时复制（COW）要保证的性质，
/// 但 COW 每个子句要复制整个列表（列表对象 + 内部数组 + 逐元素拷贝）。</para>
/// <para>遍历：链是"尾指向头"的逆序结构，<c>MaterializeClauses</c> 负责倒排成数组
/// （每次执行一次，之后由 builder 的缓存数组供所有只读遍历复用）。</para></summary>
internal sealed class ClauseNode
{
    public readonly ClauseNode? Previous;
    public readonly QueryClause Clause;
    public readonly int Count;

    public ClauseNode(ClauseNode? previous, in QueryClause clause)
    {
        Previous = previous;
        Clause = clause;
        Count = previous?.Count + 1 ?? 1;
    }
}
