using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

[Table("pipeline_probe_rows")]
internal sealed partial class PipelineProbeRow
{
    [Key][Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("value")] public long value { get; set; }
}

/// <summary>请求级查询管道的语义契约（SQLite 顺序回退路径）：结果与顺序执行一致、
/// 句柄生命周期、事务可见性、拦截器逐逻辑查询通知、不支持选项的明确拒绝。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "连接生命周期移交 DataSession（同既有测试口径）；keeper 由测试释放。")]
public sealed class QueryPipelineTests
{
    private sealed class RecordingInterceptor : IQueryInterceptor
    {
        internal int Before;
        internal int After;
        internal int Errors;
        public int Priority => 0;
        public void OnBefore(QueryContext context) => Before++;
        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) => After++;
        public void OnError(QueryContext context, Exception exception) => Errors++;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeper;
        internal DataSession<SqliteProvider> Session { get; }

        private Fixture(SqliteConnection keeper, DataSession<SqliteProvider> session)
        {
            _keeper = keeper;
            Session = session;
        }

        internal static async Task<Fixture> CreateAsync(string tag)
        {
            string cs = $"Data Source=pipeline_{tag}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keeper = new SqliteConnection(cs);
            await keeper.OpenAsync().ConfigureAwait(false);
            await using (DbCommand init = keeper.CreateCommand())
            {
                init.CommandText =
                    "CREATE TABLE pipeline_probe_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL, value INTEGER NOT NULL);"
                    + "INSERT INTO pipeline_probe_rows (id, name, value) VALUES (1, 'a', 10), (2, 'b', 20), (3, 'c', 30);";
                await init.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var conn = new SqliteConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);
            var session = new DataSession<SqliteProvider>(
                conn, new DbOptions { ConnectionString = cs }, []);
            return new Fixture(keeper, session);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().ConfigureAwait(false);
            await _keeper.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Pipeline_SqliteFallback_MatchesSequentialExecution()
    {
        await using Fixture fixture = await Fixture.CreateAsync("same");
        DataSession<SqliteProvider> session = fixture.Session;

        List<PipelineProbeRow> expectedList =
            await session.From<PipelineProbeRow>().Where($"\"value\" >= {20L}").ToListAsync();
        PipelineProbeRow? expectedFirst =
            await session.From<PipelineProbeRow>().Where($"\"id\" = {2L}").FirstOrDefaultAsync();
        long expectedCount = await session.CountAsync<PipelineProbeRow>($"\"value\" >= {10L}");

        var pipeline = session.CreatePipeline();
        PipelineQuery<PipelineProbeRow, SqliteProvider> list =
            pipeline.Query(session.From<PipelineProbeRow>().Where($"\"value\" >= {20L}"));
        PipelineFirst<PipelineProbeRow, SqliteProvider> first =
            pipeline.QueryFirst(session.From<PipelineProbeRow>().Where($"\"id\" = {2L}"));
        PipelineScalar<SqliteProvider> count =
            pipeline.Count(session.From<PipelineProbeRow>().Where($"\"value\" >= {10L}"));
        await pipeline.ExecuteAsync();
        await Assert.That(list.Value.Select(static row => row.id).ToArray())
            .IsEquivalentTo(expectedList.Select(static row => row.id).ToArray());
        await Assert.That(first.Value?.id).IsEqualTo(expectedFirst?.id);
        await Assert.That(count.Value).IsEqualTo(expectedCount);
        pipeline.Dispose();
    }

    [Test]
    public async Task Pipeline_FirstWithNoRows_ReturnsNull()
    {
        await using Fixture fixture = await Fixture.CreateAsync("norows");
        var pipeline = fixture.Session.CreatePipeline();
        PipelineFirst<PipelineProbeRow, SqliteProvider> first =
            pipeline.QueryFirst(fixture.Session.From<PipelineProbeRow>().Where($"\"id\" = {999L}"));
        await pipeline.ExecuteAsync();
        await Assert.That(first.Value).IsNull();
        pipeline.Dispose();
    }

    [Test]
    public async Task Pipeline_ReadingBeforeExecute_Throws()
    {
        await using Fixture fixture = await Fixture.CreateAsync("early");
        var pipeline = fixture.Session.CreatePipeline();
        PipelineQuery<PipelineProbeRow, SqliteProvider> handle =
            pipeline.Query(fixture.Session.From<PipelineProbeRow>());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => _ = handle.Value));
        pipeline.Dispose();
    }

    [Test]
    public async Task Pipeline_ExecuteTwice_Throws()
    {
        await using Fixture fixture = await Fixture.CreateAsync("twice");
        var pipeline = fixture.Session.CreatePipeline();
        _ = pipeline.Query(fixture.Session.From<PipelineProbeRow>());
        await pipeline.ExecuteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pipeline.ExecuteAsync());
        pipeline.Dispose();
    }

    [Test]
    public async Task Pipeline_EmptyExecute_IsNoOp_AndRejectsSecondExecute()
    {
        await using Fixture fixture = await Fixture.CreateAsync("empty");
        var pipeline = fixture.Session.CreatePipeline();
        // 空管道执行是合法空操作；管道单次使用，第二次执行必须响亮拒绝。
        await pipeline.ExecuteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync());
        pipeline.Dispose();
    }

    [Test]
    public async Task Pipeline_RejectsCacheAndTracing()
    {
        await using Fixture fixture = await Fixture.CreateAsync("reject");
        var pipeline = fixture.Session.CreatePipeline();
        Assert.Throws<NotSupportedException>(() =>
            _ = pipeline.Query(fixture.Session.From<PipelineProbeRow>().WithCache("pipeline-key")));
        Assert.Throws<NotSupportedException>(() =>
            _ = pipeline.Query(fixture.Session.From<PipelineProbeRow>().WithTracing()));
        pipeline.Dispose();
    }

    [Test]
    public async Task Pipeline_SeesRowsInsideTransaction_AndRollbackHidesThem()
    {
        await using Fixture fixture = await Fixture.CreateAsync("tx");
        DataSession<SqliteProvider> session = fixture.Session;
        await using (DbTransaction transaction = await session.BeginTransactionAsync())
        {
            await session.InsertAsync(new PipelineProbeRow { id = 4, name = "d", value = 40 });
            var pipeline = session.CreatePipeline();
            PipelineScalar<SqliteProvider> count = pipeline.Count(session.From<PipelineProbeRow>());
            await pipeline.ExecuteAsync();
            await Assert.That(count.Value).IsEqualTo(4L);
            pipeline.Dispose();
            await transaction.RollbackAsync();
        }

        var after = session.CreatePipeline();
        PipelineScalar<SqliteProvider> afterCount = after.Count(session.From<PipelineProbeRow>());
        await after.ExecuteAsync();
        await Assert.That(afterCount.Value).IsEqualTo(3L);
        after.Dispose();
    }

    [Test]
    public async Task Pipeline_Interceptors_AreNotifiedPerLogicalQuery()
    {
        await using Fixture fixture = await Fixture.CreateAsync("interceptors");
        var interceptor = new RecordingInterceptor();
        fixture.Session.AddInterceptor(interceptor);
        var pipeline = fixture.Session.CreatePipeline();
        _ = pipeline.Query(fixture.Session.From<PipelineProbeRow>());
        _ = pipeline.QueryFirst(fixture.Session.From<PipelineProbeRow>());
        _ = pipeline.Count(fixture.Session.From<PipelineProbeRow>());
        await pipeline.ExecuteAsync();
        pipeline.Dispose();
        await Assert.That(interceptor.Before).IsEqualTo(3);
        await Assert.That(interceptor.After).IsEqualTo(3);
        await Assert.That(interceptor.Errors).IsEqualTo(0);
    }
}
