using System.Collections.Concurrent;

namespace PalORM;

/// <summary>非泛型共享缓存--避免泛型 DataSession&lt;TProvider&gt; 中的 static 字段
/// 不跨 TProvider 共享（Sonar S2743）。v4.1 极致降内存优化。</summary>
internal static class DataSessionCache
{
    /// <summary>per-(Type, Dialect) 缓存 selectColumns 字符串，消除每次 QuoteIdentifier + string.Join。
    /// <para><b>注意形态</b>：本缓存存的是<b>不带表名限定</b>的裸列清单（<c>"id", "status"</c>），
    /// 供 GetAllAsync/GetAsync 的 FROM 单表查询使用。需要表名限定的调用点（QueryBuilder 的
    /// SELECT 列表，JOIN 下防 ambiguous column）必须用 <see cref="QualifiedSelectColumnsCache"/>，
    /// 两者键相同但值不同，混用会静默丢掉限定名。</para></summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect), string> SelectColumnsCache = new();

    /// <summary>per-(Type, Dialect) 缓存<b>带表名限定</b>的列清单（<c>"t"."id", "t"."status"</c>），
    /// 供 QueryBuilder.BuildSql 的 SELECT 列表使用（v5.6）。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect), string> QualifiedSelectColumnsCache = new();

    /// <summary>per-(Type, Dialect, softDelete, tenant) 缓存默认过滤的三种拼接形态，消除每次
    /// QuoteIdentifier + 插值。<b>v5.6</b>：值由「条件文本」升级为
    /// <see cref="DefaultFilterForms"/>——三个调用点（COUNT 的裸条件、拼在既有 WHERE 之后的
    /// 追加片段、独立 WHERE 子句）原本各自再做一次插值，现在一次查找同时拿到三种形态。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect, bool, bool), DefaultFilterForms> FilterFormsCache = new();

    /// <summary>per-(Type, Dialect, hasTenant, !ignoreFilters) 缓存 GetAsync 完整 SQL，消除每次插值 + QuoteIdentifier。
    /// （ITM-640：第四元实义为 !ignoreFilters——key 由 DataSession.Crud 传入，注释修正）。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect, bool, bool), string> GetByKeySqlCache = new();

    /// <summary>per-Dialect 缓存租户过滤追加片段（" AND {quote(tenant_id)} = @__tenant0"，
    /// M1，v5.7）——片段只含方言标识符与 const 参数名，方言内恒定；原先四处调用点每次
    /// 2 次 QuoteIdentifier + 插值。供 BulkDeleteAsync（语句随批次占位符变化，仅后缀可缓存）
    /// 及下方三个语句级缓存的构建期复用。</summary>
    internal static readonly ConcurrentDictionary<SqlDialect, string> TenantAppendFragmentCache = new();

    /// <summary>per-(Type, Dialect) 缓存 UpdateCoreAsync 的 sqls.Update + 租户后缀（M1，v5.7）——
    /// 原先租户会话每次更新都重新拼接。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect), string> UpdateWithTenantSqlCache = new();

    /// <summary>per-(Type, Dialect) 缓存 DeleteAsync 物理路径的 sqls.Delete + 租户后缀（M1，v5.7）。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect), string> DeleteWithTenantSqlCache = new();

    /// <summary>per-(Type, Dialect, hasTenant) 缓存软删 UPDATE 全句（M1，v5.7）——原先
    /// DeleteAsync 软删路径每次调用 5 次 QuoteIdentifier + 全句插值重建。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect, bool), string> SoftDeleteUpdateSqlCache = new();
}

/// <summary>默认过滤（软删/租户）的三种拼接形态。空过滤时为 <see cref="Empty"/>（三项全空串），
/// 而非 default——后者三项为 null，调用点拼接前取 Length 会 NRE。</summary>
internal readonly record struct DefaultFilterForms(string Condition, string AndFragment, string WhereClause)
{
    /// <summary>无默认过滤（实体既非 [SoftDelete] 也无租户会话）。</summary>
    internal static readonly DefaultFilterForms Empty = new("", "", "");

    /// <summary>由条件文本派生三种形态；空条件映射为 <see cref="Empty"/>。</summary>
    internal static DefaultFilterForms FromCondition(string condition)
        => condition.Length == 0
            ? Empty
            : new DefaultFilterForms(condition, " AND " + condition, " WHERE " + condition);
}
