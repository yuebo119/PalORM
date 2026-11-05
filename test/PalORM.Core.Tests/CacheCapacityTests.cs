namespace PalORM.Core.Tests;

/// <summary>两处无界缓存的容量纪律测试（PERF-3.1/3.2，2026-11-05）。
/// <para><b>为什么需要</b>：两个键空间原本被注释论证为"有限、无界放大源不存在"，但
/// <c>OrderByClauseKey.SourceName</c> 取 <c>_cteName ?? _tableName</c>（CTE 名由调用方提供），
/// <c>PartialUpdateSqlCache</c> 的列子集位是 2^N——两者都随调用方可变。上限本身是防线，
/// 没有用例时"上限被删掉"不会有任何红（B95 家族：数字对但没测到目标）。</para>
/// <para>满员策略 = 拒写（照抄 <c>SqlShapeCache</c>）：既有条目继续命中，新键逐次重建，
/// 行为等同缓存不存在，正确性不受影响。</para>
/// <para><b>为什么整类串行</b>：三个用例都靠「先 Clear 再精确填满」构造满员态，而
/// <c>QueryBuilderShared.OrderByClauseText</c> 与 <c>DataSessionCache.PartialUpdateSqlCache</c>
/// 是进程级静态状态。并行执行时一个用例的 <c>Clear</c> 会插进另一个用例的填满循环，
/// 满员态不成立即断言失败（CI run 42029682678 实测三个用例同时红）。</para></summary>
[NotInParallel]
internal sealed class CacheCapacityTests
{
    [Test]
    public async Task OrderByClauseText_RejectsWritesWhenFull_AndKeepsExistingEntries()
    {
        try
        {
            QueryBuilderShared.ClearOrderByClauseText();
            var key = new QueryBuilderShared.OrderByClauseKey(
                typeof(CacheProbeEntity), SqlDialect.Sqlite, "cache_probe", "Id", Descending: false, Continue: false);
            QueryBuilderShared.TryAddOrderByClauseText(key, "ORDER BY first");

            // 填满到上限
            for (int i = 0; i < QueryBuilderShared.OrderByClauseTextMaxEntries; i++)
            {
                var fill = key with { SourceName = "cte_" + i };
                QueryBuilderShared.TryAddOrderByClauseText(fill, "ORDER BY fill-" + i);
            }

            // 被拒：新键写不进
            var rejected = key with { SourceName = "cte_rejected" };
            QueryBuilderShared.TryAddOrderByClauseText(rejected, "ORDER BY rejected");

            await Assert.That(QueryBuilderShared.OrderByClauseText.TryGetValue(rejected, out _)).IsFalse();
            // 既有条目继续命中（拒写不驱逐）
            await Assert.That(QueryBuilderShared.OrderByClauseText.TryGetValue(key, out string? first)).IsTrue();
            await Assert.That(first).IsEqualTo("ORDER BY first");
            await Assert.That(QueryBuilderShared.OrderByClauseText.Count)
                .IsLessThanOrEqualTo(QueryBuilderShared.OrderByClauseTextMaxEntries);
        }
        finally
        {
            QueryBuilderShared.ClearOrderByClauseText();
        }
    }

    [Test]
    public async Task OrderByClauseText_ClearResetsCounter_SoWritesResume()
    {
        try
        {
            QueryBuilderShared.ClearOrderByClauseText();
            var key = new QueryBuilderShared.OrderByClauseKey(
                typeof(CacheProbeEntity), SqlDialect.Sqlite, "cache_probe", "Id", Descending: false, Continue: false);
            for (int i = 0; i < QueryBuilderShared.OrderByClauseTextMaxEntries; i++)
            {
                QueryBuilderShared.TryAddOrderByClauseText(key with { SourceName = "s" + i }, "ORDER BY " + i);
            }
            QueryBuilderShared.TryAddOrderByClauseText(key with { SourceName = "full" }, "ORDER BY full");
            await Assert.That(QueryBuilderShared.OrderByClauseText.ContainsKey(key with { SourceName = "full" })).IsFalse();

            QueryBuilderShared.ClearOrderByClauseText();
            QueryBuilderShared.TryAddOrderByClauseText(key, "ORDER BY after-clear");
            await Assert.That(QueryBuilderShared.OrderByClauseText.TryGetValue(key, out string? text)).IsTrue();
            await Assert.That(text).IsEqualTo("ORDER BY after-clear");
        }
        finally
        {
            QueryBuilderShared.ClearOrderByClauseText();
        }
    }

    [Test]
    public async Task PartialUpdateSqlCache_RejectsWritesWhenFull_AndKeepsExistingEntries()
    {
        try
        {
            DataSessionCache.PartialUpdateSqlCache.Clear();
            var key = (typeof(CacheProbeEntity), SqlDialect.Sqlite, "0", false);
            DataSessionCache.TryAddPartialUpdateSql(key, "UPDATE first");

            for (int i = 0; i < DataSessionCache.PartialUpdateSqlCacheMaxEntries; i++)
            {
                var fill = (typeof(CacheProbeEntity), SqlDialect.Sqlite, "cols" + i, false);
                DataSessionCache.TryAddPartialUpdateSql(fill, "UPDATE fill-" + i);
            }

            var rejected = (typeof(CacheProbeEntity), SqlDialect.Sqlite, "cols_rejected", false);
            DataSessionCache.TryAddPartialUpdateSql(rejected, "UPDATE rejected");

            await Assert.That(DataSessionCache.PartialUpdateSqlCache.TryGetValue(rejected, out _)).IsFalse();
            await Assert.That(DataSessionCache.PartialUpdateSqlCache.TryGetValue(key, out string? first)).IsTrue();
            await Assert.That(first).IsEqualTo("UPDATE first");
        }
        finally
        {
            DataSessionCache.PartialUpdateSqlCache.Clear();
        }
    }

    [Test]
    public async Task PartialUpdateSqlCache_ClearResetsCounter_SoWritesResume()
    {
        try
        {
            DataSessionCache.PartialUpdateSqlCache.Clear();
            for (int i = 0; i < DataSessionCache.PartialUpdateSqlCacheMaxEntries; i++)
            {
                DataSessionCache.TryAddPartialUpdateSql(
                    (typeof(CacheProbeEntity), SqlDialect.Sqlite, "c" + i, false), "UPDATE " + i);
            }
            var full = (typeof(CacheProbeEntity), SqlDialect.Sqlite, "full", false);
            DataSessionCache.TryAddPartialUpdateSql(full, "UPDATE full");
            await Assert.That(DataSessionCache.PartialUpdateSqlCache.ContainsKey(full)).IsFalse();

            // ClearForTesting 是唯一的计数器复位入口（生产无消费方）——这里直接调用它验证复位
            DataSessionCache.ClearForTesting();
            DataSessionCache.TryAddPartialUpdateSql(full, "UPDATE after-clear");
            await Assert.That(DataSessionCache.PartialUpdateSqlCache.TryGetValue(full, out string? sql)).IsTrue();
            await Assert.That(sql).IsEqualTo("UPDATE after-clear");
        }
        finally
        {
            DataSessionCache.ClearForTesting();
        }
    }
}

/// <summary>容量测试用的类型占位（无 [Table]——只为给缓存键提供不同的 Type）。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "本类只作为缓存键里的 Type 占位（typeof 使用），不需要实例。")]
internal sealed class CacheProbeEntity
{
    public long Id { get; set; }
}