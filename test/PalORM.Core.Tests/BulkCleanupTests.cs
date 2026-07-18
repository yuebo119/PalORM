using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using PalORM.MySql;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

[Table("bulk_cleanup")]
internal sealed partial class BulkCleanupEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Column("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class BulkCleanupTests
{
    [Test]
    [Arguments("sqlite")]
    [Arguments("mysql")]
    public async Task BulkInsert_CleanupFailures_PreserveExecutionException(string provider)
    {
        var failures = new BulkFailureSet();
        await using var connection = new BulkFailureConnection(
            BulkFailureScenario.MainAndTransactionCleanup,
            failures);

        Exception? actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await BulkInsertAsync(provider, connection));

        await Assert.That(actual).IsSameReferenceAs(failures.Execution);
        await Assert.That(actual!.Data["PalORM.CommandCleanupException"])
            .IsSameReferenceAs(failures.MainCleanup);
        await Assert.That(actual.Data["PalORM.RollbackException"])
            .IsSameReferenceAs(failures.Rollback);
        await Assert.That(actual.Data["PalORM.TransactionCleanupException"])
            .IsSameReferenceAs(failures.TransactionCleanup);
    }

    [Test]
    [Arguments("sqlite")]
    [Arguments("mysql")]
    public async Task BulkInsert_ProbeCleanupWithoutPrimary_ThrowsCleanupException(
        string provider)
    {
        var failures = new BulkFailureSet();
        await using var connection = new BulkFailureConnection(
            BulkFailureScenario.ProbeCleanup,
            failures);

        Exception? actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await BulkInsertAsync(provider, connection));

        await Assert.That(actual).IsSameReferenceAs(failures.ProbeCleanup);
    }

    [Test]
    [Arguments("sqlite")]
    [Arguments("mysql")]
    public async Task BulkInsert_RowAndMainCleanupFailures_AreBothPreserved(
        string provider)
    {
        var failures = new BulkFailureSet();
        await using var connection = new BulkFailureConnection(
            BulkFailureScenario.RowAndMainCleanup,
            failures);

        Exception? actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await BulkInsertAsync(provider, connection));

        await Assert.That(actual).IsSameReferenceAs(failures.RowOperation);
        await Assert.That(actual!.Data["PalORM.RowCommandCleanupException"])
            .IsSameReferenceAs(failures.RowCleanup);
        await Assert.That(actual.Data["PalORM.CommandCleanupException"])
            .IsSameReferenceAs(failures.MainCleanup);
    }

    private static Task<long> BulkInsertAsync(
        string provider,
        DbConnection connection)
    {
        BulkCleanupEntity[] entities =
        [
            new() { Id = "1", Name = "item" }
        ];

        return provider switch
        {
            "sqlite" => SqliteProvider.BulkInsertAsync(
                connection, null, entities, 100, CancellationToken.None),
            "mysql" => MySqlProvider.BulkInsertAsync(
                connection, null, entities, 100, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
    }
}

internal enum BulkFailureScenario
{
    MainAndTransactionCleanup,
    ProbeCleanup,
    RowAndMainCleanup
}

internal sealed class BulkFailureSet
{
    internal InvalidOperationException Execution { get; } = new("execute failed");
    internal InvalidOperationException ProbeCleanup { get; } = new("probe cleanup failed");
    internal InvalidOperationException RowOperation { get; } = new("row operation failed");
    internal InvalidOperationException RowCleanup { get; } = new("row cleanup failed");
    internal InvalidOperationException MainCleanup { get; } = new("main cleanup failed");
    internal InvalidOperationException Rollback { get; } = new("rollback failed");
    internal InvalidOperationException TransactionCleanup { get; } = new("transaction cleanup failed");
}

internal sealed class BulkFailureConnection(
    BulkFailureScenario scenario,
    BulkFailureSet failures) : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;
    private int _commandCount;

    [AllowNull]
    public override string ConnectionString { get; set; } = "fake";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "1";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => new BulkFailureTransaction(this, scenario, failures);

    protected override DbCommand CreateDbCommand()
    {
        _commandCount++;
        return new BulkFailureCommand(_commandCount, scenario, failures);
    }
}

internal sealed class BulkFailureTransaction(
    DbConnection connection,
    BulkFailureScenario scenario,
    BulkFailureSet failures) : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
    protected override DbConnection DbConnection => connection;

    public override void Commit() { }
    public override void Rollback()
    {
        if (scenario == BulkFailureScenario.MainAndTransactionCleanup)
            throw failures.Rollback;
    }

    public override Task RollbackAsync(CancellationToken cancellationToken = default)
        => scenario == BulkFailureScenario.MainAndTransactionCleanup
            ? Task.FromException(failures.Rollback)
            : Task.CompletedTask;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        if (scenario == BulkFailureScenario.MainAndTransactionCleanup)
            throw failures.TransactionCleanup;
    }
}

internal sealed class BulkFailureCommand(
    int commandIndex,
    BulkFailureScenario scenario,
    BulkFailureSet failures) : DbCommand
{
    // 命令创建顺序（MultiValueBulkInsert 骨架）：probe=1、复用行命令=2、主命令=3（每批一个）
    private readonly BulkFailureParameterCollection _parameters = new(
        commandIndex == 2 && scenario == BulkFailureScenario.RowAndMainCleanup
            ? failures.RowOperation
            : null);

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;
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
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => throw new NotSupportedException();
    protected override DbParameter CreateDbParameter() => new BulkFailureParameter();

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        => commandIndex == 3
            && scenario == BulkFailureScenario.MainAndTransactionCleanup
                ? Task.FromException<int>(failures.Execution)
                : Task.FromResult(1);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        if (commandIndex == 1 && scenario == BulkFailureScenario.ProbeCleanup)
            throw failures.ProbeCleanup;
        if (commandIndex == 3
            && scenario is BulkFailureScenario.MainAndTransactionCleanup
                or BulkFailureScenario.RowAndMainCleanup)
            throw failures.MainCleanup;
        if (commandIndex == 2 && scenario == BulkFailureScenario.RowAndMainCleanup)
            throw failures.RowCleanup;
    }
}

internal sealed class BulkFailureParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;
    public override int Size { get; set; }
    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override void ResetDbType() { }
}

internal sealed class BulkFailureParameterCollection(
    Exception? addException) : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot;
    public override int Add(object value)
    {
        if (addException is not null) throw addException;
        _items.Add((DbParameter)value);
        return _items.Count - 1;
    }
    public override void AddRange(Array values) { foreach (object value in values) Add(value); }
    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains((DbParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName)
        => _items.FindIndex(parameter => parameter.ParameterName == parameterName);
    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        int index = IndexOf(parameterName);
        if (index < 0) _items.Add(value); else _items[index] = value;
    }
}
