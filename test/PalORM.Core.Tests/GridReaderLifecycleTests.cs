using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace PalORM.Core.Tests;

[Table("grid_lifecycle")]
internal sealed partial class GridLifecycleEntity
{
    [Key]
    public long Id { get; set; }
}

public sealed class GridReaderLifecycleTests
{
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
        Task<List<GridLifecycleEntity>> read = grid.ReadAsync<GridLifecycleEntity>().AsTask();
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
        await Assert.That(resources.Connection.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentDispose_SharesOneCleanupResult()
    {
        var resources = new GridFailureResources(failReaderDispose: true);
        GridReader grid = await resources.CreateGridReaderAsync();

        Task first = grid.DisposeAsync().AsTask();
        Task second = grid.DisposeAsync().AsTask();
        Exception? firstException = await Assert.ThrowsAsync<InvalidOperationException>(first);
        Exception? secondException = await Assert.ThrowsAsync<InvalidOperationException>(second);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(firstException).IsSameReferenceAs(secondException);
        await Assert.That(firstException).IsSameReferenceAs(resources.ReaderDisposeFailure);
        await Assert.That(resources.Reader.DisposeCount).IsEqualTo(1);
        await Assert.That(resources.Command.DisposeCount).IsEqualTo(1);
        await Assert.That(resources.Connection.DisposeCount).IsEqualTo(1);
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

    internal async ValueTask<GridReader> CreateGridReaderAsync()
    {
        ConnectionLease lease = await ConnectionLease.OpenOwnedAsync(
            () => Connection,
            CancellationToken.None);
        return new GridReader(Reader, Command, lease, null);
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
