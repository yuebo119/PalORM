using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>First/Single 族的 SQLite 字面量 LIMIT 形态（2026-09-22 性能实测落地）。
/// <para>该族的 take 由 API 固定为 1/2 且不带 Skip，SQLite 上内联成 <c>LIMIT 1</c> 字面量、
/// 不再发 OFFSET。依据：ADO.NET 层同连接同物化实测，字面量与无 LIMIT 同价（5.658 对 5.534 µs），
/// 参数化形态每次多付约 8 µs（13.557 µs），而该差值恰等于单行族相对无 LIMIT 查询的全部差价。
/// 走 PalORM 原始 SQL 通道的端到端对照同样成立（12.793 对 22.571 µs）。</para>
/// <para>本组钉住四件事：字面量形态生效（参数快照里没有 limit 参数）、Single 用 LIMIT 2、
/// 带 Skip 时退回参数化（保住 SHAPE-010 的有限形状集）、字面量值进形状缓存键
///（LIMIT 1 与 LIMIT 2 不得互相复用条目）。</para>
/// </summary>
[NotInParallel("SqlShapeCache")]
public sealed class FirstSingleLimitLiteralTests
{
    private sealed class SqlRecorder : IQueryInterceptor
    {
        public List<string> Sql { get; } = [];
        public List<int> ParamCounts { get; } = [];

        public void OnBefore(QueryContext context)
        {
            Sql.Add(context.Sql);
            ParamCounts.Add(context.Parameters.Count);
        }

        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) { }

        public void OnError(QueryContext context, Exception exception) { }
    }

    private static async Task<(DataSession<SqliteProvider> Session, SqlRecorder Recorder)> CreateAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        await session.ExecuteAsync(
            $"CREATE TABLE lit_limit_probe (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        var recorder = new SqlRecorder();
        _ = session.AddInterceptor(recorder);
        return (session, recorder);
    }

    [Test]
    public async Task FirstOrDefault_EmitsLiteralLimitOne_WithoutLimitParameter()
    {
        (DataSession<SqliteProvider> session, SqlRecorder recorder) = await CreateAsync();
        await using (session)
        {
            _ = await session.From<LitLimitProbeEntity>()
                .Where($"name = {"probe"}").FirstOrDefaultAsync();

            await Assert.That(recorder.Sql.Count).IsEqualTo(1);
            string sql = recorder.Sql[0];
            await Assert.That(sql).Contains("LIMIT 1");
            await Assert.That(sql).DoesNotContain("LIMIT @");
            await Assert.That(sql).DoesNotContain("OFFSET");
            // 只有 WHERE 一个参数——limit 值不再走参数快照
            await Assert.That(recorder.ParamCounts[0]).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Single_EmitsLiteralLimitTwo()
    {
        (DataSession<SqliteProvider> session, SqlRecorder recorder) = await CreateAsync();
        await using (session)
        {
            _ = await session.From<LitLimitProbeEntity>()
                .Where($"name = {"probe"}").SingleOrDefaultAsync();

            await Assert.That(recorder.Sql[0]).Contains("LIMIT 2");
            await Assert.That(recorder.Sql[0]).DoesNotContain("OFFSET");
            await Assert.That(recorder.ParamCounts[0]).IsEqualTo(1);
        }
    }

    [Test]
    public async Task FirstOrDefault_WithSkip_FallsBackToParameterizedLimit()
    {
        // 带 Skip 时值域无界（动态分页），必须回到参数化，否则形状缓存会按 skip 值膨胀
        (DataSession<SqliteProvider> session, SqlRecorder recorder) = await CreateAsync();
        await using (session)
        {
            _ = await session.From<LitLimitProbeEntity>()
                .Where($"name = {"probe"}").Skip(5).FirstOrDefaultAsync();

            string sql = recorder.Sql[0];
            await Assert.That(sql).Contains("LIMIT @");
            await Assert.That(sql).Contains("OFFSET @");
            // WHERE + take + skip
            await Assert.That(recorder.ParamCounts[0]).IsEqualTo(3);
        }
    }

    [Test]
    public async Task UserTake_StaysParameterized()
    {
        // 用户 Take 的值域无界，维持 SHAPE-010 的参数化（与 First/Single 族的字面量区分开）
        await using DataSession<SqliteProvider> session = (await CreateAsync()).Session;
        DryRunResult dry = session.From<LitLimitProbeEntity>()
            .Where($"name = {"probe"}").Take(10).AsDryRun();

        await Assert.That(dry.Sql).Contains("LIMIT @");
        // WHERE + take + skip（SQLite take-only 仍发 OFFSET 参数并绑 0，成本实测为零，未动）
        await Assert.That(dry.Parameters.Count).IsEqualTo(3);
    }

    [Test]
    public async Task LiteralTakeValues_OccupyDistinctCacheEntries()
    {
        // 字面量值写进 SQL 文本 ⇒ 值必须进形状键：LIMIT 1 与 LIMIT 2 两条文本各自一条条目，
        // 不互相复用（复用会让 First 拿到 Single 的 SQL）
        SqlShapeCache.Clear();
        try
        {
            (DataSession<SqliteProvider> session, SqlRecorder recorder) = await CreateAsync();
            await using (session)
            {
                _ = await session.From<LitLimitProbeEntity>()
                    .Where($"name = {"probe-a"}").FirstOrDefaultAsync();
                _ = await session.From<LitLimitProbeEntity>()
                    .Where($"name = {"probe-a"}").SingleOrDefaultAsync();

                string firstSql = recorder.Sql[0];
                string singleSql = recorder.Sql[1];
                await Assert.That(firstSql).IsNotEqualTo(singleSql);
                await Assert.That(SqlShapeCache.Entries.Count(e => e.FullSql == firstSql)).IsEqualTo(1);
                await Assert.That(SqlShapeCache.Entries.Count(e => e.FullSql == singleSql)).IsEqualTo(1);
            }
        }
        finally { SqlShapeCache.Clear(); }
    }
}

[Table("lit_limit_probe")]
internal sealed partial class LitLimitProbeEntity
{
    [Key]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = string.Empty;
}
