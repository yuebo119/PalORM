using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;

namespace PalORM;

/// <summary>BuildSql 输出的形状缓存（T5c）——同一形状的查询复用同一 SQL 文本实例。
/// <para><b>形状定义</b>：子句 Sql 文本序列（值相等）+ <see cref="SqlShapeCache.ShapeFields"/>
/// （SplitQuery 改变 JOIN 拼接；Take/Skip 的值直接内联进 LIMIT/OFFSET 文本；表名/CTE 名决定 FROM）。
/// 参数<b>不</b>在键内——编译期参数化保证值只进 @pN 占位，同形状 ⇒ 同 SQL 文本，
/// 参数值由调用方逐次绑定。</para>
/// <para><b>正确性</b>：哈希只用于选桶；命中后逐条核对子句序列（值相等）与字段包
/// （记录结构体值相等），碰撞只会落到"未命中重建"，不会产出错误 SQL。容量以应用内
/// "不同查询形状数"为界（有限，通常远小于查询数），与既有静态缓存同纪律；
/// 跨会话共享（形状与具体会话无关）。</para></summary>
internal static class SqlShapeCache
{
    /// <summary>形状中"子句序列之外"的字段——全部参与键与核对。
    /// <para><b>Dialect 必须在内</b>：SELECT 列清单的引用符随方言不同（PG 双引号 / MySQL 反引号），
    /// 而用户手写的 WHERE 子句文本跨方言可能完全相同（无引用符差异）——缺方言会让
    /// PG 会话的缓存 SQL 发给 MySQL（本缺陷由 PessimisticLockTests 跨方言同形状用例实测暴露）。</para></summary>
    internal readonly record struct ShapeFields(SqlDialect Dialect, bool SplitQuery, int? Take, int? Skip, string TableName, string? CteName);

    internal sealed record SqlShapeEntry(string[] SqlSequence, ShapeFields Fields, string FullSql);

    private static readonly ConcurrentDictionary<int, ConcurrentQueue<SqlShapeEntry>> Buckets = new();

    public static IEnumerable<SqlShapeEntry> GetBucket(int shapeHash)
        => Buckets.TryGetValue(shapeHash, out ConcurrentQueue<SqlShapeEntry>? bucket)
            ? bucket
            : System.Array.Empty<SqlShapeEntry>();

    /// <summary>查找与当前形状匹配的 SQL 文本——命中返回缓存的 string 实例（调用方直接复用）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3267:LoopsShouldBeSimplifiedWithLinq",
        Justification = "查找本身在查询热路径上：LINQ 形式每次分配委托+迭代器，抵消缓存收益。手写循环零分配。")]
    public static string? FindMatch(
        int shapeHash,
        IReadOnlyList<QueryClause> clauses,
        ShapeFields fields)
    {
        if (!Buckets.TryGetValue(shapeHash, out ConcurrentQueue<SqlShapeEntry>? bucket))
            return null;
        foreach (SqlShapeEntry entry in bucket)
        {
            if (entry.Matches(clauses, fields))
                return entry.FullSql;
        }
        return null;
    }

    public static void Add(
        int shapeHash,
        IReadOnlyList<QueryClause> clauses,
        ShapeFields fields, string fullSql)
    {
        var sequence = new string[clauses.Count];
        for (int i = 0; i < clauses.Count; i++)
        {
            sequence[i] = clauses[i].Sql;
        }

        var entry = new SqlShapeEntry(sequence, fields, fullSql);
        Buckets.GetOrAdd(shapeHash, static _ => new ConcurrentQueue<SqlShapeEntry>()).Enqueue(entry);
    }

    /// <summary>全缓存条目总数——诊断与测试观测点（遍历 O(桶数)，不进查询热路径）。
    /// 审计 2026-09-19 A1 防线：进程级缓存必须有界，本计数让上界可断言。</summary>
    internal static int TotalEntryCount
    {
        get
        {
            int total = 0;
            foreach (ConcurrentQueue<SqlShapeEntry> bucket in Buckets.Values)
                total += bucket.Count;
            return total;
        }
    }

    /// <summary>清空全部条目——测试隔离专用（与 CacheStore.Clear 同纪律）。</summary>
    internal static void Clear() => Buckets.Clear();
}

internal static class SqlShapeCacheExtensions
{
    /// <summary>核对：子句数一致 + 每条子句 Sql 文本值相等 + 字段包值相等。
    /// 全走值相等（短字符串比较/记录结构体），零分配；碰撞候选最多浪费这一次核对。</summary>
    public static bool Matches(
        this SqlShapeCache.SqlShapeEntry entry,
        IReadOnlyList<QueryClause> clauses,
        SqlShapeCache.ShapeFields fields)
    {
        if (entry.Fields != fields)
            return false;
        if (entry.SqlSequence.Length != clauses.Count)
            return false;
        for (int i = 0; i < clauses.Count; i++)
        {
            if (!string.Equals(entry.SqlSequence[i], clauses[i].Sql, System.StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
