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

    /// <summary>per-(Type, Dialect, softDelete, tenant) 缓存默认过滤条件，消除每次 QuoteIdentifier + 插值。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect, bool, bool), string> FilterConditionCache = new();
    /// <summary>per-(Type, Dialect, hasTenant, !ignoreFilters) 缓存 GetAsync 完整 SQL，消除每次插值 + QuoteIdentifier。
    /// （ITM-640：第四元实义为 !ignoreFilters——key 由 DataSession.Crud 传入，注释修正）。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect, bool, bool), string> GetByKeySqlCache = new();
}
