using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace PalORM.Core.Tests;

public sealed class GridReaderLifecycleTests
{
    // 类级串行：ITM-882 测试挂进程级 ActivityListener 并捕获引用，与其他测试的
    // PalORM activity 并行会覆盖捕获（B74：无参形态按 TUnit 官方语义与任何测试互斥）。
    [Test]
    public async Task ConcurrentRead_FailsFast_AndFirstReadCompletes()
    {
        var resources = new GridFailureResources();
        await using var grid = await resources.CreateGridReaderAsync();
        Task<List<GridLifecycleEntity>> first = grid.ReadAsync<GridLifecycleEntity>().AsTask();
        await resources.Reader.ReadStarted.Task;

        await Assert.That(async () => await grid.ReadAsync<GridLifecycleEntity>())
            .Throws<InvalidOperationException>();

        resources.Reader.AllowRead.TrySetResult();
        await Assert.That((await first).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeDuringRead_WaitsAndRejectsNewReads()
    {
        var resources = new GridFailureResources();
        GridReader grid = await resources.CreateGridReaderAsync();
#pragma warning disable S5034 // ValueTask 经 .AsTask() 显式转 Task 后多次 await Task 是合法的，非双消费
        Task<List<GridLifecycleEntity>> read = grid.ReadAsync<GridLifecycleEntity>().AsTask();
#pragma warning restore S5034
        await resources.Reader.ReadStarted.Task;

        Task dispose = grid.DisposeAsync().AsTask();
        await Assert.That(dispose.IsCompleted).IsFalse();
        await Assert.That(async () => await grid.ReadAsync<GridLifecycleEntity>())
            .Throws<ObjectDisposedException>();

        resources.Reader.AllowRead.TrySetResult();
        await read;
        await dispose;
        await Assert.That(resources.Reader.DisposeCount).IsEqualTo(1);
        await Assert.That(resources.Command.DisposeCount).IsEqualTo(1);
        // v5.6：连接归会话所有——GridReader 释放命令与读取器，但不释放被借用的连接
        await Assert.That(resources.Connection.DisposeCount).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentDispose_SharesOneCleanupResult()
    {
        var resources = new GridFailureResources(failReaderDispose: true);
        GridReader grid = await resources.CreateGridReaderAsync();
#pragma warning disable S5034 // ValueTask 经 .AsTask() 显式转 Task 后多次 await Task 是合法的，非双消费

        Task first = grid.DisposeAsync().AsTask();
        Task second = grid.DisposeAsync().AsTask();
        // TUnit 1.x：Assert.ThrowsAsync<T>(Task) 重载移除，需 Func<Task>。
        // first/second 是已启动的 ValueTask.AsTask() 副本（GridReader 内部幂等），
        // 用 _ => first 把"已启动的 Task"包装成委托以满足新签名。
        Exception? firstException = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Exception? secondException = await Assert.ThrowsAsync<InvalidOperationException>(() => second);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(firstException).IsSameReferenceAs(secondException);
        await Assert.That(firstException).IsSameReferenceAs(resources.ReaderDisposeFailure);
        await Assert.That(resources.Reader.DisposeCount).IsEqualTo(1);
        await Assert.That(resources.Command.DisposeCount).IsEqualTo(1);
        await Assert.That(resources.Connection.DisposeCount).IsEqualTo(0);
    }

    // ITM-882 第一面：被拒读取必须经 catch 完成观测——修复前 ReadFirstAsync 的 EnterRead
    // 在 try 外，"already has an active read" 的 InvalidOperationException 不经 catch →
    // 观测悬挂（Activity 不 Dispose、Duration 恒 Zero）。
    // 选并发拒绝而非 Dispose 后调用：DisposeAsync 自身会 Complete("success")，那条路径上
    // 修复前后观测终态相同（都被 Dispose 收口），只有"观测未完成时被拒"才暴露差异。
    // 断言面捕获 activity 引用看 Duration 与 outcome tag（类级 NotInParallel 消除并行污染）；
    // finally 放行 AllowRead：断言失败传播时 await using 的 DisposeAsync 必须能等到
    // 阻塞中的读完成，否则 5 分钟 DisposeWaitTimeout 超时会掩盖真正的断言失败。
    [Test]
    public async Task ReadFirstAsync_RejectedWhileActive_CompletesObservationAsError()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == PalORMMetrics.ActivitySourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        Activity? captured = null;
        listener.ActivityStarted = activity => captured = activity;

        var resources = new GridFailureResources();
        var observation = new QueryObservation(tracingEnabled: true, metricsEnabled: false,
            operation: "QueryMultiple", provider: "SQLite");
        await using var grid = new GridReader(resources.Reader, resources.Command, observation);

        Task<List<GridLifecycleEntity>> first = grid.ReadAsync<GridLifecycleEntity>().AsTask();
        await resources.Reader.ReadStarted.Task;

        try
        {
            await Assert.That(async () => await grid.ReadFirstAsync<GridLifecycleEntity>())
                .Throws<InvalidOperationException>();
            // Complete("error") → SetStatus + Dispose（隐式 Stop，Duration 固化为正值）；
            // 修复前 Complete 未执行，Duration 恒 Zero
            await Assert.That(captured).IsNotNull();
            await Assert.That(captured!.Duration).IsGreaterThan(TimeSpan.Zero);
            string? outcome = captured.Tags.FirstOrDefault(t => t.Key == "palorm.outcome").Value;
            await Assert.That(outcome).IsEqualTo("error");
        }
        finally
        {
            resources.Reader.AllowRead.TrySetResult();
        }
        await Assert.That((await first).Count).IsEqualTo(0);
    }

    // ITM-882 第二面（entered 标记）：被拒调用不得误清活动读标记。修复前 ReadAsync 的
    // 无条件 finally ExitRead（ITM-813 引入）在被拒时清掉在飞读的 _activeRead 并 TrySetResult——
    // 下一个调用趁窗口穿过 EnterRead 与第一个读并发进 _reader。被拒方必须用 ReadAsync
    // （修复前它才携带误清行为；ReadFirstAsync 在 try 外抛、无 finally，不误清）。
    // 锁定的行为：被拒后再来的调用仍被 EnterRead 以"already has an active read"拒绝，
    // 而不是进到 reader 层（fake reader 的 "concurrent reader access" 消息）。
    [Test]
    public async Task ReadRejected_DoesNotReleaseActiveReadMarker_ForSubsequentCalls()
    {
        var resources = new GridFailureResources();
        await using var grid = await resources.CreateGridReaderAsync();
        Task<List<GridLifecycleEntity>> first = grid.ReadAsync<GridLifecycleEntity>().AsTask();
        await resources.Reader.ReadStarted.Task;

        try
        {
            await Assert.That(async () => await grid.ReadAsync<GridLifecycleEntity>())
                .Throws<InvalidOperationException>();

            // 修复前：上一次被拒的 finally 已清标记，本次会穿过 EnterRead 进入 fake reader
            Exception? second = await Assert.ThrowsAsync<InvalidOperationException>(
                () => grid.ReadAsync<GridLifecycleEntity>().AsTask());
            await Assert.That(second!.Message).Contains("already has an active read");
        }
        finally
        {
            resources.Reader.AllowRead.TrySetResult();
        }
        await Assert.That((await first).Count).IsEqualTo(0);
    }
}

internal sealed class GridFailureResources
{
    internal GridFailureResources(bool failReaderDispose = false)
    {
        Reader = new GridBlockingReader(
            failReaderDispose ? ReaderDisposeFailure : null);
    }

    internal InvalidOperationException ReaderDisposeFailure { get; } = new("reader dispose failed");
    internal GridBlockingReader Reader { get; }
    internal GridTrackingCommand Command { get; } = new();
    internal GridTrackingConnection Connection { get; } = new();

    // CA2000：GridReader 的所有权在本夹具中转移给调用方（测试以 await using 释放），
    // 与方法是否 async 无关——分析器此前仅因方法体内含 await 边界而未告警。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "GridReader ownership transfers to the test, which disposes it via await using.")]
    internal ValueTask<GridReader> CreateGridReaderAsync()
    {
        // M3-6：租约已退场（借用语义无资源可释放，连接由会话持有）——
        // GridReader 构造不再收连接参数，释放链为 reader→command→operation。
        return ValueTask.FromResult(new GridReader(Reader, Command, null));
    }
}

internal sealed class GridBlockingReader(Exception? disposeException) : DbDataReader
{
    private int _readCalls;
    internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource AllowRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int DisposeCount { get; private set; }

    public override int FieldCount => 1;
    public override bool HasRows => false;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int Depth => 0;
    public override object this[int ordinal] => 0L;
    public override object this[string name] => 0L;

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _readCalls) != 1)
            throw new InvalidOperationException("concurrent reader access");
        ReadStarted.TrySetResult();
        await AllowRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => Task.FromResult(false);

    public override async ValueTask DisposeAsync()
    {
        DisposeCount++;
        await base.DisposeAsync().ConfigureAwait(false);
        if (disposeException is not null) throw disposeException;
    }

    public override bool Read() => throw new NotSupportedException();
    public override bool NextResult() => false;
    public override string GetName(int ordinal) => "id";
    public override int GetOrdinal(string name) => 0;
    public override object GetValue(int ordinal) => 0L;
    public override int GetValues(object[] values) { values[0] = 0L; return 1; }
    public override bool IsDBNull(int ordinal) => false;
    public override string GetDataTypeName(int ordinal) => "INTEGER";
    public override Type GetFieldType(int ordinal) => typeof(long);
    public override bool GetBoolean(int ordinal) => false;
    public override byte GetByte(int ordinal) => 0;
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override char GetChar(int ordinal) => '\0';
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
    public override Guid GetGuid(int ordinal) => Guid.Empty;
    public override short GetInt16(int ordinal) => 0;
    public override int GetInt32(int ordinal) => 0;
    public override long GetInt64(int ordinal) => 0;
    public override float GetFloat(int ordinal) => 0;
    public override double GetDouble(int ordinal) => 0;
    public override string GetString(int ordinal) => string.Empty;
    public override decimal GetDecimal(int ordinal) => 0;
    public override DateTime GetDateTime(int ordinal) => default;
    public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
}

internal sealed class GridTrackingCommand : DbCommand
{
    private readonly BulkFailureParameterCollection _parameters = new(null);
    internal int DisposeCount { get; private set; }
    [AllowNull] public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() => throw new NotSupportedException();
    protected override DbParameter CreateDbParameter() => new BulkFailureParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    public override async ValueTask DisposeAsync() { DisposeCount++; await base.DisposeAsync().ConfigureAwait(false); }
}

internal sealed class GridTrackingConnection : DbConnection
{
    internal int DisposeCount { get; private set; }
    [AllowNull] public override string ConnectionString { get; set; } = "fake";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "1";
    public override ConnectionState State => ConnectionState.Open;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    public override async ValueTask DisposeAsync() { DisposeCount++; await base.DisposeAsync().ConfigureAwait(false); }
}

#region Test Entities
[Table("grid_lifecycle")]
// PALORM023/024：测试专用的"仅 Key"实体——验证 GridReader 在最小列集下的生命周期
#pragma warning disable PALORM023, PALORM024
internal sealed partial class GridLifecycleEntity
#pragma warning restore PALORM023, PALORM024
{
    [Key]
    public long Id { get; set; }
}
#endregion
