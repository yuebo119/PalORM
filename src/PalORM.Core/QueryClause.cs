using System.Data.Common;

namespace PalORM;

internal enum QueryClauseKind
{
    Comment,
    CommonTableExpression,
    Window,
    Join,
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
