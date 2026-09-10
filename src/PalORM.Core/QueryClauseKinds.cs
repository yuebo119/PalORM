namespace PalORM;

/// <summary>v4.4：QueryClauseKind 数组缓存，避免每次查询分配临时数组。
/// 放在非泛型类中，避免 S2743（泛型类型的 static 字段不跨 T 共享）。</summary>
internal static class QueryClauseKinds
{
    // ITM-788(r21)：Query/QuerySplit 补 Window——当前 UnsafeWindowOver 不携带参数（枚举
    // 不到参数，行为不变），但路由数组与 BuildSql 追加的品类保持同构后，未来 Window 支持
    // 带参时参数不会静默丢弃（结构性对齐防患）。Count 族不含 Window（COUNT 无窗口语义）。
    internal static readonly QueryClauseKind[] Query =
    [
        QueryClauseKind.CommonTableExpression, QueryClauseKind.Join, QueryClauseKind.DefaultFilter,
        QueryClauseKind.Where, QueryClauseKind.GroupBy, QueryClauseKind.Having,
        QueryClauseKind.OrderBy, QueryClauseKind.Raw, QueryClauseKind.Lock, QueryClauseKind.Window
    ];
    internal static readonly QueryClauseKind[] QuerySplit =
    [
        QueryClauseKind.CommonTableExpression, QueryClauseKind.DefaultFilter, QueryClauseKind.Where,
        QueryClauseKind.GroupBy, QueryClauseKind.Having, QueryClauseKind.OrderBy,
        QueryClauseKind.Raw, QueryClauseKind.Lock, QueryClauseKind.Window
    ];
    internal static readonly QueryClauseKind[] Count =
    [
        QueryClauseKind.CommonTableExpression, QueryClauseKind.Join, QueryClauseKind.DefaultFilter,
        QueryClauseKind.Where, QueryClauseKind.GroupBy, QueryClauseKind.Having
    ];
    internal static readonly QueryClauseKind[] CountSplit =
    [
        QueryClauseKind.CommonTableExpression, QueryClauseKind.DefaultFilter, QueryClauseKind.Where,
        QueryClauseKind.GroupBy, QueryClauseKind.Having
    ];
    // ITM-640：Join/CTE 由 BuildUpdateSql 先抛 NotSupportedException——数组中的两位是
    // 不可达死防御位（GetUpdateParameters 走此数组过滤参数），移除防三处维护漂移。
    internal static readonly QueryClauseKind[] Update =
    [
        QueryClauseKind.Set, QueryClauseKind.DefaultFilter, QueryClauseKind.Where
    ];
}
