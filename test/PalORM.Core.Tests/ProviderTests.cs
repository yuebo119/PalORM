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
    public async Task SqliteConnectionFactory_RejectsUnsupportedPoolOptions()
    {
        var options = new DbOptions { ConnectionString = "Data Source=:memory:" }.WithPool(10);
        await Assert.That(() => PalORM.Sqlite.SqliteProvider.CreateConnection(options.ConnectionString, options))
            .Throws<NotSupportedException>();
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
