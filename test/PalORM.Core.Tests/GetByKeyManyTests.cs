using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>多键点读（<c>GetByKeyManyAsync</c>，2026-11-05 性能方案 Phase 1）的锁定测试。
/// <para><b>为什么需要这一组</b>：新 API 有三条方言形态（SQLite <c>json_each</c> 单参数 /
/// PG <c>= ANY</c> / 回退分批 <c>IN</c>），三条路径在"结果对不对"上完全一致——只断言行数
/// 与键集合无法区分它们。故本组用连接包装采集语句文本，把每条形态各自钉住；
/// 字符串主键与敌对键锁住 JSON 转义（转义错 = 漏读，不是注入面）；默认过滤用例锁住
/// 批量形态不能成为绕过软删/租户的缺口。</para>
/// <para><b>组键 ReadManyFixtures</b>：静态采集钩子要求组内串行（TestInfra 不变式）。</para></summary>
[NotInParallel("ReadManyFixtures")]
internal sealed class GetByKeyManyTests
{
    /// <summary>执行前采集的语句文本（按执行顺序）。</summary>
    private static readonly List<string> Statements = [];

    private static async Task<DataSession<ReadManySqliteProvider>> CreateSessionAsync()
    {
        Statements.Clear();
        ReadManyRecordingCommand.SetRecorder(Statements.Add);
        DataSession<ReadManySqliteProvider> session = await DataSession<ReadManySqliteProvider>.CreateAsync(
            new DbOptions
            {
                ConnectionString = $"Data Source=readmany_{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
            });
        await session.ExecuteAsync(
            $"CREATE TABLE read_many_rows (Id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        await session.ExecuteAsync(
            $"CREATE TABLE read_many_string (Id TEXT PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        await session.ExecuteAsync(
            $"CREATE TABLE read_many_soft (Id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL, deleted_at TEXT NULL)");
        await session.ExecuteAsync(
            $"CREATE TABLE read_many_tenant (Id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        await session.ExecuteAsync(
            $"CREATE TABLE read_many_inform (Id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        return session;
    }

    /// <summary>用递归 CTE 单语句播种 1..count（不依赖被测 Provider 的 BulkInsertAsync——
    /// 夹具不 override 被测路径的能力面，与 BulkDeleteInFormPoolingTests 播种纪律一致）。</summary>
    private static async Task SeedNumericAsync(DataSession<ReadManySqliteProvider> session, int count)
        => await session.ExecuteAsync(
            $"""
            WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < {count})
            INSERT INTO read_many_rows (Id, name, qty) SELECT i, 'name-' || i, i % 100 FROM seq
            """);

    private static List<object> Keys(int from, int to)
    {
        var keys = new List<object>(to - from + 1);
        for (int i = from; i <= to; i++) keys.Add((long)i);
        return keys;
    }

    [Test]
    public async Task NumericKeys_Sqlite_UsesJsonEachSingleParameter()
    {
        // 2500 键 > SQLite 保守参数上限 999——json_each 单参数无此约束，且语句文本与键数无关
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        await SeedNumericAsync(session, 2500);

        Statements.Clear();
        List<ReadManyRow> rows = await session.GetByKeyManyAsync<ReadManyRow>(Keys(1, 2500));

        await Assert.That(rows.Count).IsEqualTo(2500);
        await Assert.That(Statements.Count).IsEqualTo(1);
        await Assert.That(Statements[0]).Contains("json_each(");
        // 单参数：除租户参数外不应有 @p0/@p1 之类的逐键占位符
        await Assert.That(Statements[0].Contains("@p0", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task NumericKeys_PartialOverlap_ReturnsOnlyMatchingRows()
    {
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        await SeedNumericAsync(session, 100);

        List<ReadManyRow> rows = await session.GetByKeyManyAsync<ReadManyRow>(
            new List<object> { 10L, 20L, 999_999L });

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows.Any(static r => r.Id == 10L)).IsTrue();
        await Assert.That(rows.Any(static r => r.Id == 20L)).IsTrue();
        await Assert.That(rows.Any(static r => r.Id == 999_999L)).IsFalse();
    }

    [Test]
    public async Task EmptyKeyCollection_ReturnsEmptyWithoutSql()
    {
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        await SeedNumericAsync(session, 10);

        Statements.Clear();
        List<ReadManyRow> rows = await session.GetByKeyManyAsync<ReadManyRow>(Array.Empty<object>());

        await Assert.That(rows.Count).IsEqualTo(0);
        // 空集合不发 SQL
        await Assert.That(Statements.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StringPk_HostileKeys_RoundTripPrecisely()
    {
        // 字符串主键走同一 json_each 形态：转义正确性是匹配正确性（漏转义 = 漏读）
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        string[] hostile =
        [
            "he said \"hi\"",
            "back\\slash",
            "line\nbreak\ttab",
            "ctrl\u0001char",
            "换行中文键",
            "plain",
        ];
        string[] decoys = ["he said \"hi\" x", "back\\\\slash", "plainx"];
        var all = new List<ReadManyStringRow>(hostile.Length + decoys.Length);
        int qty = 1;
        foreach (string id in hostile.Concat(decoys))
        {
            all.Add(new ReadManyStringRow { Id = id, Name = "n" + qty, Qty = qty++ });
        }
        foreach (ReadManyStringRow row in all)
        {
            await session.ExecuteAsync(
                $"INSERT INTO read_many_string (Id, name, qty) VALUES ({row.Id}, {row.Name}, {row.Qty})");
        }

        List<ReadManyStringRow> rows = await session.GetByKeyManyAsync<ReadManyStringRow>(
            hostile.Cast<object>().ToList());

        await Assert.That(rows.Count).IsEqualTo(hostile.Length);
        await Assert.That(rows.Select(static r => r.Id).OrderBy(static s => s, StringComparer.Ordinal)
            .SequenceEqual(hostile.OrderBy(static s => s, StringComparer.Ordinal))).IsTrue();
        await Assert.That(rows.Any(static r => r.Id == "plainx")).IsFalse();
    }

    [Test]
    public async Task SoftDeleteFilter_HidesDeletedRows()
    {
        // 默认过滤必须适用——否则批量读会成为读已软删数据的缺口
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        var rows = new List<ReadManySoftRow>(10);
        for (int i = 1; i <= 10; i++)
        {
            rows.Add(new ReadManySoftRow { Id = i, Name = "n" + i, Qty = i, DeletedAt = i % 2 == 0 ? "gone" : null });
        }
        foreach (ReadManySoftRow row in rows)
        {
            await session.ExecuteAsync(
                $"INSERT INTO read_many_soft (Id, name, qty, deleted_at) VALUES ({row.Id}, {row.Name}, {row.Qty}, {row.DeletedAt})");
        }

        List<ReadManySoftRow> visible = await session.GetByKeyManyAsync<ReadManySoftRow>(Keys(1, 10));
        await Assert.That(visible.Count).IsEqualTo(5);
        await Assert.That(visible.All(static r => r.Id % 2 == 1)).IsTrue();

        // IgnoreFilters 下同一键集合应读到全部 10 行
        List<ReadManySoftRow> all = await session.IgnoreFilters().GetByKeyManyAsync<ReadManySoftRow>(Keys(1, 10));
        await Assert.That(all.Count).IsEqualTo(10);
    }

    [Test]
    public async Task TenantFilter_CrossTenantKeysReturnNothing()
    {
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        await session.InsertAsync(new ReadManyTenantRow { Id = 1, TenantId = 1, Name = "a", Qty = 1 });
        await session.InsertAsync(new ReadManyTenantRow { Id = 2, TenantId = 2, Name = "b", Qty = 2 });

        List<ReadManyTenantRow> mine = await session.WithTenant(1L)
            .GetByKeyManyAsync<ReadManyTenantRow>(new List<object> { 1L, 2L });

        await Assert.That(mine.Count).IsEqualTo(1);
        await Assert.That(mine[0].Id).IsEqualTo(1L);
        await Assert.That(mine[0].Name).IsEqualTo("a");
    }

    [Test]
    public async Task InFormFallback_MultiBatch_ReturnsAllRows()
    {
        // 强制 IN 回退形态（json_each 门控关掉）：2500 键 = 999/批 ×3 批，跨批结果必须完整。
        // 独立实体（ReadManyInFormRow）承载——能力位按 (Provider, T) 单调，共用 ReadManyRow
        // 会把 json_each 用例一并毒化（组内执行顺序不定）。
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        DataSession<ReadManySqliteProvider>.ForceInFormDeleteForTests<ReadManyInFormRow>();
        await session.ExecuteAsync(
            $"""
            WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < 2500)
            INSERT INTO read_many_inform (Id, name, qty) SELECT i, 'name-' || i, i % 100 FROM seq
            """);

        Statements.Clear();
        List<ReadManyInFormRow> rows = await session.GetByKeyManyAsync<ReadManyInFormRow>(Keys(1, 2500));

        await Assert.That(rows.Count).IsEqualTo(2500);
        await Assert.That(rows.Select(static r => r.Id).OrderBy(static x => x)
            .SequenceEqual(Enumerable.Range(1, 2500).Select(static i => (long)i))).IsTrue();
        // 多批：每批一条独立语句（回退形态不做跨批参数复用）
        int inFormStatements = Statements.Count(static s => s.Contains(" IN (", StringComparison.Ordinal)
            && !s.Contains("json_each", StringComparison.Ordinal));
        await Assert.That(inFormStatements).IsEqualTo(3);
    }

    [Test]
    public async Task InFormFallback_MatchesJsonEachForm_OnSameKeys()
    {
        // 两形态结果集必须逐行等价（形态差异只在语句形状，不在语义）。
        // 同一份数据写两张同构表：ReadManyRow 走 json_each、ReadManyInFormRow 走 IN 回退。
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        await SeedNumericAsync(session, 100);
        await session.ExecuteAsync(
            $"INSERT INTO read_many_inform (Id, name, qty) SELECT Id, name, qty FROM read_many_rows");
        DataSession<ReadManySqliteProvider>.ForceInFormDeleteForTests<ReadManyInFormRow>();

        List<ReadManyRow> viaJsonEach = await session.GetByKeyManyAsync<ReadManyRow>(Keys(1, 100));
        List<ReadManyInFormRow> viaInForm = await session.GetByKeyManyAsync<ReadManyInFormRow>(Keys(1, 100));

        await Assert.That(viaInForm.Count).IsEqualTo(viaJsonEach.Count);
        await Assert.That(viaInForm.Select(static r => (r.Id, r.Name, r.Qty))
            .SequenceEqual(viaJsonEach.Select(static r => (r.Id, r.Name, r.Qty)))).IsTrue();
    }

    [Test]
    public async Task NullKeyEntry_ThrowsInsteadOfSilentMiss()
    {
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        await SeedNumericAsync(session, 10);

        Exception? thrown = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await session.GetByKeyManyAsync<ReadManyRow>(new List<object> { 1L, null!, 3L }));

        await Assert.That(thrown).IsNotNull();
    }

    [Test]
    public async Task LargeKeyCollection_DoesNotExceedSqliteParameterLimit()
    {
        // 20000 键（> 保守上限 999 与 MaxRowsPerBatch 5000）：json_each 形态仍单语句，
        // 不因键数越界参数上限
        await using DataSession<ReadManySqliteProvider> session = await CreateSessionAsync();
        await SeedNumericAsync(session, 20000);

        Statements.Clear();
        List<ReadManyRow> rows = await session.GetByKeyManyAsync<ReadManyRow>(Keys(1, 20000));

        await Assert.That(rows.Count).IsEqualTo(20000);
        int jsonEachStatements = Statements.Count(static s => s.Contains("json_each(", StringComparison.Ordinal));
        await Assert.That(jsonEachStatements).IsEqualTo(1);
    }
}

/// <summary>本组专用 Provider：报 SQLite 方言，连接返回包装命令以采集语句文本
/// （<see cref="BulkDeleteInFormPoolingTests"/> 同手法——自带实现，不复用 SqliteProvider
/// 的接口面，避免测试夹具被产品默认实现变更牵连）。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Generic type argument only — static abstract interface members are never invoked on an instance.")]
internal sealed class ReadManySqliteProvider : IDbProvider
{
    public static string Name => "ReadManySqlite";

    public static SqlDialect Dialect => SqlDialect.Sqlite;

    public static DbConnection CreateConnection(string connectionString, DbOptions options)
        => new ReadManyRecordingConnection(connectionString);

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

[Table("read_many_rows")]
internal sealed partial class ReadManyRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
}

[Table("read_many_string")]
internal sealed partial class ReadManyStringRow
{
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
}

[Table("read_many_inform")]
internal sealed partial class ReadManyInFormRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
}

[Table("read_many_soft")]
[SoftDelete]
internal sealed partial class ReadManySoftRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}

[Table("read_many_tenant")]
[TenantAware]
internal sealed partial class ReadManyTenantRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("tenant_id")] [Required] public long TenantId { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
}

/// <summary>包装连接：<c>CreateDbCommand</c> 返回 <see cref="ReadManyRecordingCommand"/>。</summary>
internal sealed class ReadManyRecordingConnection(string connectionString) : DbConnection
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

    protected override DbCommand CreateDbCommand() => new ReadManyRecordingCommand(_inner.CreateCommand());

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

/// <summary>包装命令：在读取器创建前采集语句文本。多键读走
/// <c>ExecuteReaderAsync</c>，故同步与异步两条读取入口都采集（非查询命令不采集）。</summary>
internal sealed class ReadManyRecordingCommand(SqliteCommand inner) : DbCommand
{
    private static Action<string>? _onStatement;

    /// <summary>注册采集钩子（测试串行使用，见类级 <c>NotInParallel</c>）。</summary>
    internal static void SetRecorder(Action<string> recorder) => _onStatement = recorder;

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

    public override int ExecuteNonQuery() => inner.ExecuteNonQuery();

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        => inner.ExecuteNonQueryAsync(cancellationToken);

    public override object? ExecuteScalar() => inner.ExecuteScalar();

    public override void Prepare() => inner.Prepare();

    protected override DbParameter CreateDbParameter() => inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior)
    {
        _onStatement?.Invoke(inner.CommandText);
        return inner.ExecuteReader(behavior);
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        System.Data.CommandBehavior behavior, CancellationToken cancellationToken)
    {
        _onStatement?.Invoke(inner.CommandText);
        // SqliteDataReader 是 DbDataReader 的子类，但 Task<T> 不变——需显式桥接。
        return WrapAsync(inner.ExecuteReaderAsync(behavior, cancellationToken));
    }

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
