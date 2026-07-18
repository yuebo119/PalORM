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
