using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>G1/G3（2026-10-13 五轮全量重读微项包）的双防线锁定测试。
/// <para><b>G1（单行物化管线）</b>：First/Single/SingleOrDefault 族改走 ExecuteQueryRowsAsync
/// 单行内核后，拦截器时序契约（OnBefore → OnAfter(实际行数) → OnError，与 ToList/ForEach
/// 管线逐位一致——PipelineParityContractTests 的同款序列断言扩到第三条管线）与
/// 0/1/多行的结果语义（空抛/返回/多行抛）此前无直接锁定（异常消息串全仓零引用），
/// 本组补齐；另锁截断防护：First 族的 1 行截断不得写入用户缓存键（ITM-406 家族）。</para>
/// <para><b>G3（共享默认过滤链头）</b>：SoftDelete 实体的无参默认过滤子句改为跨 builder
/// 共享静态链头节点后，副本隔离语义（同一会话先后两个 builder 分叉条件互不污染）是
/// 共享形态的新增风险面——链不可变是论证依据，本组用行为断言钉住。</para></summary>
[NotInParallel("FirstSinglePipeline")]
public sealed class FirstSinglePipelineAndSharedNodeTests
{
    private sealed class RecordingInterceptor : IQueryInterceptor
    {
        public List<string> Events { get; } = [];

        public void OnBefore(QueryContext context) => Events.Add("OnBefore");

        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount)
            => Events.Add($"OnAfter:{rowCount}");

        public void OnError(QueryContext context, Exception exception) => Events.Add("OnError");
    }

    private static async Task<(DataSession<SqliteProvider> Session, RecordingInterceptor Recorder)> CreateAsync()
    {
        var recorder = new RecordingInterceptor();
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:", Interceptors = [recorder] });
        await session.ExecuteAsync(
            $"CREATE TABLE fs_probe (id INTEGER PRIMARY KEY, name TEXT NOT NULL, deleted_at TEXT)");
        return (session, recorder);
    }

    private static async Task SeedAsync(DataSession<SqliteProvider> db, int count)
    {
        // PALORM005 豁免：种子逐行插入（夹具播种，DefaultFilterBehaviorTests 同形态）
#pragma warning disable PALORM005
        for (int i = 1; i <= count; i++)
            await db.InsertAsync(new FsProbe { Id = i, Name = "row" + i });
#pragma warning restore PALORM005
    }

    // ─── G1：拦截器时序契约（与 ToList/ForEach 管线同款序列断言）──────────────────

    [Test]
    public async Task FirstOrDefault_Hit_ReturnsRow_InterceptorSequenceMatchesPipeline()
    {
        (DataSession<SqliteProvider> session, RecordingInterceptor recorder) = await CreateAsync();
        await using (session)
        {
            await SeedAsync(session, 3);
            recorder.Events.Clear();

            FsProbe? row = await session.From<FsProbe>().OrderBy(x => x.Id).FirstOrDefaultAsync();

            await Assert.That(row!.Name).IsEqualTo("row1");
            // 单行管线的时序契约：与 ToList 族一致（OnBefore → OnAfter(实际行数)），无第三种形态
            await Assert.That(string.Join("|", recorder.Events)).IsEqualTo("OnBefore|OnAfter:1");
        }
    }

    [Test]
    public async Task FirstOrDefault_Empty_ReturnsNull_OnAfterCountIsZero()
    {
        (DataSession<SqliteProvider> session, RecordingInterceptor recorder) = await CreateAsync();
        await using (session)
        {
            recorder.Events.Clear();

            FsProbe? row = await session.From<FsProbe>().Where($"id = {999}").FirstOrDefaultAsync();

            await Assert.That(row).IsNull();
            await Assert.That(string.Join("|", recorder.Events)).IsEqualTo("OnBefore|OnAfter:0");
        }
    }

    // ─── G1：0/1/多行结果语义（此前无直接锁定）─────────────────────────────────

    [Test]
    public async Task First_Empty_ThrowsInvalidOperation()
    {
        (DataSession<SqliteProvider> session, RecordingInterceptor recorder) = await CreateAsync();
        await using (session)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.From<FsProbe>().FirstAsync());
        }
    }

    [Test]
    public async Task Single_Empty_Throws_AndTwoRows_Throws_OneRowReturns()
    {
        (DataSession<SqliteProvider> session, RecordingInterceptor recorder) = await CreateAsync();
        await using (session)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.From<FsProbe>().SingleAsync());

            await SeedAsync(session, 1);
            FsProbe single = await session.From<FsProbe>().SingleAsync();
            await Assert.That(single.Name).IsEqualTo("row1");

            // 单行内核读满 LIMIT 2 的两行：OnAfter 行数与 ToList 管线同口径（截断上限 2）
            recorder.Events.Clear();
            await SeedAsync(session, 2);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.From<FsProbe>().SingleAsync());
            await Assert.That(string.Join("|", recorder.Events)).IsEqualTo("OnBefore|OnAfter:2");
        }
    }

    [Test]
    public async Task SingleOrDefault_EmptyReturnsNull_OneRowReturns_TwoRowsThrow()
    {
        (DataSession<SqliteProvider> session, RecordingInterceptor _) = await CreateAsync();
        await using (session)
        {
            await Assert.That(await session.From<FsProbe>().SingleOrDefaultAsync()).IsNull();

            await SeedAsync(session, 1);
            await Assert.That((await session.From<FsProbe>().SingleOrDefaultAsync())!.Name)
                .IsEqualTo("row1");

            await SeedAsync(session, 2);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.From<FsProbe>().SingleOrDefaultAsync());
        }
    }

    [Test]
    public async Task FirstOrDefault_TruncatedResult_DoesNotPolluteUserCacheKey()
    {
        // ITM-406 家族在单行内核上的防线：内核结构性无缓存写入面，即便调用方设了 WithCache
        // 也不得把 1 行截断写进用户键——同键 ToListAsync 必须看到全量行
        (DataSession<SqliteProvider> session, RecordingInterceptor _) = await CreateAsync();
        await using (session)
        {
            await SeedAsync(session, 3);
            string key = $"fs-trunc-{Guid.NewGuid():N}";

            FsProbe? first = await session.From<FsProbe>()
                .OrderBy(x => x.Id).WithCache(key).FirstOrDefaultAsync();
            await Assert.That(first!.Name).IsEqualTo("row1");

            List<FsProbe> all = await session.From<FsProbe>()
                .OrderBy(x => x.Id).WithCache(key).ToListAsync();
            await Assert.That(all.Count).IsEqualTo(3);
        }
    }

    // ─── G3：共享默认过滤链头的副本隔离 ────────────────────────────────────────

    [Test]
    public async Task SharedSoftDeleteNode_BuildersFromSameSessionDivergeIndependently()
    {
        (DataSession<SqliteProvider> session, RecordingInterceptor _) = await CreateAsync();
        await using (session)
        {
            await session.ExecuteAsync(
                $"INSERT INTO fs_probe (id, name, deleted_at) VALUES ({1}, {"live"}, {null})");
            await session.ExecuteAsync(
                $"INSERT INTO fs_probe (id, name, deleted_at) VALUES ({2}, {"dead"}, {"2026-01-01"})");

            // 先建带条件的 builder（共享软删链头 + 用户条件），再建裸 builder——
            // 两者先后构建，后建的裸链不得被先建的条件污染，反之亦然
            var conditioned = session.From<FsProbe>().Where($"name = {"live"}");
            var bare = session.From<FsProbe>();

            await Assert.That(conditioned.AsDryRun().Sql).Contains("deleted_at");
            await Assert.That(conditioned.AsDryRun().Sql).Contains("name = @p0");
            await Assert.That(bare.AsDryRun().Sql).Contains("deleted_at");
            await Assert.That(bare.AsDryRun().Sql).DoesNotContain("name = @p0");

            List<FsProbe> viaConditioned = await conditioned.ToListAsync();
            List<FsProbe> viaBare = await bare.ToListAsync();
            await Assert.That(string.Join(",", viaConditioned.Select(static x => x.Name))).IsEqualTo("live");
            await Assert.That(string.Join(",", viaBare.Select(static x => x.Name))).IsEqualTo("live");
        }
    }
}

[SoftDelete]
[Table("fs_probe")]
internal sealed partial class FsProbe
{
    [Key]
    [Column("id")]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = "";
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
