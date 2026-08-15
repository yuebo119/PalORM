using System.Collections.Concurrent;

namespace PalORM;

/// <summary>非泛型共享缓存--避免泛型 DataSession&lt;TProvider&gt; 中的 static 字段
/// 不跨 TProvider 共享（Sonar S2743）。v4.1 极致降内存优化。</summary>
internal static class DataSessionCache
{
    /// <summary>per-(Type, Dialect) 缓存 selectColumns 字符串，消除每次 QuoteIdentifier + string.Join。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect), string> SelectColumnsCache = new();

    /// <summary>per-(Type, Dialect, softDelete, tenant) 缓存默认过滤条件，消除每次 QuoteIdentifier + 插值。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect, bool, bool), string> FilterConditionCache = new();
    /// <summary>per-(Type, Dialect, hasTenant, !ignoreFilters) 缓存 GetAsync 完整 SQL，消除每次插值 + QuoteIdentifier。
    /// （ITM-640：第四元实义为 !ignoreFilters——key 由 DataSession.Crud 传入，注释修正）。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect, bool, bool), string> GetByKeySqlCache = new();
}
