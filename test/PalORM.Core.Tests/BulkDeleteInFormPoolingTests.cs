using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>IN 形态批量删除的「命令与参数实例跨批复用」锁定测试（2026-10-07 池化恢复）。
/// <para><b>为什么必须有实例级断言</b>：R-UNNESTB 变体 C（Clear 后重加 per-key 新建实例 →
/// PG auto-prepare 第 3 个等长批重发第 2 批的键）与池化形态在"结果对不对"上完全一致——
/// 本机 SQLite 无 auto-prepare，两种形态都能删干净。只有断言「等长批之间参数实例
/// <c>ReferenceEquals</c> 逐位相同 + Value 已更新为当前批键」，才能把池化形态钉住；
/// 回退到每批新建命令（2026-10-05 审计形态）时实例断言必红（B95 家族：数字对但没测到目标）。</para>
/// <para><b>与 <see cref="BulkDeleteArrayFormTests"/> 同一手法</b>：连接/命令包装在执行前采集
/// 语句形状与参数实例引用。Provider 报 SQLite 方言（数组形态能力检测在此为 false，必走 IN 形态）。</para>
/// <para><b>组键 InFormPoolingFixtures</b>：静态采集钩子要求组内串行（TestInfra 不变式）。</para></summary>
[NotInParallel("InFormPoolingFixtures")]
internal sealed class BulkDeleteInFormPoolingTests
{
    /// <summary>每次执行的采集快照：语句文本 + 参数实例引用（执行前逐位快照，元素与集合内同一实例）。</summary>
    private static readonly List<(string Sql, DbParameter[] Parameters)> Statements = [];

    private static async Task<DataSession<InFormSqliteProvider>> CreateSessionAsync()
    {
        Statements.Clear();
        InFormRecordingCommand.SetRecorder((sql, parameters) => Statements.Add((sql, parameters)));
        DataSession<InFormSqliteProvider> session =
            await DataSession<InFormSqliteProvider>.CreateAsync(
                new DbOptions
                {
                    ConnectionString = $"Data Source=inpool_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                });
        return session;
    }

    /// <summary>播种 <paramref name="count"/> 行（id 从 1 起连续 long 主键）。逐行参数化 INSERT，
    /// 与 <see cref="BulkDeleteArrayFormTests"/> 播种纪律一致：不给被测路径的夹具加 override。
    /// 表名内联（插值会被 FormattableString 参数化成 <c>CREATE TABLE @p0</c> 语法错）。</summary>
    private static async Task SeedAsync(
        DataSession<InFormSqliteProvider> session, int count)
    {
        await session.ExecuteAsync(
            $"CREATE TABLE in_pool_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        for (int i = 1; i <= count; i++)
            await session.ExecuteAsync($"INSERT INTO in_pool_rows (id, name) VALUES ({i}, {"row" + i})");
    }

    private static List<object> Keys(int from, int to)
    {
        var keys = new List<object>(to - from + 1);
        for (long i = from; i <= to; i++) keys.Add(i);
        return keys;
    }

    /// <summary>等长批核心断言：语句逐位相同 + 参数实例逐位同一引用 + 值已更新为当前批键。
    /// 三者缺一都会让"每批新建命令"的回退形态继续绿（实例换新）或错配形态绿（值未更新）。</summary>
    [Test]
    public async Task BulkDelete_InForm_EqualBatches_ReuseParameterInstances()
    {
        await using DataSession<InFormSqliteProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 1998); // 999 + 999，无末批
        Statements.Clear(); // 播种语句不参与形态断言

        long deleted = await session.BulkDeleteAsync<InFormPoolRow>(Keys(1, 1998));

        await Assert.That(deleted).IsEqualTo(1998);
        await Assert.That((await session.GetAllAsync<InFormPoolRow>()).Count).IsEqualTo(0);
        await Assert.That(Statements.Count).IsEqualTo(2);

        (string sql1, DbParameter[] p1) = Statements[0];
        (string sql2, DbParameter[] p2) = Statements[1];
        await Assert.That(sql2).IsEqualTo(sql1);
        await Assert.That(p1.Length).IsEqualTo(999);
        await Assert.That(p2.Length).IsEqualTo(999);
        for (int i = 0; i < 999; i++)
        {
            await Assert.That(ReferenceEquals(p1[i], p2[i])).IsTrue();
            await Assert.That(p2[i].Value).IsEqualTo((long)(1000 + i));
        }
    }

    /// <summary>末批（更短）断言：单独短语句（占位符收敛到末批长度）+ 新参数集合承载末批键值；
    /// 满批两批之间实例逐位同一（与用例 1 同判据，此处顺带锁定批 2 不受末批影响）。</summary>
    [Test]
    public async Task BulkDelete_InForm_FinalBatchUsesShortStatement()
    {
        await using DataSession<InFormSqliteProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 2000); // 999 + 999 + 2
        Statements.Clear(); // 播种语句不参与形态断言

        long deleted = await session.BulkDeleteAsync<InFormPoolRow>(Keys(1, 2000));

        await Assert.That(deleted).IsEqualTo(2000);
        await Assert.That((await session.GetAllAsync<InFormPoolRow>()).Count).IsEqualTo(0);
        await Assert.That(Statements.Count).IsEqualTo(3);

        (string sql1, DbParameter[] p1) = Statements[0];
        (string sql2, DbParameter[] p2) = Statements[1];
        (string sql3, DbParameter[] p3) = Statements[2];
        await Assert.That(sql2).IsEqualTo(sql1);
        for (int i = 0; i < 999; i++)
            await Assert.That(ReferenceEquals(p1[i], p2[i])).IsTrue();
        await Assert.That(p3.Length).IsEqualTo(2);
        await Assert.That(sql3).Contains("IN (@p0, @p1)");
        await Assert.That(sql3).DoesNotContain("@p2");
        await Assert.That(p3[0].Value).IsEqualTo(1999L);
        await Assert.That(p3[1].Value).IsEqualTo(2000L);
    }

    /// <summary>租户 + 末批：满批与末批的租户参数均位于键参数之后末位、值恒为会话租户——
    /// ITM-809 的批容量扣减（999-1=998/批）在此形态下同样成立。t2 行保留用无租户校验会话
    /// 显式核验（租户开启后 GetAllAsync 自身带过滤，数不到他租户行）。</summary>
    [Test]
    public async Task BulkDelete_InForm_TenantParameterSurvivesFinalBatch()
    {
        Statements.Clear();
        InFormRecordingCommand.SetRecorder((sql, parameters) => Statements.Add((sql, parameters)));
        string cs = $"Data Source=inpool_tenant_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using DataSession<InFormSqliteProvider> tenantSession =
            await DataSession<InFormSqliteProvider>.CreateAsync(new DbOptions { ConnectionString = cs });
        await tenantSession.ExecuteAsync(
            $"CREATE TABLE in_pool_tenant_rows (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, qty INTEGER NOT NULL)");
        for (int i = 1; i <= 1001; i++)
            await tenantSession.ExecuteAsync(
                $"INSERT INTO in_pool_tenant_rows (id, tenant_id, qty) VALUES ({i}, {"t1"}, {i})");
        for (int i = 5001; i <= 5005; i++)
            await tenantSession.ExecuteAsync(
                $"INSERT INTO in_pool_tenant_rows (id, tenant_id, qty) VALUES ({i}, {"t2"}, {i})");
        Statements.Clear(); // 播种语句不参与形态断言
        tenantSession.WithTenant("t1");

        long deleted = await tenantSession.BulkDeleteAsync<InFormPoolTenantRow>(Keys(1, 1001)); // 998 + 3

        await Assert.That(deleted).IsEqualTo(1001);
        await Assert.That(Statements.Count).IsEqualTo(2);
        (string sql1, DbParameter[] p1) = Statements[0];
        (string _, DbParameter[] p2) = Statements[1];
        await Assert.That(p1.Length).IsEqualTo(999); // 998 键 + 1 租户
        await Assert.That(p1[998].ParameterName).IsEqualTo("@__tenant0");
        await Assert.That(p1[998].Value).IsEqualTo("t1");
        await Assert.That(p2.Length).IsEqualTo(4); // 3 键 + 1 租户
        await Assert.That(p2[3].ParameterName).IsEqualTo("@__tenant0");
        await Assert.That(p2[3].Value).IsEqualTo("t1");
        await Assert.That(sql1).Contains("@__tenant0");
        // 跨租户护栏（ITM-404）：无租户会话核验 t2 行全部保留
        await using DataSession<InFormSqliteProvider> verifier =
            await DataSession<InFormSqliteProvider>.CreateAsync(new DbOptions { ConnectionString = cs });
        await Assert.That((await verifier.GetAllAsync<InFormPoolTenantRow>()).Count).IsEqualTo(5);
    }
}

/// <summary>IN 形态池化测试的 Provider——报 SQLite 方言（数组形态能力检测为 false），
/// 连接返回包装命令以在执行前采集参数实例。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Generic type argument only — static abstract interface members are never invoked on an instance.")]
internal sealed class InFormSqliteProvider : IDbProvider
{
    public static string Name => "InFormSqlite";

    public static SqlDialect Dialect => SqlDialect.Sqlite;

    public static DbConnection CreateConnection(string connectionString, DbOptions options)
        => new InFormRecordingConnection(connectionString);

    public static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    public static bool SupportsReturningClause => true;

    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    public static DbParameter CreateParameter(string name, object? value)
        => new SqliteParameter(name, value ?? DBNull.Value);

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
#pragma warning disable S2077, CA2100 // 表名经 QuoteIdentifier 转义；测试夹具无用户输入
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
#pragma warning restore S2077, CA2100
        return 1;
    }
}

[Table("in_pool_rows")]
internal sealed partial class InFormPoolRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";
}

[TenantAware]
[Table("in_pool_tenant_rows")]
internal sealed partial class InFormPoolTenantRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("tenant_id")]
    public string TenantId { get; set; } = "";

    [Column("qty")]
    public long Qty { get; set; }
}

/// <summary>包装连接：<c>CreateDbCommand</c> 返回 <see cref="InFormRecordingCommand"/>。</summary>
internal sealed class InFormRecordingConnection(string connectionString) : DbConnection
{
    private readonly SqliteConnection _inner = new(connectionString);

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value ?? "";
    }

    public override string Database => _inner.Database;

    public override string DataSource => _inner.DataSource;

    public override string ServerVersion => _inner.ServerVersion;

    public override System.Data.ConnectionState State => _inner.State;

    public override void Open() => _inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);

    public override void Close() => _inner.Close();

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

    protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
        => _inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() => new InFormRecordingCommand(_inner.CreateCommand());

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

/// <summary>包装命令：在 <c>ExecuteNonQuery</c> 前采集语句文本与参数实例引用快照。
/// Core 走 <c>ExecuteNonQueryAsync</c>，基类默认实现转调同步 <c>ExecuteNonQuery</c>，
/// 故无需覆写异步路径（与 <c>ArrayAnySqliteCommand</c> 同一依据）。</summary>
internal sealed class InFormRecordingCommand(SqliteCommand inner) : DbCommand
{
    private static Action<string, DbParameter[]>? _onStatement;

    /// <summary>注册采集钩子（测试串行使用，见类级 <c>NotInParallel</c>）。</summary>
    internal static void SetRecorder(Action<string, DbParameter[]> recorder) => _onStatement = recorder;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => inner.CommandText;
#pragma warning disable CA2100 // value 是 Core 生成的语句文本，非用户输入
        set => inner.CommandText = value ?? "";
#pragma warning restore CA2100
    }

    public override int CommandTimeout
    {
        get => inner.CommandTimeout;
        set => inner.CommandTimeout = value;
    }

    public override System.Data.CommandType CommandType
    {
        get => inner.CommandType;
        set => inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => inner.DesignTimeVisible;
        set => inner.DesignTimeVisible = value;
    }

    public override System.Data.UpdateRowSource UpdatedRowSource
    {
        get => inner.UpdatedRowSource;
        set => inner.UpdatedRowSource = value;
    }

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

    private DbParameter[] SnapshotParameters()
    {
        DbParameter[] snapshot = new DbParameter[inner.Parameters.Count];
        for (int i = 0; i < snapshot.Length; i++) snapshot[i] = inner.Parameters[i];
        return snapshot;
    }

    public override int ExecuteNonQuery()
    {
        _onStatement?.Invoke(inner.CommandText, SnapshotParameters());
        return inner.ExecuteNonQuery();
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        _onStatement?.Invoke(inner.CommandText, SnapshotParameters());
        return inner.ExecuteNonQueryAsync(cancellationToken);
    }

    public override object? ExecuteScalar() => inner.ExecuteScalar();

    public override void Prepare() => inner.Prepare();

    protected override DbParameter CreateDbParameter() => inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior)
        => inner.ExecuteReader(behavior);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        System.Data.CommandBehavior behavior, CancellationToken cancellationToken)
        // SqliteDataReader 是 DbDataReader 的子类，但 Task<T> 不变——需显式桥接。
        => WrapAsync(inner.ExecuteReaderAsync(behavior, cancellationToken));

    private static async Task<DbDataReader> WrapAsync(Task<SqliteDataReader> pending)
        => await pending.ConfigureAwait(false);

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
