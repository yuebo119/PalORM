namespace PalORM.Core.Tests;

public sealed class ResilienceTests
{
    [Test]
    public async Task ExecuteAsync_Success_ReturnsResult()
    {
        var opts = new DbOptions { ConnectionString = "dummy", MaxRetries = 2, CircuitBreakerThreshold = 0 };
        var executor = new ResilienceExecutor(opts);
        int result = await executor.ExecuteAsync(async ct =>
        {
            await Task.CompletedTask;
            return 42;
        });
        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    public async Task Constructor_NegativeCommandTimeout_ThrowsAtConstruction()
    {
        // r19/ITM-696：直接构造未 Validate 的 options 时负值会在执行期 CancelAfter(负)
        // 抛 AOORE 且不指向配置——构造期拒绝（与 RetryBackoff 守卫同族）。
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            CommandTimeout = TimeSpan.FromSeconds(-1)
        };

        await Assert.That(() => new ResilienceExecutor(opts)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ExecuteAsync_RetriesOnFailure()
    {
        var opts = new DbOptions { ConnectionString = "dummy", MaxRetries = 2, CircuitBreakerThreshold = 0 };
        var executor = new ResilienceExecutor(opts, static _ => true);
        int attempts = 0;
        int result = await executor.ExecuteAsync(_ =>
        {
            attempts++;
            if (attempts < 2) throw new InvalidOperationException("transient");
            return Task.FromResult(99);
        });
        await Assert.That(result).IsEqualTo(99);
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteAsync_DeterministicFailure_DoesNotRetry()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 3,
            RetryBackoff = _ => TimeSpan.Zero,
            CircuitBreakerThreshold = 0
        };
        var executor = new ResilienceExecutor(opts, static _ => false);
        int attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await executor.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new InvalidOperationException("deterministic");
            });
        });

        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_FinalFailure_AttemptsMaxRetriesPlusOneTimes()
    {
        for (int maxRetries = 0; maxRetries <= 2; maxRetries++)
        {
            var opts = new DbOptions
            {
                ConnectionString = "dummy",
                MaxRetries = maxRetries,
                RetryBackoff = _ => TimeSpan.Zero,
                CircuitBreakerThreshold = 0
            };
            var executor = new ResilienceExecutor(opts, static _ => true);
            int attempts = 0;

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await executor.ExecuteAsync<int>(_ =>
                {
                    attempts++;
                    throw new InvalidOperationException("fail");
                });
            });

            await Assert.That(attempts).IsEqualTo(maxRetries + 1);
        }
    }

    [Test]
    public async Task ExecuteAsync_NegativeRetryBackoff_ReportsBackoffConfig()
    {
        // ITM-605：自定义 RetryBackoff 返回负值时，ResilienceExecutor 包装守卫应抛
        // InvalidOperationException 指向 RetryBackoff 配置（带 attempt=N），
        // 不应让 Task.Delay 抛 AOORE("delay") 不指向配置。
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 1,
            RetryBackoff = _ => TimeSpan.FromSeconds(-1),
            CircuitBreakerThreshold = 0
        };
        var executor = new ResilienceExecutor(opts, static _ => true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await executor.ExecuteAsync<int>(_ => throw new InvalidOperationException("trigger retry"));
        });
        await Assert.That(ex!.Message).Contains("RetryBackoff");
        await Assert.That(ex.Message).Contains("attempt=0");
    }

    [Test]
    public async Task ExecuteAsync_CircuitBreaker_CountsEachFinalFailureOnce()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 2,
            RetryBackoff = _ => TimeSpan.Zero,
            CircuitBreakerThreshold = 2,
            CircuitBreakerResetAfter = TimeSpan.FromMinutes(1)
        };
        var executor = new ResilienceExecutor(opts, static _ => true);
        int attempts = 0;

        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await executor.ExecuteAsync<int>(_ =>
                {
                    attempts++;
                    throw new InvalidOperationException("fail");
                });
            });
        }

        await Assert.That(attempts).IsEqualTo(6);
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
        {
            await executor.ExecuteAsync<int>(_ => Task.FromResult(1));
        });
    }

    [Test]
    public async Task ExecuteAsync_Success_ClearsConsecutiveFinalFailures()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 0,
            CircuitBreakerThreshold = 2,
            CircuitBreakerResetAfter = TimeSpan.FromMinutes(1)
        };
        var executor = new ResilienceExecutor(opts);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail")));
        int result = await executor.ExecuteAsync(_ => Task.FromResult(42));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail")));

        await Assert.That(result).IsEqualTo(42);
        await Assert.That(await executor.ExecuteAsync(_ => Task.FromResult(99))).IsEqualTo(99);
    }

    [Test]
    public async Task ExecuteAsync_ExternalCancellation_DoesNotOpenCircuit()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 0,
            CircuitBreakerThreshold = 1,
            CircuitBreakerResetAfter = TimeSpan.FromMinutes(1)
        };
        var executor = new ResilienceExecutor(opts);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync<int>(ct => Task.FromCanceled<int>(ct), cancellation.Token));

        await Assert.That(await executor.ExecuteAsync(_ => Task.FromResult(42))).IsEqualTo(42);
    }

    [Test]
    public async Task ExecuteAsync_InternalTimeout_CountsAsFinalFailure()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            CommandTimeout = TimeSpan.FromMilliseconds(10),
            MaxRetries = 0,
            CircuitBreakerThreshold = 1,
            CircuitBreakerResetAfter = TimeSpan.FromMinutes(1)
        };
        var executor = new ResilienceExecutor(opts);

        // ITM-131 后：重试耗尽的内部超时包装为 TimeoutException（不再是裸 OCE）
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await executor.ExecuteAsync<int>(async ct =>
            {
                await Task.Delay(5000, ct);
                return 1;
            });
        });

        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
            await executor.ExecuteAsync(_ => Task.FromResult(1)));
    }

    [Test]
    public async Task ExecuteAsync_HalfOpen_AllowsSingleProbe()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 0,
            CircuitBreakerThreshold = 1,
            CircuitBreakerResetAfter = TimeSpan.Zero
        };
        var executor = new ResilienceExecutor(opts, static _ => true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail")));

        Task<int> probe = executor.ExecuteAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return 42;
        }).AsTask();
        await entered.Task;

        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
            await executor.ExecuteAsync(_ => Task.FromResult(1)));

        release.SetResult();
        await Assert.That(await probe).IsEqualTo(42);
    }

    [Test]
    public async Task ExecuteAsync_InFlightSuccess_DoesNotCloseNewlyOpenedCircuit()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 0,
            CircuitBreakerThreshold = 1,
            CircuitBreakerResetAfter = TimeSpan.FromMinutes(1)
        };
        var executor = new ResilienceExecutor(opts, static _ => true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> inFlightSuccess = executor.ExecuteAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return 42;
        }).AsTask();
        await entered.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail")));
        release.SetResult();
        await Assert.That(await inFlightSuccess).IsEqualTo(42);

        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
            await executor.ExecuteAsync(_ => Task.FromResult(1)));
    }

    [Test]
    public async Task DefaultBackoff_LargeAttempt_IsBoundedAndNonNegative()
    {
        TimeSpan delay = ResilienceExecutor.GetDefaultBackoff(100);

        await Assert.That(delay).IsGreaterThanOrEqualTo(TimeSpan.Zero);
        await Assert.That(delay).IsLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task GeneratedId_NormalizesSignedAndUnsignedProviderValues()
    {
        await Assert.That(DataSession<PalORM.Sqlite.SqliteProvider>.NormalizeGeneratedId(42L)).IsEqualTo(42L);
        await Assert.That(DataSession<PalORM.Sqlite.SqliteProvider>.NormalizeGeneratedId(42UL)).IsEqualTo(42L);
        await Assert.That(DataSession<PalORM.Sqlite.SqliteProvider>.NormalizeGeneratedId(42U)).IsEqualTo(42L);
        await Assert.That(DataSession<PalORM.Sqlite.SqliteProvider>.NormalizeGeneratedId(0UL)).IsNull();
        await Assert.That(() => DataSession<PalORM.Sqlite.SqliteProvider>.NormalizeGeneratedId(ulong.MaxValue))
            .Throws<OverflowException>();
    }

    [Test]
    public async Task MySqlUpsert_UsesLastInsertIdOnlyForGeneratedNumericKey()
    {
        string assignedKeySql = DataSession<PalORM.MySql.MySqlProvider>.BuildMySqlUpsertSql(
            "order", "Id", ["Id", "select"], 2, hasGeneratedKey: false);
        string generatedKeySql = DataSession<PalORM.MySql.MySqlProvider>.BuildMySqlUpsertSql(
            "order", "Id", ["Id", "select"], 2, hasGeneratedKey: true);
        string keyOnlySql = DataSession<PalORM.MySql.MySqlProvider>.BuildMySqlUpsertSql(
            "order", "Id", ["Id"], 1, hasGeneratedKey: false);

        await Assert.That(assignedKeySql).IsEqualTo(
            "INSERT INTO `order` (`Id`, `select`) VALUES (@p0, @p1) " +
            "ON DUPLICATE KEY UPDATE `select` = VALUES(`select`)");
        await Assert.That(assignedKeySql).DoesNotContain("LAST_INSERT_ID");
        await Assert.That(generatedKeySql).Contains(
            "`Id` = LAST_INSERT_ID(`Id`); SELECT LAST_INSERT_ID()");
        await Assert.That(keyOnlySql).IsEqualTo(
            "INSERT INTO `order` (`Id`) VALUES (@p0) " +
            "ON DUPLICATE KEY UPDATE `Id` = VALUES(`Id`)");
    }

    [Test]
    public async Task DataSession_ResilienceState_PersistsAcrossCalls()
    {
        var opts = new DbOptions
        {
            ConnectionString = "Data Source=:memory:",
            MaxRetries = 0,
            CircuitBreakerThreshold = 2,
            CircuitBreakerResetAfter = TimeSpan.FromMinutes(1)
        };
        await using var session = await DataSession<PalORM.Sqlite.SqliteProvider>.CreateAsync(opts);

        // ITM-506：SqliteProvider.IsTransient 只认 SQLITE_BUSY/LOCKED，InvalidOperationException
        // 是确定性异常 → 不计入熔断。连续失败后熔断仍关闭，弹性状态跨调用持续（_resilience 复用）。
        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.ExecuteWithResilience<int>(_ => throw new InvalidOperationException("deterministic")));
        }

        // 确定性失败不熔断——后续正常操作照常返回（证明弹性状态跨调用持续且未误熔断）
        await Assert.That(await session.ExecuteWithResilience(_ => Task.FromResult(42))).IsEqualTo(42);
    }

    /// <summary>瞬时 DbException 测试替身——IsTransient=true 使弹性执行器判为可熔断故障。</summary>
    private sealed class TransientTestException : System.Data.Common.DbException
    {
        public TransientTestException() : base("transient") { }
        public TransientTestException(string message) : base(message) { }
        public TransientTestException(string message, Exception innerException) : base(message, innerException) { }
        public override bool IsTransient => true;
    }

    // ITM-506：确定性异常（唯一约束冲突、语法错误等）不得熔断整个执行器。
    [Test]
    public async Task ExecuteAsync_DeterministicException_DoesNotOpenCircuit()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 0,
            CircuitBreakerThreshold = 1,
            CircuitBreakerResetAfter = TimeSpan.FromMinutes(1)
        };
        // 默认瞬时判定 = DbException.IsTransient；InvalidOperationException 非瞬时
        var executor = new ResilienceExecutor(opts);

        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await executor.ExecuteAsync<int>(_ => throw new InvalidOperationException("deterministic")));
        }
        // 5 次确定性失败后熔断仍关闭——后续正常操作照常执行，不抛 CircuitBreakerOpenException
        await Assert.That(await executor.ExecuteAsync(_ => Task.FromResult(42))).IsEqualTo(42);
    }

    // ITM-507：熔断打开期间，在飞旧失败不得反复顺延恢复时间点。
    [Test]
    public async Task ExecuteAsync_InFlightFailuresAfterOpen_DoNotExtendWindow()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 0,
            CircuitBreakerThreshold = 1,
            CircuitBreakerResetAfter = TimeSpan.Zero
        };
        var executor = new ResilienceExecutor(opts, static _ => true);

        // 首次瞬时失败即打开熔断（阈值 1）
        await Assert.ThrowsAsync<TransientTestException>(async () =>
            await executor.ExecuteAsync<int>(_ => throw new TransientTestException()));
        // 再多次在飞失败——resetAfter=Zero，若被顺延则半开探针永远进不去
        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TransientTestException>(async () =>
                await executor.ExecuteAsync<int>(_ => throw new TransientTestException()));
        }
        // resetAfter=Zero 且未被顺延 → 半开探针可进入并成功关闭熔断
        await Assert.That(await executor.ExecuteAsync(_ => Task.FromResult(7))).IsEqualTo(7);
    }

    [Test]
    public async Task ExecuteAsync_InternalTimeoutExhausted_ThrowsTimeoutException_NotBareOce()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 1,
            RetryBackoff = _ => TimeSpan.Zero,
            CommandTimeout = TimeSpan.FromMilliseconds(30),
            CircuitBreakerThreshold = 0
        };
        var executor = new ResilienceExecutor(opts);

        // 操作只响应内部超时 token，调用方 ct 未取消 → 耗尽后应为 TimeoutException 而非裸 OCE
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await executor.ExecuteAsync<int>(async innerCt =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, innerCt);
                return 0;
            }));
        await Assert.That(ex!.InnerException).IsTypeOf<TaskCanceledException>();
    }

    [Test]
    public async Task ExecuteAsync_CallerCancellation_StillThrowsOce()
    {
        var opts = new DbOptions { ConnectionString = "dummy", MaxRetries = 3, CircuitBreakerThreshold = 0 };
        var executor = new ResilienceExecutor(opts);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync<int>(async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            }, cts.Token));
    }

    [Test]
    public async Task HalfOpen_StaleOperationFailure_DoesNotReleaseActiveProbe()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            MaxRetries = 0,
            RetryBackoff = _ => TimeSpan.Zero,
            CircuitBreakerThreshold = 1,
            CircuitBreakerResetAfter = TimeSpan.Zero
        };
        var executor = new ResilienceExecutor(opts, static _ => true);

        // 旧操作 A：熔断关闭时进入，挂起待命
        var staleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable S5034 // ValueTask 经 .AsTask() 显式转 Task 后多次 await Task 是合法的
        Task<int> stale = executor.ExecuteAsync<int>(async _ =>
        {
            staleStarted.SetResult();
            await staleGate.Task;
            throw new InvalidOperationException("stale-failure");
        }).AsTask();
        await staleStarted.Task;

        // 操作 B 失败 → 打开熔断（threshold=1）；resetAfter=0 → 立即半开
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync<int>(_ => throw new InvalidOperationException("open")));

        // 探针 P 进入半开态并挂起
        var probeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> probe = executor.ExecuteAsync<int>(async _ =>
        {
            probeStarted.SetResult();
            await probeGate.Task;
            return 1;
        }).AsTask();
        await probeStarted.Task;

        // 旧操作 A 此刻最终失败——修复前会无条件清 _halfOpenProbeActive
        staleGate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stale);

        // 探针 P 仍在飞：不得放行第二个并发探针（半开单探针不变式）
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
            await executor.ExecuteAsync<int>(_ => Task.FromResult(2)));

        probeGate.SetResult();
        int result = await probe;
        await Assert.That(result).IsEqualTo(1);
    }
}
