using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>R3（v5.6.0）行为防线——ExecuteAsync（原始 DDL/DML）的拦截器三段式接入：
/// OnBefore 收到完整 SQL 与绑定参数表、OnAfter 收到受影响行数、失败路径触发 OnError
/// 且原始异常不被吞。此前该入口是拦截器覆盖面的文档化缺口（ITM-513/547），
/// 接入后由本用例钉住，防回归回"静默绕过"。</summary>
[NotInParallel("ExecuteAsyncInterceptor")]
public sealed class ExecuteAsyncInterceptorTests
{
    private sealed class RecordingInterceptor : IQueryInterceptor
    {
        public List<string> BeforeSql { get; } = [];
        public List<int> BeforeParamCounts { get; } = [];
        public List<(int Rows, TimeSpan Elapsed)> AfterCalls { get; } = [];
        public List<Exception> Errors { get; } = [];

        public void OnBefore(QueryContext context)
        {
            BeforeSql.Add(context.Sql);
            BeforeParamCounts.Add(context.Parameters.Count);
        }

        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount)
            => AfterCalls.Add((rowCount, elapsed));

        public void OnError(QueryContext context, Exception exception) => Errors.Add(exception);
    }

    [Test]
    public async Task ExecuteAsync_NotifiesBeforeAfterWithSqlAndParameters()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        var recorder = new RecordingInterceptor();
        _ = db.AddInterceptor(recorder);

        await db.ExecuteAsync($"CREATE TABLE r3_probe (id INTEGER PRIMARY KEY, v TEXT NOT NULL)");
        int inserted = await db.ExecuteAsync(
            $"INSERT INTO r3_probe (id, v) VALUES ({1L}, {"a"})");

        await Assert.That(inserted).IsEqualTo(1);
        await Assert.That(recorder.BeforeSql.Count).IsEqualTo(2);
        // SQL 全文与参数表可见（插值洞 → 绑定参数 2 个）
        await Assert.That(recorder.BeforeSql[0]).Contains("CREATE TABLE r3_probe");
        await Assert.That(recorder.BeforeSql[1]).Contains("INSERT INTO r3_probe");
        await Assert.That(recorder.BeforeParamCounts[1]).IsEqualTo(2);
        // OnAfter 的行数是 ExecuteNonQuery 的受影响行数
        await Assert.That(recorder.AfterCalls.Count).IsEqualTo(2);
        await Assert.That(recorder.AfterCalls[1].Rows).IsEqualTo(1);
        await Assert.That(recorder.Errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExecuteAsync_FailureTriggersOnError_PreservesOriginalException()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        var recorder = new RecordingInterceptor();
        _ = db.AddInterceptor(recorder);

        Exception? thrown = await Assert.ThrowsAsync<Exception>(async () =>
            await db.ExecuteAsync($"INSERT INTO missing_r3_table (x) VALUES ({1})"));
        ArgumentNullException.ThrowIfNull(thrown);

        await Assert.That(recorder.Errors.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(recorder.Errors[0], thrown)).IsTrue();
        await Assert.That(recorder.AfterCalls.Count).IsEqualTo(0); // 失败不触发 OnAfter
    }

    [Test]
    public async Task ExecuteAsync_WithoutInterceptors_UnchangedBehavior()
    {
        // 默认会话（无拦截器）零开销路径——行为与接入前一致
        await using var db = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        await db.ExecuteAsync($"CREATE TABLE r3_plain (id INTEGER PRIMARY KEY)");
        await Assert.That(
            await db.ExecuteAsync($"INSERT INTO r3_plain (id) VALUES ({1L})")).IsEqualTo(1);
    }
}
