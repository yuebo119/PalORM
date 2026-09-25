using System.Globalization;

namespace PalORM.Core.Tests;

// ─── Phase 1: Provider 单元测试 ─────────────────────

public sealed class ProviderTests
{
    private sealed class ProviderBatchEntity;

    [Test]
    public async Task SqliteProvider_KeyMembers_BehaveAsExpected()
    {
        await Assert.That(PalORM.Sqlite.SqliteProvider.Name).IsEqualTo("SQLite");
        await Assert.That(PalORM.Sqlite.SqliteProvider.SupportsReturningClause).IsTrue();
        await Assert.That(PalORM.Sqlite.SqliteProvider.QuoteIdentifier("test")).IsEqualTo("\"test\"");
        // ITM-403: 仅扩展码 2067(UNIQUE)/1555(PK) 判定为唯一冲突——真库触发见
        // Integration.Tests SqliteErrorCodeMatrixTests（手工构造异常无扩展码，此处只验负例）
        await Assert.That(PalORM.Sqlite.SqliteProvider.IsUniqueViolation(
            new Microsoft.Data.Sqlite.SqliteException("constraint", 19))).IsFalse();
        await Assert.That(PalORM.Sqlite.SqliteProvider.IsUniqueViolation(
            new Microsoft.Data.Sqlite.SqliteException("constraint", 19, 2067))).IsTrue();
        await Assert.That(PalORM.Sqlite.SqliteProvider.IsUniqueViolation(
            new Microsoft.Data.Sqlite.SqliteException("constraint", 19, 1555))).IsTrue();
        await Assert.That(PalORM.Sqlite.SqliteProvider.IsUniqueViolation(
            new Microsoft.Data.Sqlite.SqliteException("not null", 19, 1299))).IsFalse();
        await Assert.That(PalORM.Sqlite.SqliteProvider.IsUniqueViolation(
            new Microsoft.Data.Sqlite.SqliteException("busy", 5))).IsFalse();
    }

    [Test]
    public async Task SqliteProvider_CreateParameter_Works()
    {
        var p = PalORM.Sqlite.SqliteProvider.CreateParameter("@p0", "hello");
        await Assert.That(p.ParameterName).IsEqualTo("@p0");
        await Assert.That(p.Value).IsEqualTo("hello");
    }

    [Test]
    public async Task SqliteProvider_OnlyBusyAndLockedAreTransient()
    {
        await Assert.That(PalORM.Sqlite.SqliteProvider.IsTransient(
            new Microsoft.Data.Sqlite.SqliteException("busy", 5))).IsTrue();
        await Assert.That(PalORM.Sqlite.SqliteProvider.IsTransient(
            new Microsoft.Data.Sqlite.SqliteException("locked", 6))).IsTrue();
        await Assert.That(PalORM.Sqlite.SqliteProvider.IsTransient(
            new Microsoft.Data.Sqlite.SqliteException("constraint", 19))).IsFalse();
    }

    [Test]
    public async Task NamingConvention_SnakeCase_Works()
    {
        var opts = new DbOptions { ConnectionString = "x", NamingConvention = NamingConvention.SnakeCase };
        await Assert.That(opts.ApplyNaming("OrderId")).IsEqualTo("order_id");
        await Assert.That(opts.ApplyNaming("CreatedAt")).IsEqualTo("created_at");
        await Assert.That(opts.ApplyNaming("Id")).IsEqualTo("id");
    }

    [Test]
    public async Task IValueConverter_Interface_Exists()
    {
        // 验证 IValueConverter<T,U> 接口已定义并可被实现
        var converter = new TestConverter();
        await Assert.That(converter.FromProvider("123")).IsEqualTo(123);
        await Assert.That(converter.ToProvider(456)).IsEqualTo("456");
    }

    private sealed class TestConverter : IValueConverter<int, string>
    {
        public int FromProvider(string value) => int.Parse(value, CultureInfo.InvariantCulture);
        public string ToProvider(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    [Test]
    public async Task PostgreSqlProvider_KeyMembers_Compiled()
    {
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.Name).IsEqualTo("PostgreSql");
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.SupportsReturningClause).IsTrue();
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.QuoteIdentifier("t")).IsEqualTo("\"t\"");
    }

    [Test]
    public async Task MySqlProvider_KeyMembers_Compiled()
    {
        await Assert.That(PalORM.MySql.MySqlProvider.Name).IsEqualTo("MySql");
        await Assert.That(PalORM.MySql.MySqlProvider.SupportsReturningClause).IsFalse();
        await Assert.That(PalORM.MySql.MySqlProvider.QuoteIdentifier("t")).IsEqualTo("`t`");
    }

    [Test]
    public async Task Providers_QuoteInternalDelimitersAndQualifiedNames()
    {
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.QuoteIdentifier("a\"b")).IsEqualTo("\"a\"\"b\"");
        await Assert.That(PalORM.Sqlite.SqliteProvider.QuoteIdentifier("a\"b")).IsEqualTo("\"a\"\"b\"");
        await Assert.That(PalORM.MySql.MySqlProvider.QuoteIdentifier("a`b")).IsEqualTo("`a``b`");
        await Assert.That(PalORM.PostgreSql.PostgreSqlProvider.QuoteQualifiedIdentifier("app", "users"))
            .IsEqualTo("\"app\".\"users\"");
    }

    [Test]
    public async Task ProviderConnectionFactories_ApplyPoolOptions()
    {
        var options = new DbOptions { ConnectionString = "x" }.WithPool(23, 17, 5);
        await using var postgres = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test", options);
        await using var mysql = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Database=test", options);

        await Assert.That(postgres.ConnectionString).Contains("Maximum Pool Size=23");
        await Assert.That(postgres.ConnectionString).Contains("Connection Idle Lifetime=17");
        await Assert.That(postgres.ConnectionString).Contains("Connection Lifetime=300");
        await Assert.That(mysql.ConnectionString).Contains("Maximum Pool Size=23");
        await Assert.That(mysql.ConnectionString).Contains("Connection Idle Timeout=17");
        await Assert.That(mysql.ConnectionString).Contains("Connection Lifetime=300");
    }

    [Test]
    public async Task ProviderConnectionFactories_ApplyMinPoolSizeOnlyWhenConfigured()
    {
        // C4（v5.6.0）契约：MinPoolSize 默认 0 = 不覆盖驱动默认（键缺席即保留）；显式 >0 才透传。
        // 语义为空闲修剪保留下限（Npgsql ConnectionIdleLifetime / MySqlConnector
        // ConnectionIdleTimeout 到期时至少保留这么多条），非启动预热——启动预热是
        // DataSession.PreWarmAsync 的职责，与本参数正交。
        var defaults = new DbOptions { ConnectionString = "x" };
        await using var postgresDefault = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test", defaults);
        await using var mysqlDefault = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Database=test", defaults);
        await Assert.That(postgresDefault.ConnectionString).DoesNotContain("Minimum Pool Size");
        await Assert.That(mysqlDefault.ConnectionString).DoesNotContain("Minimum Pool Size");

        var options = new DbOptions { ConnectionString = "x" }.WithPool(23, minSize: 8);
        await using var postgres = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test", options);
        await using var mysql = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Database=test", options);
        await Assert.That(postgres.ConnectionString).Contains("Minimum Pool Size=8");
        await Assert.That(mysql.ConnectionString).Contains("Minimum Pool Size=8");

        // 「仅默认时覆盖」策略：用户连接串已显式给出下限时（此处 3），DbOptions 值不改写
        await using var explicitUser = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test;Minimum Pool Size=3", options);
        await Assert.That(explicitUser.ConnectionString).Contains("Minimum Pool Size=3");
    }

    [Test]
    public async Task ProviderConnectionFactories_LeaveDriverIdleTimeoutAtDriverDefault_WhenNotConfigured()
    {
        // v5.6 契约：PoolIdleTimeoutSeconds 默认 0 = **不覆盖驱动默认值**（Npgsql 300 /
        // MySqlConnector 180）。观察方式是连接串里该键的有无——builder 只序列化显式设过的键，
        // 键缺席即"驱动默认原样保留"；一旦出现就说明被 PalORM 改写。
        // 为什么值得一条用例：实测被改写时的代价是间隔超过该值后的首次查询多付 13.2 ms 重连
        // （跨网段 SELECT 1 池内 0.300 ms vs 新建连接 13.523 ms），而这在应用侧表现为
        // "PalORM 慢"，不会有人想到去查池空闲超时。
        var defaults = new DbOptions { ConnectionString = "x" };
        await using var postgres = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test", defaults);
        await using var mysql = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Database=test", defaults);

        await Assert.That(postgres.ConnectionString).DoesNotContain("Connection Idle Lifetime");
        await Assert.That(mysql.ConnectionString).DoesNotContain("Connection Idle Timeout");

        // WithPool 只给池大小（Production 预设的形态）同样不覆盖空闲超时
        var sized = new DbOptions { ConnectionString = "x" }.WithPool(maxSize: 100);
        await using var postgresSized = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test", sized);
        await using var mysqlSized = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Database=test", sized);
        await Assert.That(postgresSized.ConnectionString).DoesNotContain("Connection Idle Lifetime");
        await Assert.That(mysqlSized.ConnectionString).DoesNotContain("Connection Idle Timeout");

        // 显式给出正数才覆盖
        var explicitIdle = new DbOptions { ConnectionString = "x" }.WithPool(23, 17);
        await using var postgresExplicit = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test", explicitIdle);
        await using var mysqlExplicit = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Database=test", explicitIdle);
        await Assert.That(postgresExplicit.ConnectionString).Contains("Connection Idle Lifetime=17");
        await Assert.That(mysqlExplicit.ConnectionString).Contains("Connection Idle Timeout=17");
    }

    [Test]
    public async Task SqliteConnectionFactory_IgnoresUnsupportedPoolOptions()
    {
        // v5.6 契约变更：原实现抛 NotSupportedException，但 DbOptions.Production(...) 内部
        // 就调用 WithPool——「Production 预设 + SQLite」因此在构造期必然失败，走预设或走
        // PALORM_MAX_POOL_SIZE 的 SQLite 部署全都装不起来。改为忽略池参数（SQLite 无服务端
        // 池可调这三个旋钮）。本用例与 SqlitePoolParameterTests 共同锁定新契约。
        var options = new DbOptions { ConnectionString = "Data Source=:memory:" }.WithPool(10);

        using var connection = PalORM.Sqlite.SqliteProvider.CreateConnection(options.ConnectionString, options);
        await Assert.That(connection).IsNotNull();
        await Assert.That(connection.GetType().Name).IsEqualTo("SqliteConnection");
    }

    [Test]
    public async Task PostgreSqlConnectionFactory_AppliesConnectionTuning()
    {
        // 评审 2026-09-02 补测：v5.0 连接串自动调优此前仅由 Provider 注释与 README 承载、
        // 无回归断言——驱动升级或重构时调优值可静默漂移。6 项均为"用户未显式设置才覆盖"。
        await using var postgres = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test", new DbOptions { ConnectionString = "Host=localhost;Database=test" });
        string cs = postgres.ConnectionString;
        await Assert.That(cs).Contains("Max Auto Prepare=100");
        await Assert.That(cs).Contains("Auto Prepare Min Usages=2");
        // 驱动 builder 规范化布尔为首字母大写（True/False）
        await Assert.That(cs).Contains("No Reset On Close=True");
        await Assert.That(cs).Contains("Read Buffer Size=16384");
        await Assert.That(cs).Contains("Write Buffer Size=16384");
        await Assert.That(cs).Contains("Enlist=False");
    }

    [Test]
    public async Task ProviderConnectionFactories_HonorExplicitlySetDriverDefaults()
    {
        // PROV-001（2026-09-23）：判据从"值 == 驱动默认"改为"连接串未显式给出该键 且 值 == 驱动默认"——
        // 显式设成默认值的用户意图必须保留（原实现会静默改写：调优/安全加固落空）。
        // 注意 Npgsql 10.0.3 的 ContainsKey 对未设置键同样返回 true（探针实测），故判据用 Keys 集合。
        // 断言用"不含被改写后的值"而非"含默认值"——驱动渲染连接串时会省略默认值键，正向断言会假失败。
        var options = new DbOptions { ConnectionString = "x" }.WithPool(23, 17, 5);

        await using var postgres = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Maximum Pool Size=100;Max Auto Prepare=0;Enlist=True", options);
        string pg = postgres.ConnectionString;
        await Assert.That(pg).DoesNotContain("Maximum Pool Size=23");   // 显式 100 不被 WithPool(23) 改写
        await Assert.That(pg).DoesNotContain("Max Auto Prepare=100");   // 显式 0（关预编译）不被改写
        await Assert.That(pg).DoesNotContain("Enlist=False");           // 显式 True 不被改写（环境事务场景）

        await using var mysql = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Maximum Pool Size=100;Auto Enlist=True;Cancellation Timeout=2", options);
        string my = mysql.ConnectionString;
        await Assert.That(my).DoesNotContain("Maximum Pool Size=23");
        await Assert.That(my).DoesNotContain("Auto Enlist=False");
        await Assert.That(my).DoesNotContain("Cancellation Timeout=5");
    }

    [Test]
    public async Task MySqlConnectionFactory_AppliesConnectionTuning()
    {
        // 同上（MySQL 5 项）
        await using var mysql = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Database=test", new DbOptions { ConnectionString = "Server=localhost;Database=test" });
        string cs = mysql.ConnectionString;
        await Assert.That(cs).Contains("Auto Enlist=False");
        await Assert.That(cs).Contains("Connection Reset=False");
        await Assert.That(cs).Contains("Cancellation Timeout=5");
        await Assert.That(cs).Contains("Allow Load Local Infile=True");
        await Assert.That(cs).Contains("Server Redirection Mode=Preferred");
    }

    [Test]
    public async Task SqliteConnection_Pragmas_AppliedForFileDatabase()
    {
        // SQLite 调优 PRAGMA（文件库分支，v5.0 阶段 3.3/3.5 + v7.2.1 收窄口径）：
        // :memory: 库仅跑 foreign_keys+cache_size（无文件 I/O 语义项被收窄），
        // 故文件相关项必须用文件库验证。
        string dbPath = Path.Combine(Path.GetTempPath(), $"palorm-pragma-{Guid.NewGuid():N}.db");
        try
        {
            await using var session = await PalORM.DataSession<PalORM.Sqlite.SqliteProvider>.CreateAsync(
                new DbOptions { ConnectionString = $"Data Source={dbPath}" });
            await Assert.That(await session.ScalarAsync<long>($"PRAGMA foreign_keys")).IsEqualTo(1);
            await Assert.That(await session.ScalarAsync<string>($"PRAGMA journal_mode")).IsEqualTo("wal");
            await Assert.That(await session.ScalarAsync<long>($"PRAGMA synchronous")).IsEqualTo(1);        // NORMAL
            await Assert.That(await session.ScalarAsync<long>($"PRAGMA cache_size")).IsEqualTo(-65536);   // 64MB
            await Assert.That(await session.ScalarAsync<long>($"PRAGMA temp_store")).IsEqualTo(2);        // MEMORY
            await Assert.That(await session.ScalarAsync<long>($"PRAGMA wal_autocheckpoint")).IsEqualTo(1000);
            // 2026-09-25 极致优化批次新增：busy_timeout（并发 BUSY 引擎内等待）、
            // journal_size_limit（WAL 防 64MB 无界膨胀）、analysis_limit（optimize/ANALYZE 采样成本约束）
            await Assert.That(await session.ScalarAsync<long>($"PRAGMA busy_timeout")).IsEqualTo(5000);
            await Assert.That(await session.ScalarAsync<long>($"PRAGMA journal_size_limit")).IsEqualTo(67108864);
            await Assert.That(await session.ScalarAsync<long>($"PRAGMA analysis_limit")).IsEqualTo(400);
        }
        finally
        {
            foreach (string path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                // 尽力清理临时库文件；被占用/缺失时忽略（临时目录，非被测资产）
                try { File.Delete(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _ = exception;
                }
            }
        }
    }

    [Test]
    public async Task SqliteConnection_Pragmas_InMemoryBranch_StaysNarrow()
    {
        // v7.2.1 收窄口径锁定：:memory: 库只跑 foreign_keys + busy_timeout + cache_size + analysis_limit，
        // journal_mode 保持默认 memory（非 WAL）——防"宽窄分支合一"漂移。
        // （2026-09-25：busy_timeout/analysis_limit 属纯连接态设置，宽窄两分支共有。）
        await using var session = await PalORM.DataSession<PalORM.Sqlite.SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        await Assert.That(await session.ScalarAsync<long>($"PRAGMA foreign_keys")).IsEqualTo(1);
        await Assert.That(await session.ScalarAsync<long>($"PRAGMA cache_size")).IsEqualTo(-65536);
        await Assert.That(await session.ScalarAsync<string>($"PRAGMA journal_mode")).IsEqualTo("memory");
        await Assert.That(await session.ScalarAsync<long>($"PRAGMA busy_timeout")).IsEqualTo(5000);
        await Assert.That(await session.ScalarAsync<long>($"PRAGMA analysis_limit")).IsEqualTo(400);
    }

    [Test]
    public async Task Providers_RejectInvalidBatchSizeBeforeDatabaseAccess()
    {
        await using var postgres = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test", new DbOptions { ConnectionString = "Host=localhost;Database=test" });
        await using var mysql = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Database=test", new DbOptions { ConnectionString = "Server=localhost;Database=test" });
        await using var sqlite = PalORM.Sqlite.SqliteProvider.CreateConnection(
            "Data Source=:memory:", new DbOptions { ConnectionString = "Data Source=:memory:" });

        await Assert.That(async () => await PalORM.PostgreSql.PostgreSqlProvider.BulkInsertAsync(
            postgres, null, Array.Empty<ProviderBatchEntity>(), 0, 30, default)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await PalORM.MySql.MySqlProvider.BulkInsertAsync(
            mysql, null, Array.Empty<ProviderBatchEntity>(), 0, 30, default)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await PalORM.Sqlite.SqliteProvider.BulkInsertAsync(
            sqlite, null, Array.Empty<ProviderBatchEntity>(), 0, 30, default)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SchemaCommands_UseProviderSpecificColumnOrdinals()
    {
        await using var postgres = PalORM.PostgreSql.PostgreSqlProvider.CreateConnection(
            "Host=localhost;Database=test", new DbOptions { ConnectionString = "Host=localhost;Database=test" });
        await using var postgresCommand = postgres.CreateCommand();
        int postgresOrdinal = PalORM.PostgreSql.PostgreSqlProvider.ConfigureSchemaCommand(
            postgresCommand, "users", "app");

        await using var mysql = PalORM.MySql.MySqlProvider.CreateConnection(
            "Server=localhost;Database=test", new DbOptions { ConnectionString = "Server=localhost;Database=test" });
        await using var mysqlCommand = mysql.CreateCommand();
        int mysqlOrdinal = PalORM.MySql.MySqlProvider.ConfigureSchemaCommand(mysqlCommand, "users", "app");

        await using var sqlite = PalORM.Sqlite.SqliteProvider.CreateConnection(
            "Data Source=:memory:", new DbOptions { ConnectionString = "Data Source=:memory:" });
        await using var sqliteCommand = sqlite.CreateCommand();
        int sqliteOrdinal = PalORM.Sqlite.SqliteProvider.ConfigureSchemaCommand(sqliteCommand, "users");

        await Assert.That(postgresOrdinal).IsEqualTo(0);
        await Assert.That(postgresCommand.Parameters.Count).IsEqualTo(2);
        await Assert.That(mysqlOrdinal).IsEqualTo(0);
        await Assert.That(mysqlCommand.CommandText).Contains("`app`.`users`");
        await Assert.That(sqliteOrdinal).IsEqualTo(1);
        await Assert.That(sqliteCommand.CommandText).Contains("\"users\"");
    }

    [Test]
    public async Task BulkInsert_EmptyList_UnregisteredType_ThrowsConsistently()
    {
        // ITM-637/662 锁定：空列表 + 未注册类型与 + 非空列表一致抛（元数据检查先于空短路）
        await using var session = await PalORM.DataSession<PalORM.Sqlite.SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "DataSource=:memory:" });
        await Assert.That(async () => await session.BulkInsertAsync(new List<UnregisteredEntity>()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BulkDelete_EmptyKeys_UnregisteredType_ThrowsConsistently()
    {
        // r14-S3 锁定（r13-S1 修复的行为面）：Delete 侧与 Insert 侧同族口径
        await using var session = await PalORM.DataSession<PalORM.Sqlite.SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "DataSource=:memory:" });
        await Assert.That(async () => await session.BulkDeleteAsync<UnregisteredEntity>([]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BulkUpdateBatch_EmptyList_UnregisteredType_ThrowsConsistently()
    {
        // r14-S3：UpdateBatch 侧同族（r12-B1 修复的行为面）
        await using var session = await PalORM.DataSession<PalORM.Sqlite.SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "DataSource=:memory:" });
        await Assert.That(async () => await session.BulkUpdateBatchAsync(new List<UnregisteredEntity>()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Seed_EmptyList_UnregisteredType_ThrowsConsistently()
    {
        // r14-S3：Seed 侧同族（r12-B1 修复的行为面）
        await using var session = await PalORM.DataSession<PalORM.Sqlite.SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "DataSource=:memory:" });
        await Assert.That(async () => await session.SeedAsync(new List<UnregisteredEntity>()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BulkUpdate_EmptyList_UnregisteredType_ThrowsConsistently()
    {
        // r15-DB1 锁定（第五侧）
        await using var session = await PalORM.DataSession<PalORM.Sqlite.SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "DataSource=:memory:" });
        await Assert.That(async () => await session.BulkUpdateAsync(new List<UnregisteredEntity>()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BulkMerge_EmptyList_UnregisteredType_ThrowsConsistently()
    {
        // r15-DB2 锁定（第六侧——族全闭）
        await using var session = await PalORM.DataSession<PalORM.Sqlite.SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "DataSource=:memory:" });
        await Assert.That(async () => await session.BulkMergeAsync(new List<UnregisteredEntity>()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CommandTimeout_Zero_SecondsMapsToZeroInfinite()
    {
        // ITM-619/662 锁定：Zero 透传为 0（ADO.NET 无限等待）——Resilience 侧归一
        // InfiniteTimeSpan 的上游契约面（Validate 允许 Zero + 秒值=0）
        var options = new DbOptions { ConnectionString = "x", CommandTimeout = TimeSpan.Zero };
        options.Validate();
        await Assert.That(DbOptions.ToCommandTimeoutSeconds(TimeSpan.Zero)).IsEqualTo(0);
        await Assert.That(options.CommandTimeoutSeconds).IsEqualTo(0);
    }

    private sealed class UnregisteredEntity { public string Name { get; set; } = ""; }
}
