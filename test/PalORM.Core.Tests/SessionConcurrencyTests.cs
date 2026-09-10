using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

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

        await Assert.That(overlapException!.Message)
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
        long[] results = await Task.WhenAll(first, second);
        await Assert.That(results[0]).IsGreaterThan(0L);
        await Assert.That(results[1]).IsGreaterThan(0L);
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
    public async Task WithTransaction_AbandonedEnumerator_TimesOutAndAttemptsRollback()
    {
        // r19/ITM-693：被弃的 QueryAsyncEnumerable 枚举器让租约永不归还——收口等待超时后
        // 仍须尝试回滚（绕过门禁直接 rollback，成败都以结构化 Data 留痕）。
        // 评审 2026-09-02：DisposeWaitTimeout 已实例化——直接设会话实例属性，无需保存/还原全局值。
        IAsyncEnumerator<SessionConcurrencyEntity>? abandoned = null;
        await using var session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        session.DisposeWaitTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            await session.ExecuteAsync(
                $"CREATE TABLE session_concurrency (id INTEGER PRIMARY KEY, name TEXT)");
            await session.ExecuteAsync(
                $"INSERT INTO session_concurrency (id, name) VALUES (1, 'x')");

            Exception? primary = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await session.WithTransaction(async ct =>
                {
                    abandoned = session.QueryAsyncEnumerable<SessionConcurrencyEntity>(
                        $"SELECT * FROM session_concurrency", ct).GetAsyncEnumerator(ct);
                    // 必须 MoveNextAsync 一次才真正进入迭代器体并持有操作租约——
                    // GetAsyncEnumerator 本身不执行体，租约不会被占
                    if (!await abandoned.MoveNextAsync())
                        throw new InvalidOperationException("unexpected empty result");
                    throw new InvalidOperationException("callback failed");
                });
            });

            await Assert.That(primary!.Message).IsEqualTo("callback failed");
            await Assert.That(primary.Data["PalORM.TransactionResourceCleanupException"])
                .IsTypeOf<InvalidOperationException>();
            // 门禁被活动租约拒绝后仍直接尝试回滚：成功则挂 GateException、失败则挂 RollbackException
            await Assert.That(primary.Data.Contains("PalORM.RollbackException")
                || primary.Data.Contains("PalORM.RollbackGateException")).IsTrue();
        }
        finally
        {
            if (abandoned is not null)
                await abandoned.DisposeAsync();
        }
    }

    [Test]
    public async Task WithTransaction_UndisposedGridReader_IsDisposedByTransactionCompletion()
    {
        // 评审 2026-09-02 补测（此前无覆盖的组合）：QueryMultipleAsync 把 GridReader 登记为
        // 事务资源（RegisterTransactionResource）——回调未 await using 释放时，事务收口
        // （DisposeTransactionResourcesAsync）统一释放它，其操作租约随之归还，提交正常
        // 完成、会话继续可用。锁定该安全网行为，防"清理链修一处漏一处"回归。
        await using var session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        await session.ExecuteAsync(
            $"CREATE TABLE session_concurrency (id INTEGER PRIMARY KEY, name TEXT)");
        await session.ExecuteAsync(
            $"INSERT INTO session_concurrency (id, name) VALUES (1, 'x')");

        await session.WithTransaction(async ct =>
        {
            GridReader grid = await session.From<SessionConcurrencyEntity>()
                .QueryMultipleAsync(
                    $"SELECT * FROM session_concurrency; SELECT * FROM session_concurrency", ct);
            List<SessionConcurrencyEntity> first = await grid.ReadAsync<SessionConcurrencyEntity>(ct);
            await Assert.That(first.Count).IsEqualTo(1);
            // 故意不释放 grid——由事务收口兜底释放
        });

        // 事务已提交、门禁已归还——后续操作可用且数据可见
        long count = await session.CountAsync<SessionConcurrencyEntity>();
        await Assert.That(count).IsEqualTo(1L);
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
                    "Complete or dispose the active transaction before disposing DataSession.",
                    StringComparison.Ordinal);
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
#pragma warning disable S5034 // ValueTask 经 .AsTask() 显式转 Task 后多次 await Task 是合法的
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
                .WithMessage("DataSession cannot be disposed from its active operation or transaction scope.", StringComparison.Ordinal);
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
                exception!.Data["PalORM.TransactionCleanupException"])
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
        var sibling = Task.Run(async () =>
        {
            await startSibling.Task;
            await session.ExecuteAsync($"SELECT 1");
        });

        await session.WithTransaction(async _ =>
        {
            startSibling.TrySetResult();
            // TUnit 1.x：ThrowsAsync<T>(Task) 重载移除，需 Func<Task>
            Exception? exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sibling);
            await Assert.That(exception!.Message)
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
                .WithMessage("DataSession does not support nested transactions.", StringComparison.Ordinal);
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

        await Assert.That(exception!.Message)
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
            .WithMessage("DataSession already has an active database operation.", StringComparison.Ordinal);

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
#pragma warning disable S5034 // ValueTask 经 .AsTask() 显式转 Task 后多次 await Task 是合法的
            Task transaction = session.WithTransaction(async ct =>
            {
                callbackEntered.TrySetResult();
                await releaseCallback.Task;
                await session.ExecuteAsync($"SELECT 1", ct);
            }).AsTask();
            await callbackEntered.Task;

            Task dispose = session.DisposeAsync().AsTask();
            // r19/T-P3-01：原 IsCompletedSuccessfully 断言在 await 后恒真——改为锁等待语义：
            // 回调未释放前 Dispose 必须仍在等待（完成后为 false = Dispose 未等回调，提前返回）
            bool disposeBlockedBeforeRelease = !dispose.IsCompleted;
            releaseCallback.TrySetResult();
            await transaction;
            await dispose;

            await Assert.That(disposeBlockedBeforeRelease).IsTrue();
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
        // 不应被 ReferenceEquals(null, _conn) 遮蔽为"transaction must belong to primary connection"消息。
        await using var resources = new ConcurrencyResources();
        await using var tran = await resources.Session.BeginTransactionAsync();
        await tran.DisposeAsync();  // Connection 变 null

        var ex = await Assert.That(() => resources.Session.UseTransaction(tran))
            .Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("disposed transaction");
        // 确保未被遮蔽为"transaction must belong to primary connection"消息
        await Assert.That(ex.Message.Contains("主连接", StringComparison.Ordinal)).IsFalse();
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
                .WithMessage("The active transaction flow is completing.", StringComparison.Ordinal);
            await Assert.That(() => state.RegisterTransactionResource(
                    new TrackingAsyncDisposable()))
                .Throws<InvalidOperationException>()
                .WithMessage("The active transaction flow is completing.", StringComparison.Ordinal);
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
            // v3.1: RowFactories 注册值从 IRowFactory<T> 改为 Func<DbDataReader, T> 委托。
            var factory = (Func<System.Data.Common.DbDataReader, SessionConcurrencyEntity>)
                PalORM_Runtime.RowFactories[typeof(SessionConcurrencyEntity)];
            var builder = new QueryBuilder<SessionConcurrencyEntity>(
                new QueryBuilderContext<SessionConcurrencyEntity>(
                    connection,
                    new QueryBuilderServices<SessionConcurrencyEntity>(
                        SqlDialect.Sqlite, factory, [],
                        ConcurrencyProvider.CreateParameter,
                        ConcurrencyProvider.QuoteIdentifier,
                        state,
                        // v5.4：弹性策略参数——本用例走 QueryMultipleAsync（不接入管线），直通实例即可
                        new ResilienceExecutor(new DbOptions
                        {
                            ConnectionString = "controlled",
                            MaxRetries = 0,
                            CircuitBreakerThreshold = 0
                        }),
                        TimeSpan.FromSeconds(30)),
                    "session_concurrency", ["id", "name"],
                    () => connection)).ForRead();

            Task<GridReader> query = builder
                .QueryMultipleAsync($"SELECT 1").AsTask();
            await connection.ReaderStarted;
            Task completing = state
                .DisposeTransactionResourcesAsync(null).AsTask();

            connection.ReleaseReader();
            // TUnit 1.x：ThrowsAsync<T>(Task) 重载移除，需 Func<Task>
            Exception? exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => query);
            await completing;

            await Assert.That(exception!.Message).IsEqualTo(
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

    [Test]
    public async Task UseTransaction_ExternallyDisposedTransaction_NextOperationFailsLoud()
    {
        // ITM-640：外部事务被外部 Dispose 后，后续命令必须响亮失败而非静默自动提交——
        // 静默降级会让写操作脱离事务隔离而无任何反馈
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        DbTransaction transaction = await session.BeginTransactionAsync();
        session.UseTransaction(transaction);
        await transaction.DisposeAsync();  // 模拟外部释放（Connection 置空）

        Exception? exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.ExecuteAsync($"SELECT 1"));
        await Assert.That(exception!.Message).Contains("disposed externally");
    }

    [Test]
    public async Task RestoreTransaction_NonOwnedPath_PreservesExternalTransactionProtection()
    {
        // ITM-707(r20)：RunInTransactionScopeAsync 的 finally 曾无条件 RestoreTransaction——
        // 非自开事务路径下 previous==transaction，ReferenceEquals 成立会把 _externalTransaction
        // 复位为 false，ITM-640 的"外部事务被外部 Dispose 后响亮失败"保护被静默关闭。
        // 本用例直锁 SessionOperationState 契约：模拟该路径不得复位外部性。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbTransaction transaction = await connection.BeginTransactionAsync();

        var state = new SessionOperationState();
        state.UseTransaction(transaction);
        // 修复前 RunInTransactionScopeAsync 在此调 RestoreTransaction(tx, tx) 复位外部性；
        // 修复后非 owns 路径不还原。断言保护仍在：外部 Dispose 后必须响亮失败。
        await transaction.DisposeAsync();

        Exception? exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = state.GetActiveTransaction();
            return Task.CompletedTask;
        });
        await Assert.That(exception!.Message).Contains("disposed externally");
    }

    [Test]
    public async Task WithTransaction_ActiveTransactionThrows_ReleasesTransactionFlow()
    {
        // ITM-708(r20)：GetActiveTransaction 在外部事务被 Dispose 后会抛——此前它位于 try 之外，
        // 抛出时 ExitTransactionFlow 永不执行，_transactionOwner/_activeTransaction 永不清，
        // 会话后续操作永久失败且 DisposeAsync 挂到超时。修复后事务流必须正常释放。
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        DbTransaction external = await session.BeginTransactionAsync();
        session.UseTransaction(external);
        await external.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.WithTransaction(_ => Task.CompletedTask));

        // 事务流已释放：后续 WithTransaction 可正常自开事务执行（不再报嵌套/已有流）
        await session.WithTransaction(_ => Task.CompletedTask);
        await session.ExecuteAsync($"SELECT 1");
    }

    [Test]
    public async Task UseTransaction_ExternallyDisposedTransaction_NullClearRestoresAutoCommit()
    {
        // ITM-640 逃生门：UseTransaction(null) 显式清场后恢复自动提交语义
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        DbTransaction transaction = await session.BeginTransactionAsync();
        session.UseTransaction(transaction);
        await transaction.DisposeAsync();
        session.UseTransaction(null);

        int affected = await session.ExecuteAsync($"SELECT 1");
        // SQLite 驱动对 SELECT 返回 -1（非 0 行）——断言的是"命令成功执行"而非行数
        await Assert.That(affected).IsEqualTo(-1);
    }

    [Test]
    public async Task UseTransaction_ExternalTx_RejectsWithTransactionAsNested()
    {
        // ITM-640 边界锁定：UseTransaction 设入的活动外部事务与 WithTransaction 互斥
        //（嵌套守卫拒绝）。因此内部事务发布/还原流程永远不会覆盖外部事务——
        // GetActiveTransaction 的失效判定在外部事务整个生命周期内保持有效。
        await using DataSession<SqliteProvider> session = await CreateSqliteSessionAsync();
        DbTransaction external = await session.BeginTransactionAsync();
        session.UseTransaction(external);

        await Assert.That(async () =>
                await session.WithTransaction(_ => Task.CompletedTask))
            .Throws<InvalidOperationException>()
            .WithMessage("DataSession does not support nested transactions.", StringComparison.Ordinal);

        // 外部事务仍然生效：后续命令正常附着执行（未因拒绝而破坏状态）
        int affected = await session.ExecuteAsync($"SELECT 1");
        await Assert.That(affected).IsEqualTo(-1);
        await external.DisposeAsync();
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
    // S4144: 测试 mock 故意让 Rollback 与 Commit 同体——验证 _completed 标记在两种路径下都被设置。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell",
        "S4144:MethodsShouldNotHaveIdenticalImplementations",
        Justification = "测试 mock 故意让 RollbackAsync 与 CommitAsync 同体——验证 _completed 标记在 commit/rollback 两种路径下都被设置。")]
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

#region Test Entities
[Table("session_concurrency")]
internal sealed partial class SessionConcurrencyEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = string.Empty;
}
#endregion
