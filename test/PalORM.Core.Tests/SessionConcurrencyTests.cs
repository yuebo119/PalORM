using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

[Table("session_concurrency")]
internal sealed partial class SessionConcurrencyEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class SessionConcurrencyTests
{
    [Test]
    public async Task OverlappingOperations_OnSameSession_FailFast()
    {
        await using var resources = new ConcurrencyResources();
        Task<long> first = resources.Session.ScalarAsync<long>($"SELECT controlled").AsTask();
        await resources.Connection.Started;

        Exception? overlapException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resources.Session.ExecuteAsync($"SELECT 1"));
        resources.Connection.Release();
        await first;

        await Assert.That(overlapException.Message)
            .IsEqualTo("DataSession already has an active database operation.");
    }

    [Test]
    public async Task OverlappingOperations_OnIndependentSessions_RunConcurrently()
    {
        await using var firstResources = new ConcurrencyResources();
        await using var secondResources = new ConcurrencyResources();

        Task<long> first = firstResources.Session.ScalarAsync<long>($"SELECT controlled").AsTask();
        Task<long> second = secondResources.Session.ScalarAsync<long>($"SELECT controlled").AsTask();
        await Task.WhenAll(
            firstResources.Connection.Started,
            secondResources.Connection.Started);

        firstResources.Connection.Release();
        secondResources.Connection.Release();
        await Task.WhenAll(first, second);
    }

    [Test]
    public async Task CancelledOperation_ReleasesSessionForNextOperation()
    {
        await using var resources = new ConcurrencyResources();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.That(async () =>
                await resources.Session.ExecuteAsync(
                    $"SELECT controlled", cancellation.Token))
            .Throws<OperationCanceledException>();

        int affected = await resources.Session.ExecuteAsync($"SELECT 1");
        await Assert.That(affected).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeDuringOperation_WaitsAndRejectsNewOperations()
    {
        var resources = new ConcurrencyResources();
        try
        {
            Task<long> operation = resources.Session
                .ScalarAsync<long>($"SELECT controlled").AsTask();
            await resources.Connection.Started;

            Task dispose = resources.Session.DisposeAsync().AsTask();
            bool completedBeforeRelease = dispose.IsCompleted;
            Exception? newOperationException = await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await resources.Session.ExecuteAsync($"SELECT 1"));

            resources.Connection.Release();
            await operation;
            await dispose;

            await Assert.That(completedBeforeRelease).IsFalse();
            await Assert.That(newOperationException).IsTypeOf<ObjectDisposedException>();
        }
        finally
        {
            await resources.DisposeAsync();
        }
    }

    [Test]
    public async Task ChildFlow_FromActiveOperation_CannotReenterSession()
    {
        await using var resources = new ConcurrencyResources();
        var childCompleted = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        resources.Connection.OnOperationStarted = async () =>
        {
            try
            {
                await Task.Run(async () =>
                    await resources.Session.ExecuteAsync($"SELECT 1"));
                childCompleted.TrySetResult(null);
            }
            catch (Exception exception)
            {
                childCompleted.TrySetResult(exception);
            }
        };

        Task<long> operation = resources.Session
            .ScalarAsync<long>($"SELECT controlled").AsTask();
        Exception? childException = await childCompleted.Task;
        resources.Connection.Release();
        await operation;

        await Assert.That(childException?.Message)
            .IsEqualTo("DataSession already has an active database operation.");
    }

    [Test]
    public async Task DisposeInsideActiveOperation_FailsFastWithoutEndingSession()
    {
        await using var resources = new ConcurrencyResources();
        var disposeCompleted = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        resources.Connection.OnOperationStarted = async () =>
        {
            try
            {
                await resources.Session.DisposeAsync();
                disposeCompleted.TrySetResult(null);
            }
            catch (Exception exception)
            {
                disposeCompleted.TrySetResult(exception);
            }
        };

        Task<long> operation = resources.Session
            .ScalarAsync<long>($"SELECT controlled").AsTask();
        Exception? exception = await disposeCompleted.Task;
        resources.Connection.Release();
        await operation;
        int affected = await resources.Session.ExecuteAsync($"SELECT 1");

        await Assert.That(exception?.Message).IsEqualTo(
            "DataSession cannot be disposed from its active operation or transaction scope.");
        await Assert.That(affected).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeWithExplicitTransaction_FailsUntilTransactionCompletes()
    {
        DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        try
        {
            await using DbTransaction transaction = await session.BeginTransactionAsync();
            await Assert.That(async () => await session.DisposeAsync())
                .Throws<InvalidOperationException>()
                .WithMessage(
                    "Complete or dispose the active transaction before disposing DataSession.");
            await transaction.CommitAsync();

            await session.DisposeAsync();
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Test]
    public async Task DisposeDuringTransactionCallback_WaitsForScopeCompletion()
    {
        DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task transaction = session.WithTransaction(async _ =>
            {
                callbackEntered.TrySetResult();
                await releaseCallback.Task;
            }).AsTask();
            await callbackEntered.Task;

            Task dispose = session.DisposeAsync().AsTask();
            bool completedBeforeRelease = dispose.IsCompleted;
            releaseCallback.TrySetResult();
            await transaction;
            await dispose;

            await Assert.That(completedBeforeRelease).IsFalse();
        }
        finally
        {
            releaseCallback.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Test]
    public async Task DisposeInsideTransactionScope_FailsFastWithoutEndingSession()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();

        await session.WithTransaction(async _ =>
        {
            await Assert.That(async () => await session.DisposeAsync())
                .Throws<InvalidOperationException>()
                .WithMessage("DataSession cannot be disposed from its active operation or transaction scope.");
        });

        await session.ExecuteAsync($"SELECT 1");
    }

    [Test]
    public async Task WithTransaction_PreservesActionFailureWhenTransactionDisposeFails()
    {
        await using var resources = new ConcurrencyResources();
        resources.Connection.TransactionDisposeFailure =
            new InvalidOperationException("transaction dispose failed");
        var actionFailure = new InvalidOperationException("action failed");

        Exception? exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resources.Session.WithTransaction(
                _ => Task.FromException(actionFailure)));

        await Assert.That(exception).IsSameReferenceAs(actionFailure);
        await Assert.That(
                exception.Data["PalORM.TransactionCleanupException"])
            .IsSameReferenceAs(resources.Connection.TransactionDisposeFailure);
    }

    [Test]
    public async Task WithTransaction_AllowsSequentialOperations()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();

        await session.WithTransaction(async ct =>
        {
            await session.ExecuteAsync($"CREATE TABLE tx_items (id INTEGER)", ct);
            await session.ExecuteAsync($"INSERT INTO tx_items (id) VALUES ({1})", ct);
        });

        long count = await session.ScalarAsync<long>($"SELECT COUNT(*) FROM tx_items");
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task WithTransaction_RejectsSiblingFlow()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        var startSibling = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task sibling = Task.Run(async () =>
        {
            await startSibling.Task;
            await session.ExecuteAsync($"SELECT 1");
        });

        await session.WithTransaction(async _ =>
        {
            startSibling.TrySetResult();
            Exception? exception = await Assert.ThrowsAsync<InvalidOperationException>(sibling);
            await Assert.That(exception.Message)
                .IsEqualTo("The active transaction belongs to another asynchronous flow.");
        });
    }

    [Test]
    public async Task WithTransaction_RejectsNestedTransaction()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();

        await session.WithTransaction(async ct =>
        {
            await Assert.That(async () =>
                    await session.WithTransaction(_ => Task.CompletedTask, ct: ct))
                .Throws<InvalidOperationException>()
                .WithMessage("DataSession does not support nested transactions.");
        });
    }

    [Test]
    public async Task QueryBuilder_UsesSameSessionOperationGate()
    {
        await using var resources = new ConcurrencyResources();
        Task<long> first = resources.Session
            .ScalarAsync<long>($"SELECT controlled").AsTask();
        await resources.Connection.Started;

        Exception? exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resources.Session.From<SessionConcurrencyEntity>().ToListAsync());
        resources.Connection.Release();
        await first;

        await Assert.That(exception.Message)
            .IsEqualTo("DataSession already has an active database operation.");
    }

    [Test]
    public async Task GridReader_HoldsSessionUntilDisposed()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        await CreateConcurrencyTableAsync(session);

        GridReader grid = await session.From<SessionConcurrencyEntity>()
            .QueryMultipleAsync($"SELECT * FROM session_concurrency");
        await Assert.That(async () => await session.ExecuteAsync($"SELECT 1"))
            .Throws<InvalidOperationException>()
            .WithMessage("DataSession already has an active database operation.");

        await grid.DisposeAsync();
        int affected = await session.ExecuteAsync($"SELECT 1");
        await Assert.That(affected).IsEqualTo(-1);
    }

    [Test]
    public async Task SaveWithDefaultKey_ReentersInsertWithinOneOperation()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        await CreateConcurrencyTableAsync(session);

        SessionConcurrencyEntity saved = await session.SaveAsync(
            new SessionConcurrencyEntity { Name = "saved" });

        await Assert.That(saved.Id).IsGreaterThan(0);
        await Assert.That(saved.Name).IsEqualTo("saved");
    }

    [Test]
    public async Task BulkDelete_ReentersOwnedTransactionWithinOneOperation()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        await CreateConcurrencyTableAsync(session);
        SessionConcurrencyEntity entity = await session.InsertAsync(
            new SessionConcurrencyEntity { Name = "delete" });

        long affected = await session.BulkDeleteAsync<SessionConcurrencyEntity>([entity.Id]);
        SessionConcurrencyEntity? stored = await session.GetAsync<SessionConcurrencyEntity>(entity.Id);

        await Assert.That(affected).IsEqualTo(1);
        await Assert.That(stored).IsNull();
    }

    [Test]
    public async Task ExplicitTransaction_AfterCommit_DoesNotLeakIntoQueryBuilder()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        await CreateConcurrencyTableAsync(session);
        await using DbTransaction transaction = await session.BeginTransactionAsync();
        await transaction.CommitAsync();

        List<SessionConcurrencyEntity> rows = await session
            .From<SessionConcurrencyEntity>().ToListAsync();

        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task BulkUpdate_ReentersSequentialCrudWithinOneOperation()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        await CreateConcurrencyTableAsync(session);
        SessionConcurrencyEntity entity = await session.InsertAsync(
            new SessionConcurrencyEntity { Name = "before" });
        entity.Name = "after";

        long affected = await session.BulkUpdateAsync([entity]);
        SessionConcurrencyEntity? stored = await session.GetAsync<SessionConcurrencyEntity>(entity.Id);

        await Assert.That(affected).IsEqualTo(1);
        await Assert.That(stored?.Name).IsEqualTo("after");
    }

    [Test]
    public async Task WithTransaction_FailureAndCancellation_ReleaseSession()
    {
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();

        await Assert.That(async () => await session.WithTransaction(
                _ => Task.FromException(new InvalidOperationException("failed"))))
            .Throws<InvalidOperationException>();
        await session.ExecuteAsync($"SELECT 1");

        using var cancellation = new CancellationTokenSource();
        await Assert.That(async () => await session.WithTransaction(async ct =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }, ct: cancellation.Token))
            .Throws<OperationCanceledException>();
        await session.ExecuteAsync($"SELECT 1");
    }

    [Test]
    public async Task DisposeDuringTransactionCreation_RejectsTransactionResult()
    {
        await using var resources = new ConcurrencyResources();
        resources.Connection.BlockTransactionCreation = true;

        Task<DbTransaction> transaction = resources.Session
            .BeginTransactionAsync().AsTask();
        await resources.Connection.TransactionStarted;
        Task dispose = resources.Session.DisposeAsync().AsTask();

        resources.Connection.ReleaseTransaction();
        Exception? exception = null;
        DbTransaction? unexpectedTransaction = null;
        try
        {
            unexpectedTransaction = await transaction;
        }
        catch (Exception caught)
        {
            exception = caught;
        }
        await dispose;
        if (unexpectedTransaction is not null)
            await unexpectedTransaction.DisposeAsync();

        await Assert.That(exception).IsTypeOf<ObjectDisposedException>();
    }

    [Test]
    [NotInParallel("CacheStore")]
    public async Task CachedQuery_AfterSessionDispose_IsRejected()
    {
        CacheStore.Clear();
        await using var resources = new ConcurrencyResources();
        QueryBuilder<SessionConcurrencyEntity> query = resources.Session
            .From<SessionConcurrencyEntity>()
            .WithCache("disposed-session");
        CacheStore.Set("disposed-session", new List<SessionConcurrencyEntity>());
        await resources.Session.DisposeAsync();

        try
        {
            await Assert.That(async () => await query.ToListAsync())
                .Throws<ObjectDisposedException>();
        }
        finally
        {
            CacheStore.Clear();
        }
    }

    [Test]
    public async Task DisposeDuringTransactionCallback_AllowsRemainingOperation()
    {
        DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task transaction = session.WithTransaction(async ct =>
            {
                callbackEntered.TrySetResult();
                await releaseCallback.Task;
                await session.ExecuteAsync($"SELECT 1", ct);
            }).AsTask();
            await callbackEntered.Task;

            Task dispose = session.DisposeAsync().AsTask();
            releaseCallback.TrySetResult();
            await transaction;
            await dispose;
        }
        finally
        {
            releaseCallback.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Test]
    public async Task BulkOwnedTransaction_DisposeWaitsForBulkCompletion()
    {
        await using var resources = new ConcurrencyResources();
        resources.Connection.BlockTransactionCreation = true;

        Task<long> bulk = resources.Session
            .BulkDeleteAsync<SessionConcurrencyEntity>([1L]).AsTask();
        await resources.Connection.TransactionStarted;
        Task dispose = resources.Session.DisposeAsync().AsTask();

        resources.Connection.ReleaseTransaction();
        long affected = await bulk;
        await dispose;

        await Assert.That(affected).IsEqualTo(0);
    }

    [Test]
    public async Task UseTransaction_AfterSessionDispose_IsRejected()
    {
        await using var resources = new ConcurrencyResources();
        await resources.Session.DisposeAsync();

        await Assert.That(() => resources.Session.UseTransaction(null))
            .Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task UseTransaction_DisposedTransaction_ReportsDisposedNotBelonging()
    {
        // ITM-606：传入已 dispose 的事务（Connection == null）应抛"disposed transaction"消息，
        // 不应被 ReferenceEquals(null, _conn) 遮蔽为"事务必须属于当前 DataSession 的主连接"。
        await using var resources = new ConcurrencyResources();
        await using var tran = await resources.Session.BeginTransactionAsync();
        await tran.DisposeAsync();  // Connection 变 null

        var ex = await Assert.That(() => resources.Session.UseTransaction(tran))
            .Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("disposed transaction");
        // 确保未被遮蔽为"事务必须属于主连接"消息
        await Assert.That(ex!.Message.Contains("主连接", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task StoredProc_UsesCurrentSessionTransaction()
    {
        await using var resources = new ConcurrencyResources();
        await using DbTransaction transaction =
            await resources.Session.BeginTransactionAsync();

        await resources.Session.StoredProc("test_proc").ExecuteAsync();

        await Assert.That(resources.Connection.LastCommandTransaction)
            .IsSameReferenceAs(transaction);
        await transaction.CommitAsync();
    }

    [Test]
    public async Task QueryBuilder_CreatedBeforeTransaction_UsesTransactionAtExecution()
    {
        await using var resources = new ConcurrencyResources();
        QueryBuilder<SessionConcurrencyEntity> query = resources.Session
            .From<SessionConcurrencyEntity>()
            .Set(entity => entity.Name, "updated");
        await using DbTransaction transaction =
            await resources.Session.BeginTransactionAsync();

        await query.ExecuteNonQueryAsync();

        await Assert.That(resources.Connection.LastCommandTransaction)
            .IsSameReferenceAs(transaction);
        await transaction.CommitAsync();
    }

    [Test]
    public async Task QueryBuilder_CreatedDuringTransaction_ResolvesTransactionAtExecution()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSqliteSessionAsync();
        await CreateConcurrencyTableAsync(session);
        await using DbTransaction transaction = await session.BeginTransactionAsync();
        QueryBuilder<SessionConcurrencyEntity> query =
            session.From<SessionConcurrencyEntity>();
        await transaction.CommitAsync();

        List<SessionConcurrencyEntity> rows = await query.ToListAsync();

        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task WithTransaction_DisposesUnreleasedGridBeforeCommit()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSqliteSessionAsync();
        await CreateConcurrencyTableAsync(session);
        GridReader? grid = null;

        Exception? exception = null;
        bool gridDisposedByTransaction = false;
        try
        {
            try
            {
                await session.WithTransaction(async ct =>
                {
                    grid = await session.From<SessionConcurrencyEntity>()
                        .QueryMultipleAsync(
                            $"SELECT * FROM session_concurrency", ct);
                });
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            try
            {
                await grid!.ReadAsync<SessionConcurrencyEntity>();
            }
            catch (ObjectDisposedException)
            {
                gridDisposedByTransaction = true;
            }
        }
        finally
        {
            if (grid is not null)
                await grid.DisposeAsync();
        }

        await Assert.That(exception).IsNull();
        await Assert.That(gridDisposedByTransaction).IsTrue();
        await session.ExecuteAsync($"SELECT 1");
    }

    [Test]
    public async Task WithTransactionResult_PreservesFailureWhenGridIsUnreleased()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSqliteSessionAsync();
        await CreateConcurrencyTableAsync(session);
        var actionFailure = new InvalidOperationException("action failed");
        GridReader? grid = null;

        Exception? exception;
        bool gridDisposedByTransaction = false;
        try
        {
            exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await session.WithTransaction<int>(async ct =>
                {
                    grid = await session.From<SessionConcurrencyEntity>()
                        .QueryMultipleAsync(
                            $"SELECT * FROM session_concurrency", ct);
                    throw actionFailure;
                }));
            try
            {
                await grid!.ReadAsync<SessionConcurrencyEntity>();
            }
            catch (ObjectDisposedException)
            {
                gridDisposedByTransaction = true;
            }
        }
        finally
        {
            if (grid is not null)
                await grid.DisposeAsync();
        }

        await Assert.That(exception).IsSameReferenceAs(actionFailure);
        await Assert.That(gridDisposedByTransaction).IsTrue();
        await session.ExecuteAsync($"SELECT 1");
    }

    [Test]
    public async Task TransactionCompleting_RejectsNewOperationAndResource()
    {
        var state = new SessionOperationState();
        object owner = state.EnterTransactionFlow();
        try
        {
            await state.DisposeTransactionResourcesAsync(null);

            await Assert.That(() => state.Enter())
                .Throws<InvalidOperationException>()
                .WithMessage("The active transaction flow is completing.");
            await Assert.That(() => state.RegisterTransactionResource(
                    new TrackingAsyncDisposable()))
                .Throws<InvalidOperationException>()
                .WithMessage("The active transaction flow is completing.");
        }
        finally
        {
            state.ExitTransactionFlow(owner);
        }
    }

    [Test]
    public async Task QueryMultiple_RegistrationFailure_DisposesTransferredResources()
    {
        var state = new SessionOperationState();
        object owner = state.EnterTransactionFlow();
        var connection = new RegistrationFailureConnection();
        try
        {
            var factory = (IRowFactory<SessionConcurrencyEntity>)
                PalORM_Runtime.RowFactories[typeof(SessionConcurrencyEntity)];
            var builder = new QueryBuilder<SessionConcurrencyEntity>(
                new QueryBuilderContext<SessionConcurrencyEntity>(
                    connection,
                    new QueryBuilderServices<SessionConcurrencyEntity>(
                        SqlDialect.Sqlite, factory, [],
                        ConcurrencyProvider.CreateParameter,
                        ConcurrencyProvider.QuoteIdentifier,
                        state, TimeSpan.FromSeconds(30)),
                    "session_concurrency", ["id", "name"],
                    () => connection)).ForRead();

            Task<GridReader> query = builder
                .QueryMultipleAsync($"SELECT 1").AsTask();
            await connection.ReaderStarted;
            Task completing = state
                .DisposeTransactionResourcesAsync(null).AsTask();

            connection.ReleaseReader();
            Exception? exception = await Assert.ThrowsAsync<InvalidOperationException>(
                query);
            await completing;

            await Assert.That(exception.Message).IsEqualTo(
                "The active transaction flow is completing.");
            await Assert.That(connection.Reader.DisposeCount).IsEqualTo(1);
            await Assert.That(connection.CommandDisposeCount).IsEqualTo(1);
            await Assert.That(connection.DisposeCount).IsEqualTo(1);
        }
        finally
        {
            connection.ReleaseReader();
            state.ExitTransactionFlow(owner);
            await connection.DisposeAsync();
        }
    }

    private static async Task CreateConcurrencyTableAsync(
        DataSession<SqliteProvider> session)
    {
        await session.ExecuteAsync(
            $"CREATE TABLE session_concurrency (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)");
    }

    private static async Task<DataSession<SqliteProvider>> CreateSqliteSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
}

internal sealed class RegistrationFailureConnection : DbConnection
{
    private readonly TaskCompletionSource _readerStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseReader = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal RegistrationFailureConnection()
    {
        Reader = new GridBlockingReader(null);
    }

    internal Task ReaderStarted => _readerStarted.Task;
    internal GridBlockingReader Reader { get; }
    internal int CommandDisposeCount { get; set; }
    internal int DisposeCount { get; private set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = "registration";
    public override string Database => "registration";
    public override string DataSource => "registration";
    public override string ServerVersion => "1";
    public override ConnectionState State => ConnectionState.Open;

    internal void ReleaseReader() => _releaseReader.TrySetResult();

    internal async Task<DbDataReader> CreateReaderAsync(
        CancellationToken cancellationToken)
    {
        _readerStarted.TrySetResult();
        await _releaseReader.Task.WaitAsync(cancellationToken);
        return Reader;
    }

    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }
    protected override DbTransaction BeginDbTransaction(
        IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand()
        => new RegistrationFailureCommand(this);
    public override async ValueTask DisposeAsync()
    {
        DisposeCount++;
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class RegistrationFailureCommand(
    RegistrationFailureConnection connection) : DbCommand
{
    private readonly BulkFailureParameterCollection _parameters = new(null);

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; } = connection;
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter()
        => new BulkFailureParameter();
    protected override DbDataReader ExecuteDbDataReader(
        CommandBehavior behavior) => throw new NotSupportedException();
    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
        => connection.CreateReaderAsync(cancellationToken);
    public override async ValueTask DisposeAsync()
    {
        connection.CommandDisposeCount++;
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TrackingAsyncDisposable : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ConcurrencyResources : IAsyncDisposable
{
    internal ConcurrencyResources()
    {
        Connection = new ConcurrencyConnection();
        Session = new DataSession<ConcurrencyProvider>(
            Connection,
            new DbOptions { ConnectionString = "controlled" },
            []);
    }

    internal ConcurrencyConnection Connection { get; }
    internal DataSession<ConcurrencyProvider> Session { get; }

    public ValueTask DisposeAsync()
    {
        Connection.Release();
        Connection.ReleaseTransaction();
        return Session.DisposeAsync();
    }
}

public sealed class ConcurrencyProvider : IDbProvider
{
    public static string Name => "Concurrency";
    public static char ParameterPrefix => '@';
    public static SqlDialect Dialect => SqlDialect.Sqlite;
    public static DbConnection CreateConnection(string connectionString)
        => new ConcurrencyConnection();
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
        => new ConcurrencyConnection();
    public static string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => QuoteIdentifier(identifier);
    public static string GetLimitOffsetClause(int? limit, int? offset) => string.Empty;
    public static bool SupportsReturningClause => false;
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";
    public static string GetParameterPlaceholder(int index) => $"@p{index}";
    public static DbParameter CreateParameter(string name, object? value)
        => new BulkFailureParameter { ParameterName = name, Value = value };
    public static int ConfigureSchemaCommand(
        DbCommand command,
        string tableName,
        string? schema = null)
        => throw new NotSupportedException();
}

internal sealed class ConcurrencyConnection : DbConnection
{
    private readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _transactionStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseTransaction = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Started => _started.Task;
    internal Task TransactionStarted => _transactionStarted.Task;
    internal bool BlockTransactionCreation { get; set; }
    internal Func<Task>? OnOperationStarted { get; set; }
    internal InvalidOperationException? TransactionDisposeFailure { get; set; }
    internal DbTransaction? LastCommandTransaction { get; set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = "controlled";
    public override string Database => "controlled";
    public override string DataSource => "controlled";
    public override string ServerVersion => "1";
    public override ConnectionState State => ConnectionState.Open;

    internal void Release() => _release.TrySetResult();
    internal void ReleaseTransaction() => _releaseTransaction.TrySetResult();

    internal async Task WaitForReleaseAsync(CancellationToken cancellationToken)
    {
        _started.TrySetResult();
        if (OnOperationStarted is not null)
            await OnOperationStarted();
        await _release.Task.WaitAsync(cancellationToken);
    }

    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => new ConcurrencyTransaction(this, TransactionDisposeFailure);
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        if (BlockTransactionCreation)
        {
            _transactionStarted.TrySetResult();
            await _releaseTransaction.Task.WaitAsync(cancellationToken);
        }
        return BeginDbTransaction(isolationLevel);
    }
    protected override DbCommand CreateDbCommand()
        => new ConcurrencyCommand(this);
}

internal sealed class ConcurrencyTransaction(
    DbConnection connection,
    InvalidOperationException? disposeFailure) : DbTransaction
{
    private bool _completed;

    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => _completed ? null : connection;

    public override void Commit() => _completed = true;
    public override void Rollback() => _completed = true;
    public override Task CommitAsync(CancellationToken cancellationToken = default)
    {
        _completed = true;
        return Task.CompletedTask;
    }
    public override Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        _completed = true;
        return Task.CompletedTask;
    }
    public override ValueTask DisposeAsync()
    {
        _completed = true;
        return disposeFailure is null
            ? base.DisposeAsync()
            : ValueTask.FromException(disposeFailure);
    }
}

internal sealed class ConcurrencyCommand(
    ConcurrencyConnection connection) : DbCommand
{
    private readonly BulkFailureParameterCollection _parameters = new(null);

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; } = connection;
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new BulkFailureParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => throw new NotSupportedException();

    public override async Task<int> ExecuteNonQueryAsync(
        CancellationToken cancellationToken)
    {
        connection.LastCommandTransaction = DbTransaction;
        if (CommandText.Contains("controlled", StringComparison.Ordinal))
            await connection.WaitForReleaseAsync(cancellationToken);
        return 0;
    }

    public override async Task<object?> ExecuteScalarAsync(
        CancellationToken cancellationToken)
    {
        if (CommandText.Contains("controlled", StringComparison.Ordinal))
            await connection.WaitForReleaseAsync(cancellationToken);
        return 1L;
    }
}
