using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;

namespace PalORM;

/// <summary>BuildSql 输出的形状缓存（T5c）——同一形状的查询复用同一 SQL 文本实例。
/// <para><b>形状定义</b>：子句 Sql 文本序列（值相等）+ <see cref="SqlShapeCache.ShapeFields"/>
/// （SplitQuery 改变 JOIN 拼接；LIMIT/OFFSET 的值经参数化绑定 @pN 占位——
/// <b>值不进形状键</b>，仅"有无 Take/Skip"的形态进键；表名/CTE 名决定 FROM）。
/// 参数不在键内——同形状 ⇒ 同 SQL 文本，参数值由调用方逐次绑定。</para>
/// <para><b>正确性</b>：哈希只用于选桶；命中后逐条核对子句序列（值相等）与字段包
/// （记录结构体值相等），碰撞只会落到"未命中重建"，不会产出错误 SQL。
/// 跨会话共享（形状与具体会话无关）。</para>
/// <para><b>容量纪律（审计 2026-09-19 A1 整改）</b>：总量上限 <see cref="MaxEntries"/>
/// 条（对齐 BoundedQueryCache 的 1024 纪律），满则拒写——新形状逐次重建（回到缓存前
/// 的行为），既有条目继续命中。LIMIT/OFFSET 参数化（SHAPE-010 根解）落地后，
/// 动态分页的 OFFSET 值回归有限形态集（HasTake/HasSkip 布尔），残余无界源
/// （动态 Tag/Raw 值）由上限兜底。</para></summary>
internal static class SqlShapeCache
{
    /// <summary>总量上限——满则拒写（与 BoundedQueryCache 同纪律）。竞态窗口下可略超（并发
    /// 同时通过上限检查），偏差量为并发 Add 数，无害。</summary>
    internal const int MaxEntries = 1024;

    private static readonly ConcurrentDictionary<int, ConcurrentQueue<SqlShapeEntry>> Buckets = new();

    private static int _totalEntries;

    /// <summary>形状中"子句序列之外"的字段——全部参与键与核对。
    /// <para><b>Dialect 必须在内</b>：SELECT 列清单的引用符随方言不同（PG 双引号 / MySQL 反引号），
    /// 而用户手写的 WHERE 子句文本跨方言可能完全相同（无引用符差异）——缺方言会让
    /// PG 会话的缓存 SQL 发给 MySQL（本缺陷由 PessimisticLockTests 跨方言同形状用例实测暴露）。</para>
    /// <para><b>HasTake/HasSkip 是形态而非值</b>（SHAPE-010 参数化根解）：LIMIT/OFFSET 值经
    /// @pN 占位绑定，值不影响 SQL 文本；但 take-only / skip-only / take+skip 产出**不同文本形态**
    /// （如 SQLite skip-only 是 <c>LIMIT -1 OFFSET @pN</c>），缺形态标记会让不同形态互相复用条目。</para></summary>
    internal readonly record struct ShapeFields(
        SqlDialect Dialect,
        bool SplitQuery,
        bool HasTake,
        bool HasSkip,
        string TableName,
        string? CteName);

    internal sealed record SqlShapeEntry(string[] SqlSequence, ShapeFields Fields, string FullSql);

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
        // 容量纪律（审计 A1）：满则拒写。被拒的形状此后逐次重建——行为等同于缓存不存在，
        // 正确性不受影响；动态 Tag/Raw 值等残余无界源由此兜底。
        if (Volatile.Read(ref _totalEntries) >= MaxEntries) return;
        Interlocked.Increment(ref _totalEntries);

        var sequence = new string[clauses.Count];
        for (int i = 0; i < clauses.Count; i++)
        {
            sequence[i] = clauses[i].Sql;
        }

        var entry = new SqlShapeEntry(sequence, fields, fullSql);
        Buckets.GetOrAdd(shapeHash, static _ => new ConcurrentQueue<SqlShapeEntry>()).Enqueue(entry);
    }

    /// <summary>全缓存条目总数——诊断与测试观测点（O(1) 读计数器）。
    /// 审计 2026-09-19 A1 防线：进程级缓存必须有界，本计数让上界可断言。</summary>
    internal static int TotalEntryCount => Volatile.Read(ref _totalEntries);

    /// <summary>枚举全部条目——测试/诊断观测（分配枚举，不进热路径）。
    /// 行为断言用（如"同 SQL 文本仅一条目""无 OFFSET 形状入缓存"），对并行测试噪声免疫。</summary>
    internal static IEnumerable<SqlShapeEntry> Entries
    {
        get
        {
            foreach (ConcurrentQueue<SqlShapeEntry> bucket in Buckets.Values)
                foreach (SqlShapeEntry entry in bucket)
                    yield return entry;
        }
    }

    /// <summary>清空全部条目——测试隔离专用（非线程安全，仅测试单线程调用；与 CacheStore.Clear 同纪律）。</summary>
    internal static void Clear()
    {
        Buckets.Clear();
        Volatile.Write(ref _totalEntries, 0);
    }
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
