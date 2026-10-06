using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// 批容量算式的租户参数扣减契约（ITM-884，ITM-809 族第 4 处的锁定测试）。
/// 不变式：租户实体的批量 Upsert 每批命令的参数总数不得超过方言上限
/// （SQLite 自设保守值 999）——租户守卫在冲突子句追加的 1 个租户参数必须被
/// batchSize 算式的分子扣减，否则"恰好满批"形态每批超限 1 个参数。
/// 触发口径：UpsertColumns = 27（999 = 27 × 37 的整除因子）→ 修复前 batchSize=37、
/// 每批 37×27+1 = 1000 个参数 &gt; 999；修复后 998/27=36、963+1 = 964 ≤ 999。
/// 观察面：命令层采集（Bulk 路径不经过 IQueryInterceptor，见 BulkDeleteArrayFormTests
/// 同款结论）——包装 Provider/Connection/Command，ExecuteNonQueryAsync 前记录参数数。
/// ITM-883（BulkUpdateAsync 单语句批量，MySQL/PG only）同族算式，MySQL 真库满批
/// 用例（16 SET 列 × 3855 行）登记待补——SQLite 路径不经过该分支无法代理。
/// </summary>
[NotInParallel]
internal sealed class BulkBatchCapacityTests
{
    /// <summary>每语句采集的 (SQL 文本, 参数数)。类级 NotInParallel 串行使用（静态采集器）。</summary>
    private static readonly List<(string Sql, int ParamCount)> _statements = [];

    /// <summary>命令包装层的采集入口（Bulk 路径不经拦截器，只能命令层采集）。</summary>
    internal static void RecordStatement(string sql, int paramCount) => _statements.Add((sql, paramCount));

    [Test]
    public async Task BulkMerge_TenantWideEntity_EveryBatchWithinBindParameterLimit()
    {
        _statements.Clear();
        string tag = Guid.NewGuid().ToString("N");
        using SqliteConnection keeper = await OpenKeeperAsync(tag);

        await using DataSession<CapturingUpsertProvider> session = await DataSession<CapturingUpsertProvider>.CreateAsync(
            new DbOptions
            {
                ConnectionString = $"Data Source=tenant_wide_{tag};Mode=Memory;Cache=Shared",
                MaxRetries = 0,
                CircuitBreakerThreshold = 0,
            });
        session.WithTenant(7);

        const int rowCount = 37; // 修复前恰好构成一批 37 行（999/27 的整除商）
        List<TenantWideProbe> rows = [.. Enumerable.Range(1, rowCount)
            .Select(i => new TenantWideProbe { Id = i, TenantId = 7, F01 = i, F02 = i, F03 = i, F04 = i, F05 = i, F06 = i, F07 = i, F08 = i, F09 = i, F10 = i, F11 = i, F12 = i, F13 = i, F14 = i, F15 = i, F16 = i, F17 = i, F18 = i, F19 = i, F20 = i, F21 = i, F22 = i, F23 = i, F24 = i, F25 = i })];
        long merged = await session.BulkMergeAsync(rows);
        await Assert.That(merged).IsEqualTo(rowCount);

        // 断言具体条目（T14）：批语句存在 + 每批参数数在方言上限内
        var batches = _statements
            .Where(s => s.Sql.Contains("ON CONFLICT", StringComparison.Ordinal))
            .ToList();
        await Assert.That(batches.Count).IsGreaterThan(0);
        const int sqliteLimit = 999; // 与 SqlLimits.MaxBindParametersFor(SqlDialect.Sqlite) 同值；测试不引 internal 拷贝口径
        foreach ((string sql, int paramCount) in batches)
            await Assert.That(paramCount).IsLessThanOrEqualTo(sqliteLimit);
        // 修复前形态：27 列租户实体的满批参数恰为 37×27+1 = 1000 > 999（撤修复此断言变红）
        await Assert.That(batches.Max(static b => b.ParamCount)).IsLessThanOrEqualTo(sqliteLimit);
    }

    private static async Task<SqliteConnection> OpenKeeperAsync(string tag)
    {
        var keeper = new SqliteConnection($"Data Source=tenant_wide_{tag};Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();
        await using SqliteCommand cmd = keeper.CreateCommand();
        // 测试夹具 DDL：列名由本文件硬编码生成，无用户输入（S2077/CA2100 豁免理由）
#pragma warning disable S2077, CA2100
        cmd.CommandText =
            "CREATE TABLE tenant_wide_probe (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, " +
            string.Join(", ", Enumerable.Range(1, 25).Select(i => $"f{i:00} INTEGER NOT NULL")) + ")";
#pragma warning restore S2077, CA2100
        await cmd.ExecuteNonQueryAsync();
        return keeper;
    }
}

/// <summary>采集包装的 Provider——方言声明 SQLite（驱动真实 SQLite 语义），连接走包装。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Generic type argument only — static abstract interface members are never invoked on an instance.")]
internal sealed class CapturingUpsertProvider : IDbProvider
{
    public static string Name => "CapturingUpsert";

    public static SqlDialect Dialect => SqlDialect.Sqlite;

    public static DbConnection CreateConnection(string connectionString, DbOptions options)
        => new CapturingUpsertConnection(connectionString);

    public static string QuoteIdentifier(string identifier)
        => SqliteProvider.QuoteIdentifier(identifier);

    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => SqliteProvider.QuoteQualifiedIdentifier(schema, identifier);

    public static bool SupportsReturningClause => true;

    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    public static DbParameter CreateParameter(string name, object? value)
        => new SqliteParameter(name, value ?? DBNull.Value);

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
        => SqliteProvider.ConfigureSchemaCommand(command, tableName, schema);
}

internal sealed class CapturingUpsertConnection(string connectionString) : DbConnection
{
    private readonly SqliteConnection _inner = CreateInner(connectionString);

    private static SqliteConnection CreateInner(string cs)
    {
        var inner = new SqliteConnection(cs);
        inner.Open();
        return inner;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        // 连接串由构造函数一次性消费（inner 已建）；DataSession 可能回读该属性，
        // 但不会再写入生效路径——空 setter 保留属性契约。
        set => _ = value;
    }

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Open() { /* inner 已在构造时打开（CreateConnection 契约：返回已就绪连接） */ }
    public override void Close() => _inner.Close();
    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override ConnectionState State => _inner.State;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => _inner.BeginTransaction(isolationLevel);
    protected override DbCommand CreateDbCommand() => new CapturingUpsertCommand(_inner.CreateCommand());

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class CapturingUpsertCommand(SqliteCommand inner) : DbCommand
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => inner.CommandText;
        // 文本由 Core 生成（参数化语句），本包装透传给驱动——无用户输入（CA2100 豁免理由）
#pragma warning disable CA2100
        set => inner.CommandText = value;
#pragma warning restore CA2100
    }

    public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
    public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
    protected override DbConnection? DbConnection
    {
        get => inner.Connection;
        set => inner.Connection = (SqliteConnection?)value;
    }
    protected override DbParameterCollection DbParameterCollection => inner.Parameters;
    protected override DbTransaction? DbTransaction
    {
        get => inner.Transaction;
        set => inner.Transaction = (SqliteTransaction?)value;
    }
    public override void Cancel() => inner.Cancel();
    public override int ExecuteNonQuery()
    {
        Record();
        return inner.ExecuteNonQuery();
    }
    public override object? ExecuteScalar()
    {
        Record();
        return inner.ExecuteScalar();
    }
    public override void Prepare() => inner.Prepare();
    protected override DbParameter CreateDbParameter() => inner.CreateParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        Record();
        return inner.ExecuteReader(behavior);
    }
    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        Record();
        return inner.ExecuteNonQueryAsync(cancellationToken);
    }

    private void Record()
        => BulkBatchCapacityTests.RecordStatement(inner.CommandText, inner.Parameters.Count);
}

[TenantAware]
[Table("tenant_wide_probe")]
internal sealed partial class TenantWideProbe
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("f01")] public long F01 { get; set; }
    [Column("f02")] public long F02 { get; set; }
    [Column("f03")] public long F03 { get; set; }
    [Column("f04")] public long F04 { get; set; }
    [Column("f05")] public long F05 { get; set; }
    [Column("f06")] public long F06 { get; set; }
    [Column("f07")] public long F07 { get; set; }
    [Column("f08")] public long F08 { get; set; }
    [Column("f09")] public long F09 { get; set; }
    [Column("f10")] public long F10 { get; set; }
    [Column("f11")] public long F11 { get; set; }
    [Column("f12")] public long F12 { get; set; }
    [Column("f13")] public long F13 { get; set; }
    [Column("f14")] public long F14 { get; set; }
    [Column("f15")] public long F15 { get; set; }
    [Column("f16")] public long F16 { get; set; }
    [Column("f17")] public long F17 { get; set; }
    [Column("f18")] public long F18 { get; set; }
    [Column("f19")] public long F19 { get; set; }
    [Column("f20")] public long F20 { get; set; }
    [Column("f21")] public long F21 { get; set; }
    [Column("f22")] public long F22 { get; set; }
    [Column("f23")] public long F23 { get; set; }
    [Column("f24")] public long F24 { get; set; }
    [Column("f25")] public long F25 { get; set; }
}
