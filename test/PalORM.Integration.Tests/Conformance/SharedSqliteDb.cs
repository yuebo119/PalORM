using System.Data;
using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>共享内存库的 SQLite 会话工厂。
/// <para><b>为什么不用 TestDb.SqliteAsync()</b>：它的连接串是 <c>Data Source=:memory:</c>，
/// 每个连接各开一个私有空库，双侧对照与三分支过滤划分都要求跨会话看到同一库。
/// 本类改用唯一库名加 <c>Mode=Memory;Cache=Shared</c>，并持一条 keeper 连接保活：
/// SQLite 在最后一个连接关闭时销毁共享库。</para>
/// <para><b>建表口径</b>：<see cref="DataSession{TProvider}.MigrateAsync"/> 遍历全 Registry
/// 建所有注册实体的表，因此 keeper 会话创建时一次建齐，后续会话看到同一批表。</para></summary>
internal sealed class SharedSqliteDb : IAsyncDisposable
{
    private readonly DataSession<SqliteProvider> _keeperSession;
    private readonly string _connectionString;

    private SharedSqliteDb(DataSession<SqliteProvider> keeperSession, string connectionString)
    {
        _keeperSession = keeperSession;
        _connectionString = connectionString;
    }

    /// <summary>建库、建全部注册表并持 keeper 连接。</summary>
    /// <param name="logicalName">库名中的可读前缀，仅用于失败时识别用例。</param>
    /// <param name="ct">取消令牌。</param>
    public static async ValueTask<SharedSqliteDb> CreateAsync(
        string logicalName, CancellationToken ct = default)
    {
        string connectionString =
            $"Data Source=cf_{logicalName}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keeper = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = connectionString }, ct).ConfigureAwait(false);
        // 建表同时把 keeper 连接打开：GetRawConnection 返回的连接此后一直 Open，保活到 DisposeAsync
        await keeper.MigrateAsync(ct).ConfigureAwait(false);
        return new SharedSqliteDb(keeper, connectionString);
    }

    /// <summary>再开一个会话，与 keeper 共用同一个内存库。调用方负责释放。</summary>
    public async ValueTask<DataSession<SqliteProvider>> OpenSessionAsync(CancellationToken ct = default)
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = _connectionString }, ct).ConfigureAwait(false);

    /// <summary>建一个连到同一共享库的会话池。容量取 1 时可保证重租的是同一个实例，
    /// 用于观测"归还后瞬态状态复位"（<c>_idle</c> 是 FIFO 队列，容量大于 1 不保证同实例）。</summary>
    public DataSessionPool<SqliteProvider> CreatePool(int maxIdleSize = 1)
        => DataSessionPool<SqliteProvider>.Create(
            new DbOptions { ConnectionString = _connectionString }, maxIdleSize);

    /// <summary>裸 ADO 读软删表，按 deleted_at 是否为空划分成两个主键集合。
    /// <para><b>表名与列名必须硬编码</b>（方案 §2 原则 1）：期望值不得复用 PalORM_Runtime
    /// 或 [Column] 注解的任何值，否则 IgnoreFilters 失效时两侧同错，判据失去判别力。</para>
    /// <para>整表读回再在内存划分，而不是写 <c>WHERE deleted_at IS NULL</c>：避免手写 SQL
    /// 的过滤语义与 PalORM 的默认过滤耦合，两者一旦同错就无法区分。</para></summary>
    public async ValueTask<(HashSet<long> Alive, HashSet<long> Dead)> ReadSoftDeletePartitionAsync(
        CancellationToken ct = default)
    {
        DbConnection connection = _keeperSession.GetRawConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, deleted_at FROM cf_soft_delete";

        var alive = new HashSet<long>();
        var dead = new HashSet<long>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            object? deletedAt = reader.GetValue(1);
            if (deletedAt is null or DBNull) alive.Add(id);
            else dead.Add(id);
        }
        return (alive, dead);
    }

    /// <summary>裸 ADO 读租户表，返回（租户 → 主键集合）。表名列名同样硬编码。</summary>
    public async ValueTask<Dictionary<long, HashSet<long>>> ReadTenantPartitionAsync(
        CancellationToken ct = default)
    {
        DbConnection connection = _keeperSession.GetRawConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, tenant_id FROM cf_tenant";

        var byTenant = new Dictionary<long, HashSet<long>>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            long tenantId = reader.GetInt64(1);
            if (!byTenant.TryGetValue(tenantId, out HashSet<long>? ids))
                byTenant[tenantId] = ids = [];
            ids.Add(id);
        }
        return byTenant;
    }

    /// <summary>执行常量 SQL（手写侧专用）。
    /// <para>SQL 只允许来自测试内部的编译期常量：手写侧必须绕过 PalORM 元数据与物化器，
    /// 否则两侧同错时判据失去判别力（方案 §4.2 第三条硬约束）。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "调用方只传本测试项目内的编译期常量 SQL，不接受外部或用户输入；绕过 PalORM 元数据是判据独立性的前提")]
    public async ValueTask<int> ExecuteRawAsync(
        string constantSql,
        IReadOnlyList<(string Name, object? Value)>? parameters = null,
        CancellationToken ct = default)
    {
        DbConnection connection = await OpenKeeperConnectionAsync(ct).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = constantSql;
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>读取常量 SQL 的结果为字典列表（手写侧专用）。DBNull 归一为 null。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "调用方只传本测试项目内的编译期常量 SQL，不接受外部或用户输入；绕过 PalORM 元数据是判据独立性的前提")]
    public async ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRawAsync(
        string constantSql,
        IReadOnlyList<(string Name, object? Value)>? parameters = null,
        CancellationToken ct = default)
    {
        DbConnection connection = await OpenKeeperConnectionAsync(ct).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = constantSql;
        AddParameters(command, parameters);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.GetValue(i) is DBNull ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static void AddParameters(
        DbCommand command, IReadOnlyList<(string Name, object? Value)>? parameters)
    {
        if (parameters is null) return;
        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    private async ValueTask<DbConnection> OpenKeeperConnectionAsync(CancellationToken ct)
    {
        DbConnection connection = _keeperSession.GetRawConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        // keeper 会话关闭即销毁共享库。调用方须保证所有派生会话已先释放。
        await _keeperSession.DisposeAsync().ConfigureAwait(false);
    }
}