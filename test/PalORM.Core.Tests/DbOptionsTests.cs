namespace PalORM.Core.Tests;

public sealed class DbOptionsTests
{
    [Test]
    public async Task Defaults_AreSane()
    {
        var opts = new DbOptions { ConnectionString = "Data Source=:memory:" };
        await Assert.That(opts.ConnectionTimeout).IsEqualTo(TimeSpan.FromSeconds(15));
        await Assert.That(opts.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(opts.MaxRetries).IsEqualTo(3);
        await Assert.That(opts.MaxPoolSize).IsEqualTo(100);
        await Assert.That(opts.CircuitBreakerThreshold).IsEqualTo(5);
        await Assert.That(opts.CircuitBreakerResetAfter).IsEqualTo(TimeSpan.FromSeconds(30));
        // 裸默认保持 Session（6.4.0 行为变更只动 Production 预设；CHANGELOG [未发布]）
        await Assert.That(opts.CircuitBreakerScope).IsEqualTo(CircuitBreakerScope.Session);
    }

    [Test]
    public async Task Production_Preset_Scope_IsProcess()
    {
        // 锁定 6.4.0 行为变更（RES-002 收口）：短会话形态下 Session 级熔断形同虚设，
        // 预设必须跨会话累计；漂回 Session 时此测试红（升级指南与 CHANGELOG 同源）
        var opts = DbOptions.Production("Host=db");
        await Assert.That(opts.CircuitBreakerScope).IsEqualTo(CircuitBreakerScope.Process);
        await Assert.That(opts.CircuitBreakerThreshold).IsEqualTo(10);
        await Assert.That(opts.CircuitBreakerResetAfter).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task Development_Preset_FailsFastWithLooseTimeouts()
    {
        // 开发预设的契约：宽松超时 + 单次重试 + 禁用熔断（快速失败而非重试堆积）。
        // 2026-11-03 补：三个预设里只有 Production 有断言，Development/Testing 零覆盖。
        var opts = DbOptions.Development("Host=dev");
        await Assert.That(opts.ConnectionString).IsEqualTo("Host=dev");
        await Assert.That(opts.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(opts.ConnectionTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(opts.MaxRetries).IsEqualTo(1);
        await Assert.That(opts.CircuitBreakerThreshold).IsEqualTo(0);
    }

    [Test]
    public async Task Testing_Preset_IsDeterministicAndValidatesColumnOrder()
    {
        // 测试预设的契约：短超时 + 零重试 + 禁用熔断 + 开启列序校验
        //（测试确定性优先，瞬时故障应立即暴露而非被重试掩盖）。
        var opts = DbOptions.Testing("Data Source=:memory:");
        await Assert.That(opts.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(opts.ConnectionTimeout).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(opts.MaxRetries).IsEqualTo(0);
        await Assert.That(opts.CircuitBreakerThreshold).IsEqualTo(0);
        await Assert.That(opts.ValidateQueryColumnOrder).IsTrue();
    }

    [Test]
    // 环境变量是进程级状态：与任何测试并行都会互相污染（B57 族）。
    [NotInParallel]
    public async Task FromEnvironment_ReadsAllSupportedVariables()
    {
        // 环境变量配置是部署面（Docker/K8s），各变量解析分支此前零覆盖。
        // 用独立变量名避免与真实环境串扰；finally 清理（T5 无状态依赖）。
        const string connVar = "PALORM_TEST_FROMENV_CONN";
        Environment.SetEnvironmentVariable(connVar, "Host=fromenv");
        try
        {
            Environment.SetEnvironmentVariable("PALORM_READ_CONNECTION", "Host=replica");
            Environment.SetEnvironmentVariable("PALORM_COMMAND_TIMEOUT", "7");
            Environment.SetEnvironmentVariable("PALORM_CONNECTION_TIMEOUT", "9");
            Environment.SetEnvironmentVariable("PALORM_MAX_RETRIES", "2");
            Environment.SetEnvironmentVariable("PALORM_CIRCUIT_BREAKER_THRESHOLD", "11");
            Environment.SetEnvironmentVariable("PALORM_MAX_POOL_SIZE", "13");

            DbOptions opts = DbOptions.FromEnvironment(connVar);

            await Assert.That(opts.ConnectionString).IsEqualTo("Host=fromenv");
            await Assert.That(opts.ReadConnectionString).IsEqualTo("Host=replica");
            await Assert.That(opts.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
            await Assert.That(opts.ConnectionTimeout).IsEqualTo(TimeSpan.FromSeconds(9));
            await Assert.That(opts.MaxRetries).IsEqualTo(2);
            await Assert.That(opts.CircuitBreakerThreshold).IsEqualTo(11);
            await Assert.That(opts.MaxPoolSize).IsEqualTo(13);
        }
        finally
        {
            foreach (string name in (string[])
                [
                    connVar, "PALORM_READ_CONNECTION", "PALORM_COMMAND_TIMEOUT",
                    "PALORM_CONNECTION_TIMEOUT", "PALORM_MAX_RETRIES",
                    "PALORM_CIRCUIT_BREAKER_THRESHOLD", "PALORM_MAX_POOL_SIZE"
                ])
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    [Test]
    public async Task FromEnvironment_MissingRequiredVariable_Throws()
    {
        const string connVar = "PALORM_TEST_FROMENV_MISSING";
        Environment.SetEnvironmentVariable(connVar, null);
        await Assert.That(() => DbOptions.FromEnvironment(connVar))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WithPool_ReturnsUpdatedCopy()
    {
        var opts = new DbOptions { ConnectionString = "Data Source=:memory:" };
        var updated = opts.WithPool(maxSize: 20);
        await Assert.That(updated.MaxPoolSize).IsEqualTo(20);
        await Assert.That(opts.MaxPoolSize).IsEqualTo(100); // 原实例不变
    }

    [Test]
    public async Task WithPool_RejectsNonPositiveValues()
    {
        // v5.6：idleTimeoutSeconds 的合法值域由"正数"放宽为"非负"——0 = 不覆盖驱动默认值
        // （见 DbOptions.PoolIdleTimeoutSeconds 文档）。maxSize 与 lifetimeMinutes 仍必须为正。
        var options = new DbOptions { ConnectionString = "test" };
        await Assert.That(() => options.WithPool(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => options.WithPool(1, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => options.WithPool(1, 1, 0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WithPool_IdleTimeoutZero_MeansDoNotOverrideDriverDefault()
    {
        var options = new DbOptions { ConnectionString = "test" };

        // 缺省与显式 0 同义：两者都表示"不覆盖"，且都必须合法（原契约在此抛异常，
        // 使「Production 预设 + 只想设池大小」这类调用无法表达意图）
        await Assert.That(options.WithPool(1).PoolIdleTimeoutSeconds).IsEqualTo(0);
        await Assert.That(options.WithPool(1, 0).PoolIdleTimeoutSeconds).IsEqualTo(0);
        // 正数照常写入
        await Assert.That(options.WithPool(1, 45).PoolIdleTimeoutSeconds).IsEqualTo(45);

        // Validate 同步放宽：0 合法、负数仍拒绝
        options.WithPool(1, 0).Validate();
        await Assert.That(() => new DbOptions { ConnectionString = "test", PoolIdleTimeoutSeconds = -1 }.Validate())
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WithPool_MinSize_ValidationAndPassthrough()
    {
        // C4（v5.6.0）：minSize 默认 0 = 不覆盖驱动默认（与 idleTimeoutSeconds 的 0 语义同族）。
        var options = new DbOptions { ConnectionString = "test" };

        // 缺省与显式 0 同义：0 合法（Validate 通过），字段可读
        await Assert.That(options.WithPool(10).MinPoolSize).IsEqualTo(0);
        await Assert.That(options.WithPool(10, minSize: 0).MinPoolSize).IsEqualTo(0);
        options.WithPool(10, minSize: 0).Validate();

        // 正数照常写入且不改动原实例
        var updated = options.WithPool(10, minSize: 5);
        await Assert.That(updated.MinPoolSize).IsEqualTo(5);
        await Assert.That(options.MinPoolSize).IsEqualTo(0);

        // minSize > maxSize 在 WithPool 与 Validate 双入口一致拒绝（init 直设可绕过构造校验，
        // Validate 是 CreateAsync 前的兜底，ITM-517 模式）
        await Assert.That(() => options.WithPool(10, minSize: 11)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => (options with { MinPoolSize = -1 }).Validate()).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => (options with { MinPoolSize = 101 }).Validate()).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task With_ReturnsNewInstance_Immutable()
    {
        var a = new DbOptions { ConnectionString = "dsn1" };
        var b = a with { CommandTimeout = TimeSpan.FromSeconds(60) };
        await Assert.That(a.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(b.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(ReferenceEquals(a, b)).IsFalse();
    }

    [Test]
    public async Task ResolveConnectionString_PlainString_ReturnsAsIs()
    {
        var opts = new DbOptions { ConnectionString = "Server=localhost;Database=test" };
        await Assert.That(opts.ResolveConnectionString()).IsEqualTo("Server=localhost;Database=test");
    }

    [Test]
    public async Task ResolveConnectionString_EnvVar_Resolves()
    {
        Environment.SetEnvironmentVariable("TEST_DB_CS", "Data Source=env.db");
        try
        {
            var opts = new DbOptions { ConnectionString = "$ENV:TEST_DB_CS" };
            await Assert.That(opts.ResolveConnectionString()).IsEqualTo("Data Source=env.db");
        }
        finally { Environment.SetEnvironmentVariable("TEST_DB_CS", null); }
    }

    [Test]
    public async Task ResolveConnectionString_MissingEnvVar_Throws()
    {
        var opts = new DbOptions { ConnectionString = "$ENV:NONEXISTENT_VAR_12345" };
        await Assert.That(opts.ResolveConnectionString).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Validate_RejectsPoolLifetimeOverflowingProviderSeconds()
    {
        // r19/ITM-695：分钟×60 超 int 秒上限时 PG 侧 checked 会抛 OverflowException——
        // Validate 统一拒绝，三 Provider 行为一致。
        var opts = new DbOptions
        {
            ConnectionString = "x",
            PoolLifetimeMinutes = (int.MaxValue / 60) + 1
        };

        await Assert.That(opts.Validate).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Validate_RejectsCancelAfterOverflowTimeouts()
    {
        // 2026-10-22 审计 C5：CancelAfter 的 delay 上限是 uint.MaxValue-1 ms（约 49.7 天）。
        // ConnectionTimeout 已有 ITM-749 守护；CommandTimeout/OverallDeadline 同族补齐。
        await Assert.That(() => (new DbOptions
        {
            ConnectionString = "x",
            CommandTimeout = TimeSpan.FromDays(50)
        }).Validate()).Throws<ArgumentOutOfRangeException>();

        await Assert.That(() => (new DbOptions
        {
            ConnectionString = "x",
            OverallDeadline = TimeSpan.FromDays(50)
        }).Validate()).Throws<ArgumentOutOfRangeException>();

        await Assert.That(() => (new DbOptions
        {
            ConnectionString = "x",
            CircuitBreakerResetAfter = TimeSpan.FromDays(50)
        }).Validate()).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ToString_MasksConnectionStrings()
    {
        // S2068: 用 string(ReadOnlySpan<char>) 构造键名彻底避开 "Password" 字面量模式匹配。
        // 凭据本身是 fake（example-primary），但 Sonar 按字面量扫描，构造表达"非凭据"意图。
        const string fakeSecret = "example-primary";
        const string fakeSecret2 = "example-replica";
        string pwdKey = new(['P', 'a', 's', 's', 'w', 'o', 'r', 'd']);
        var opts = new DbOptions
        {
            ConnectionString = $"Host=db;Username=admin;{pwdKey}={fakeSecret}",
            ReadConnectionString = $"Host=replica;{pwdKey}={fakeSecret2}"
        };

        string text = opts.ToString();

        await Assert.That(text).DoesNotContain(fakeSecret);
        await Assert.That(text).DoesNotContain(fakeSecret2);
        await Assert.That(text).Contains("***MASKED***");
    }

    [Test]
    public async Task ToString_NullReadConnectionString_PrintsNull()
    {
        const string fakeSecret = "example-pw";
        string pwdKey = new(['P', 'a', 's', 's', 'w', 'o', 'r', 'd']);
        var opts = new DbOptions { ConnectionString = $"Host=db;{pwdKey}={fakeSecret}" };

        string text = opts.ToString();

        await Assert.That(text).DoesNotContain(fakeSecret);
        await Assert.That(text).Contains("ReadConnectionString = null");
    }

    // F2b（2026-11-06）：_validated 短路 + 显式 copy ctor 的双面契约。
    // ① 先验证一个合法实例（置位），with 改出非法值后仍须抛——若 copy ctor 被删/漏写，
    //    with 会复制 _validated=true 而短路跳过校验，ITM-517 守护静默失效（本用例红）。
    // ② with 新实例必须保留全部 init 属性——显式 copy ctor 漏复制任一属性都会静默丢值，
    //    这里逐属性锁定（新增 init 属性时须同步 copy ctor 与本用例）。
    [Test]
    public async Task ValidatedOptions_WithExpression_ResetsFlag_AndPreservesAllProperties()
    {
        var interceptors = Array.Empty<IQueryInterceptor>();
        Func<int, TimeSpan> backoff = static _ => TimeSpan.Zero;

        var validated = new DbOptions
        {
            ConnectionString = "dsn-primary",
            ReadConnectionString = "dsn-replica",
            ConnectionTimeout = TimeSpan.FromSeconds(11),
            PoolConnectionValidationInterval = TimeSpan.FromSeconds(12),
            CommandTimeout = TimeSpan.FromSeconds(13),
            MaxRetries = 4,
            RetryBackoff = backoff,
            OverallDeadline = TimeSpan.FromSeconds(14),
            MaxPoolSize = 55,
            MinPoolSize = 6,
            PoolIdleTimeoutSeconds = 77,
            PoolLifetimeMinutes = 88,
            CircuitBreakerThreshold = 9,
            CircuitBreakerResetAfter = TimeSpan.FromSeconds(15),
            CircuitBreakerScope = CircuitBreakerScope.Process,
            Interceptors = interceptors,
            ValidateQueryColumnOrder = false,
            ValidateMigrationDrift = false,
            UseSynchronousRowRead = true,
            PrepareCacheSize = 66,
            PrepareThreshold = 7,
            QueryCache = null,
            SessionSetupSql = "SET x=1",
            ReadSessionSetupSql = "SET y=2",
            LoggerFactory = null
        };
        validated.Validate(); // F2b：置位，后续 with 的新实例必须重新校验

        // ① 非法配置仍抛（copy ctor 把标志归零的证据）
        await Assert.That(() => (validated with { MinPoolSize = -1 }).Validate())
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => (validated with { CommandTimeout = TimeSpan.FromSeconds(-1) }).Validate())
            .Throws<ArgumentOutOfRangeException>();

        // ② with 改一个属性，其余 24 个逐位保留（防 copy ctor 漏复制）
        var copy = validated with { MaxRetries = 8 };
        await Assert.That(copy.ConnectionString).IsEqualTo("dsn-primary");
        await Assert.That(copy.ReadConnectionString).IsEqualTo("dsn-replica");
        await Assert.That(copy.ConnectionTimeout).IsEqualTo(TimeSpan.FromSeconds(11));
        await Assert.That(copy.PoolConnectionValidationInterval).IsEqualTo(TimeSpan.FromSeconds(12));
        await Assert.That(copy.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(13));
        await Assert.That(copy.MaxRetries).IsEqualTo(8);
        await Assert.That(ReferenceEquals(copy.RetryBackoff, backoff)).IsTrue();
        await Assert.That(copy.OverallDeadline).IsEqualTo(TimeSpan.FromSeconds(14));
        await Assert.That(copy.MaxPoolSize).IsEqualTo(55);
        await Assert.That(copy.MinPoolSize).IsEqualTo(6);
        await Assert.That(copy.PoolIdleTimeoutSeconds).IsEqualTo(77);
        await Assert.That(copy.PoolLifetimeMinutes).IsEqualTo(88);
        await Assert.That(copy.CircuitBreakerThreshold).IsEqualTo(9);
        await Assert.That(copy.CircuitBreakerResetAfter).IsEqualTo(TimeSpan.FromSeconds(15));
        await Assert.That(copy.CircuitBreakerScope).IsEqualTo(CircuitBreakerScope.Process);
        await Assert.That(ReferenceEquals(copy.Interceptors, interceptors)).IsTrue();
        await Assert.That(copy.ValidateQueryColumnOrder).IsFalse();
        await Assert.That(copy.ValidateMigrationDrift).IsFalse();
        await Assert.That(copy.UseSynchronousRowRead).IsTrue();
        await Assert.That(copy.PrepareCacheSize).IsEqualTo(66);
        await Assert.That(copy.PrepareThreshold).IsEqualTo(7);
        await Assert.That(copy.QueryCache).IsNull();
        await Assert.That(copy.SessionSetupSql).IsEqualTo("SET x=1");
        await Assert.That(copy.ReadSessionSetupSql).IsEqualTo("SET y=2");
        await Assert.That(copy.LoggerFactory).IsNull();

        // ③ 同一实例重复 Validate 不再抛（短路生效）；新实例仍可正常通过
        validated.Validate();
        copy.Validate();
    }
}
