using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Dapper;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.PerfHub;

/// <summary>三种实现的统一操作面——PerfHub 对每个 (方言 × 实现) 组合调同一组方法。
/// 三实现测同一份数据、同一组 SQL 语义，只有"谁来生成 SQL/绑定参数/物化行"不同。</summary>
internal interface IPerfImplementation
{
    /// <summary>实现标识：ADO_NET（地板基线）/ Dapper / PalORM。</summary>
    string Name { get; }

    Task SetupAsync(DbConnection conn, int rows, CancellationToken ct);
    Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct);
    Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct);
    Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct);
    Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct);
    Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct);
    Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct);

    // ── SQL 构建（不执行，纯 ORM 开销）──
    string BuildGetByKeySql(DbConnection conn, long id);
    string BuildComplexQuerySql(DbConnection conn);

    // ── 查询类 ──
    Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct);
    Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct);
    Task<long> CountAsync(DbConnection conn, CancellationToken ct);

    // ── 阶段 2 新增（v2 方案 §2 #11/12/16/17）──
    /// <summary>批量 UPSERT（方言：ON CONFLICT / ON DUPLICATE KEY）。</summary>
    Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    /// <summary>自增插入并回填主键——三臂各 1 RTT。</summary>
    Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct);
    /// <summary>宽表（19 列）全表物化——物化器按列数伸缩。</summary>
    Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct);
    /// <summary>1:N 装配（父页 × 每父 3 子）——JOIN + 客户端装配，返回 (父数, 子总数)。</summary>
    Task<(int Parents, int Children)> IncludeJoinAsync(DbConnection conn, int parentCount, CancellationToken ct);

    // ── 事务场景（真实业务形态）──
    Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct);
    Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);

    // ── 租户会话 / OwnedJson / SessionBatch（覆盖面补齐批次）──
    /// <summary>租户会话 COUNT——[TenantAware] 实体在固定租户下的 COUNT。无显式 where：
    /// 软删过滤由产品自动附加，ADO/Dapper 臂拼 SQL 显式带同款过滤（三臂同构）。</summary>
    Task<long> TenantCountAsync(DbConnection conn, CancellationToken ct);
    /// <summary>租户会话 COUNT + Value 范围条件——阈值随档位走（rows / 2）。</summary>
    Task<long> TenantCountWhereAsync(DbConnection conn, int rows, CancellationToken ct);
    /// <summary>租户会话全表物化——含 OwnedJson 列反序列化（产品臂走 SourceGen 反序列化，
    /// 地板/Dapper 臂手工 STJ 反序列化）。</summary>
    Task<List<BenchTenantPost>> TenantGetAllAsync(DbConnection conn, CancellationToken ct);
    /// <summary>OwnedJson 列读路径——Take(50) 固定窗口（LIMIT 50 三方言同款）。</summary>
    Task<List<BenchTenantPost>> OwnedJsonQueryAsync(DbConnection conn, CancellationToken ct);
    /// <summary>SessionBatch 路径——20 条单行 INSERT 一个批操作。主键从 <paramref name="offset"/>
    /// 起（Measure 的 prepare 只在预热前与计时前各调一次，写操作在计时循环内累积，
    /// 故每轮必须用互不重叠的主键段）。</summary>
    Task<int> SessionBatchInsertsAsync(DbConnection conn, int offset, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// ADO.NET 基线——性能地板。所有比值（Ratio）以它为分母。
// 手写参数化 SQL + 手工物化，是"不靠任何 ORM 能做到的最好情况"。
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class AdoNetImpl(DialectInfo dialect) : IPerfImplementation
{
    public string Name => "ADO_NET";

    /// <summary>local_infile 探测结果缓存——PerfHub 全程共用一条连接，实例级字段即等效于
    /// 产品的按连接缓存（ConditionalWeakTable + 60s TTL 的简化形态）。不缓存则每次批量
    /// 插入都付一次额外 RTT，地板被 PalORM 反超（门禁抓出后修正）。</summary>
    private bool? _mysqlLocalInfile;

    private string T => Dataset.Table(dialect.Dialect);
    private string Q(string c) => Dataset.Q(dialect.Dialect, c);
    private string Cols => Dataset.SelectColumns(dialect.Dialect);
    private Dialect D => dialect.Dialect;
    private string TT => Dataset.TenantTable(dialect.Dialect);

    /// <summary>快照播种缓存——键 (连接, 行数)。同一组合的第二次起 prepare 走服务端
    /// DELETE + INSERT..SELECT 快照拷贝，替代客户端逐行重播（阶段 3.2）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(DbConnection Conn, int Rows), bool>
        SeedSnapshots = [];

    /// <summary>bench_tenant 快照播种缓存——键 (连接, 行数)，与 <see cref="SeedSnapshots"/>
    /// 同模式。internal 且三臂共享：Dapper 臂的 SetupAsync 委托本臂，PalORM 臂读同一缓存
    /// （同一连接同一档位的 tenant 数据只需就绪一次）。</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<(DbConnection Conn, int Rows), bool>
        TenantSeedSnapshots = [];

    /// <summary>bench_tenant 首次全量播种的公共 DDL 骨架：建主表 + 建空快照表。
    /// 灌数由各臂按自己的路径完成，随后必须调用 <see cref="TenantSeedFinishAsync"/>。</summary>
    internal static async Task TenantSeedBeginAsync(Dialect dialect, DbConnection conn, CancellationToken ct)
    {
        await ExecSetupAsync(conn, Dataset.TenantDropTableSql(dialect), ct).ConfigureAwait(false);
        await ExecSetupAsync(conn, Dataset.TenantCreateTableSql(dialect), ct).ConfigureAwait(false);
        string snapshot = Dataset.TenantSnapshotTable(dialect);
        await ExecSetupAsync(conn, $"DROP TABLE IF EXISTS {snapshot}", ct).ConfigureAwait(false);
        await ExecSetupAsync(conn, "CREATE TABLE " + snapshot + " AS SELECT * FROM "
            + Dataset.TenantTable(dialect) + " WHERE 1=0", ct).ConfigureAwait(false);
    }

    /// <summary>灌数收尾：全表拷入快照表 + 标记缓存 + PG 统计刷新（COUNT 的顺序扫描成本
    /// 正比于页数估计，机制见 <see cref="RefreshStatsAsync"/> 注释）。</summary>
    internal static async Task TenantSeedFinishAsync(
        Dialect dialect, DbConnection conn, int rows, CancellationToken ct)
    {
        await ExecSetupAsync(conn, $"INSERT INTO {Dataset.TenantSnapshotTable(dialect)} SELECT * FROM "
            + Dataset.TenantTable(dialect), ct).ConfigureAwait(false);
        TenantSeedSnapshots[(conn, rows)] = true;
        if (dialect == Dialect.PostgreSql)
        {
            await ExecSetupAsync(conn, $"ANALYZE {Dataset.TenantTable(dialect)}", ct).ConfigureAwait(false);
        }
    }

    /// <summary>缓存命中时的快照 reset——服务端两条 SQL 复原（truncate + 快照拷贝），
    /// 与 perf_s1 的 reset 完全同构；PG 随后刷新统计。</summary>
    internal static async Task TenantResetAsync(Dialect dialect, DbConnection conn, int rows, CancellationToken ct)
    {
        string table = Dataset.TenantTable(dialect);
        string truncate = dialect == Dialect.Sqlite
            ? $"DELETE FROM {table}"
            : $"TRUNCATE TABLE {table}";
        await ExecSetupAsync(conn, truncate, ct).ConfigureAwait(false);
        await ExecSetupAsync(conn, $"INSERT INTO {table} SELECT * FROM "
            + Dataset.TenantSnapshotTable(dialect) + $" WHERE {Dataset.Q(dialect, "Id")} <= {rows}",
            ct).ConfigureAwait(false);
        if (dialect == Dialect.PostgreSql)
        {
            await ExecSetupAsync(conn, $"ANALYZE {table}", ct).ConfigureAwait(false);
        }
    }

    public async Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
    {
        // bench_tenant 播种（租户会话 / OwnedJson / SessionBatch 夹具）——快照重置模式与
        // perf_s1 同构（两套缓存互不干扰，seed 表只服务 tenant 夹具）
        await TenantSetupAsync(conn, rows, ct).ConfigureAwait(false);

        string snapshot = Q("perf_s1_seed");
        if (SeedSnapshots.ContainsKey((conn, rows)))
        {
            // 快照重置：服务端两条 SQL 完成全表复原——客户端零往返逐行成本
            await TruncateAsync(D, conn, ct).ConfigureAwait(false);
            await ExecSetupAsync(conn, $"INSERT INTO {T} SELECT * FROM {snapshot} WHERE {Q("Id")} <= {rows}",
                ct).ConfigureAwait(false);
            await RefreshStatsAsync(conn, ct).ConfigureAwait(false);
            return;
        }

        await ExecSetupAsync(conn, Dataset.DropTableSql(D), ct).ConfigureAwait(false);
        await ExecSetupAsync(conn, Dataset.CreateTableSql(D), ct).ConfigureAwait(false);
        await ExecSetupAsync(conn, $"DROP TABLE IF EXISTS {snapshot}", ct).ConfigureAwait(false);
        await ExecSetupAsync(conn, "CREATE TABLE " + snapshot + " AS SELECT * FROM " + T + " WHERE 1=0",
            ct).ConfigureAwait(false);
        const int batch = 500;
        for (int start = 0; start < rows; start += batch)
        {
            int end = Math.Min(start + batch, rows);
            var sb = new StringBuilder();
            sb.Append("INSERT INTO ").Append(T).Append(" (").Append(Cols).Append(") VALUES ");
            await using DbCommand cmd = conn.CreateCommand();
            cmd.CommandTimeout = SetupCommandTimeoutSeconds;
            for (int r = start; r < end; r++)
            {
                if (r > start)
                {
                    sb.Append(", ");
                }

                sb.Append('(');
                for (int c = 0; c < 5; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(Dataset.P(((r - start) * 5) + c));
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();
            int p = 0;
            for (int r = start; r < end; r++)
            {
                S1Row row = Dataset.Seed(r);
                AddP(cmd, p++, row.Id);
                AddP(cmd, p++, row.Name);
                AddP(cmd, p++, row.Qty);
                AddP(cmd, p++, row.Price);
                AddP(cmd, p++, row.Marker);
            }
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await ExecSetupAsync(conn, $"INSERT INTO {snapshot} SELECT * FROM {T}", ct).ConfigureAwait(false);
        SeedSnapshots[(conn, rows)] = true;
        await RefreshStatsAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T} WHERE {Q("Id")} = {Dataset.P(0)}";
        AddP(cmd, 0, id);
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Map(r) : null;
    }

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T}";
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<S1Row>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Map(r));
        }

        return list;
    }

    public async Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T}";
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        long n = 0;
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            await onRow(Map(r)).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await InsertAsync(conn, row, null, ct).ConfigureAwait(false);

    /// <summary>插入单行；<paramref name="tran"/> 非 null 时命令显式挂到该事务
    /// （MySqlConnector 不接受「命令的事务不是连接的活动事务」）。</summary>
    public async Task InsertAsync(DbConnection conn, S1Row row, DbTransaction? tran, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"INSERT INTO {T} ({Cols}) VALUES ({Dataset.P(0)}, {Dataset.P(1)}, {Dataset.P(2)}, {Dataset.P(3)}, {Dataset.P(4)})";
        AddP(cmd, 0, row.Id);
        AddP(cmd, 1, row.Name);
        AddP(cmd, 2, row.Qty);
        AddP(cmd, 3, row.Price);
        AddP(cmd, 4, row.Marker);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => await BulkInsertAsync(conn, rows, null, ct).ConfigureAwait(false);

    /// <summary>批量插入——按方言走驱动最优（v2 三臂契约：地板必须是天花板）：
    /// PG 走 Npgsql Binary COPY，MySQL 走 MySqlBulkCopy（LOAD DATA LOCAL INFILE 协议），
    /// SQLite 走多值 VALUES 单命令（进程内库无协议可省，多值即最快）。</summary>
    public async Task<long> BulkInsertAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, DbTransaction? tran, CancellationToken ct)
    {
        return D switch
        {
            Dialect.Sqlite => await MultiValueInsertAsync(conn, rows, tran, ct).ConfigureAwait(false),
            Dialect.MySql => await BulkInsertMySqlAsync(conn, rows, tran, ct).ConfigureAwait(false),
            Dialect.PostgreSql => await BulkInsertCopyAsync(conn, rows, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"方言 '{D}' 不在 PerfHub 覆盖范围内。"),
        };
    }

    /// <summary>MySQL 批量插入路由——与产品 MySqlProvider 同分流：服务端 <c>@@local_infile</c>
    /// 为 ON 走 MySqlBulkCopy（LOAD DATA 协议），否则回退多值 VALUES。地板与 PalORM 臂
    /// 走同一路径才可比——只开客户端 AllowLoadLocalInfile 而服务端关着时，
    /// 驱动直接抛 "Loading local data is disabled"（实测）。</summary>
    private async Task<long> BulkInsertMySqlAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, DbTransaction? tran, CancellationToken ct)
    {
        if (_mysqlLocalInfile is { } cached)
        {
            return cached
                ? await BulkInsertMySqlCopyAsync(conn, rows, tran, ct).ConfigureAwait(false)
                : await MultiValueInsertAsync(conn, rows, tran, ct).ConfigureAwait(false);
        }

        await using DbCommand probe = conn.CreateCommand();
        probe.Transaction = tran;
        probe.CommandText = "SELECT @@local_infile";
        object? result = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false);
        _mysqlLocalInfile = Convert.ToInt64(
            result, System.Globalization.CultureInfo.InvariantCulture) == 1;
        return _mysqlLocalInfile.Value
            ? await BulkInsertMySqlCopyAsync(conn, rows, tran, ct).ConfigureAwait(false)
            : await MultiValueInsertAsync(conn, rows, tran, ct).ConfigureAwait(false);
    }

    /// <summary>PG Binary COPY——与产品 PostgreSqlProvider 同路径；显式事务（TxBulkInsert）
    /// 由调用方持有时，COPY 自动参与该连接上的事务。</summary>
    private async Task<long> BulkInsertCopyAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        // 驱动专有快路径要求自己的连接类型——先脱掉计数装饰器（该路径的往返数不计入维度 8）
        Npgsql.NpgsqlConnection npg = (Npgsql.NpgsqlConnection)CountingConnection.Unwrap(conn);
        await using Npgsql.NpgsqlBinaryImporter importer = await npg.BeginBinaryImportAsync(
            $"COPY {T} ({Cols}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false);
        foreach (S1Row row in rows)
        {
            await importer.StartRowAsync(ct).ConfigureAwait(false);
            await importer.WriteAsync(row.Id, NpgsqlTypes.NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
            await importer.WriteAsync(row.Name, NpgsqlTypes.NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await importer.WriteAsync(row.Qty, NpgsqlTypes.NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            await importer.WriteAsync(row.Price, NpgsqlTypes.NpgsqlDbType.Numeric, ct).ConfigureAwait(false);
            await importer.WriteAsync(row.Marker, NpgsqlTypes.NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
        }
        return (long)await importer.CompleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>MySQL MySqlBulkCopy——LOAD DATA LOCAL INFILE 协议（连接串已统一追加
    /// AllowLoadLocalInfile）。未传事务时自开事务包整批（与产品同语义）；
    /// Warnings 非空即显式失败（驱动文档要求检查，否则类型截断会静默丢数据）。</summary>
    private async Task<long> BulkInsertMySqlCopyAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, DbTransaction? tran, CancellationToken ct)
    {
        MySqlConnector.MySqlTransaction? myTran =
            tran as MySqlConnector.MySqlTransaction;
        bool ownsTransaction = false;
        if (myTran is null)
        {
            myTran = (MySqlConnector.MySqlTransaction)await conn
                .BeginTransactionAsync(ct).ConfigureAwait(false);
            ownsTransaction = true;
        }

        try
        {
            // 同 PG：MySqlBulkCopy 要求真实 MySqlConnection，先脱装饰器（否则连接被搞坏，
            // 后续项全部报 Connection must be Open; state is Broken——实测先例）
            MySqlConnector.MySqlBulkCopy bulk = new(
                (MySqlConnector.MySqlConnection)CountingConnection.Unwrap(conn), myTran)
            {
                DestinationTableName = T,
            };
            string[] columns = ["Id", "Name", "Qty", "Price", "Marker"];
            for (int i = 0; i < columns.Length; i++)
            {
                // 不指定 ColumnMappings 时驱动按序号匹配目标表——显式映射防列序漂移（ITM-615）
                bulk.ColumnMappings.Add(new MySqlConnector.MySqlBulkCopyColumnMapping(i, columns[i]));
            }

            // 喂数据走读取器而不是 DataTable：与产品 v5.6 后的形态一致（DataTable 每行 372 B、
            // 且在本环境实测把连接置为 Broken，后续 BulkDelete 报 SocketException 995）
            using var reader = new S1RowDataReader(rows);
            MySqlConnector.MySqlBulkCopyResult result =
                await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
            if (result.Warnings.Count > 0)
                throw new InvalidOperationException(
                    $"MySqlBulkCopy produced {result.Warnings.Count} warnings");
            long inserted = result.RowsInserted;
            if (ownsTransaction)
                await myTran.CommitAsync(ct).ConfigureAwait(false);
            return inserted;
        }
        finally
        {
            if (ownsTransaction)
                await myTran.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>多值 INSERT——天花板写法，与产品 MultiValueBulkInsert 同语义：
    /// ① 命令与参数池一次建好，跨批只写 Value；② <b>整批裹一个事务</b>（未传事务时自开）
    /// ——每语句 autocommit 各一次 journal 同步，是量具早期实测被 PalORM 反超 6× 的直接原因
    /// （门禁抓出后修正）。</summary>
    private async Task<long> MultiValueInsertAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, DbTransaction? tran, CancellationToken ct)
    {
        bool ownsTransaction = tran is null;
        DbTransaction tx = tran ?? await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        long total;
        try
        {
            total = await ExecuteMultiValueInsertCoreAsync(conn, rows, tx, ct).ConfigureAwait(false);
            if (ownsTransaction)
                await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (ownsTransaction)
                await tx.DisposeAsync().ConfigureAwait(false);
        }

        return total;
    }

    private async Task<long> ExecuteMultiValueInsertCoreAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, DbTransaction tx, CancellationToken ct)
    {
        long total = 0;
        // SQLite 批宽对齐产品口径：min(1000, 999 参数上限 ÷ 5 列) = 199 行/语句
        int batch = BulkSql.EffectiveBatchRows(D);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        int lastBatchLength = -1;
        DbParameter[]? insertPool = null;
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            int len = end - start;
            if (len != lastBatchLength)
            {
                cmd.CommandText = "INSERT INTO " + T + " (" + Cols + ") VALUES "
                    + BulkSql.ValuesRows(len);
                lastBatchLength = len;
                cmd.Parameters.Clear();
                for (int r = start; r < end; r++)
                {
                    S1Row row = rows[r];
                    AddP(cmd, (r - start) * 5, row.Id);
                    AddP(cmd, ((r - start) * 5) + 1, row.Name);
                    AddP(cmd, ((r - start) * 5) + 2, row.Qty);
                    AddP(cmd, ((r - start) * 5) + 3, row.Price);
                    AddP(cmd, ((r - start) * 5) + 4, row.Marker);
                }
                insertPool = SnapshotParameters(cmd);
            }
            else if (insertPool is not null)
            {
                for (int r = start; r < end; r++)
                {
                    S1Row row = rows[r];
                    SetP(insertPool, (r - start) * 5, row.Id);
                    SetP(insertPool, ((r - start) * 5) + 1, row.Name);
                    SetP(insertPool, ((r - start) * 5) + 2, row.Qty);
                    SetP(insertPool, ((r - start) * 5) + 3, row.Price);
                    SetP(insertPool, ((r - start) * 5) + 4, row.Marker);
                }
            }

            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await UpdateAsync(conn, row, null, ct).ConfigureAwait(false);

    /// <summary>更新单行；<paramref name="tran"/> 非 null 时命令显式挂到该事务。</summary>
    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, DbTransaction? tran, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"UPDATE {T} SET {Q("Name")} = {Dataset.P(0)}, {Q("Qty")} = {Dataset.P(1)}, "
            + $"{Q("Price")} = {Dataset.P(2)}, {Q("Marker")} = {Dataset.P(3)} WHERE {Q("Id")} = {Dataset.P(4)}";
        AddP(cmd, 0, row.Name);
        AddP(cmd, 1, row.Qty);
        AddP(cmd, 2, row.Price);
        AddP(cmd, 3, row.Marker);
        AddP(cmd, 4, row.Id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => await BulkUpdateAsync(conn, rows, null, ct).ConfigureAwait(false);

    /// <summary>批量更新——方言最优（v2 三臂契约）：PG 发 UPDATE FROM VALUES、
    /// MySQL 发 CASE WHEN（均单语句单往返，SQL 与产品 BatchUpdateSqlBuilder 同构）；
    /// SQLite 逐条但必须裹单事务（autocommit 每行一次 journal 同步，差一个数量级）。</summary>
    public async Task<long> BulkUpdateAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, DbTransaction? tran, CancellationToken ct)
    {
        if (D == Dialect.Sqlite)
            return await BulkUpdateRowByRowAsync(conn, rows, tran, ct).ConfigureAwait(false);

        long total = 0;
        const int batch = BulkSql.BatchRows;
        int lastBatchLength = -1;
        string sql = "";
        DbParameter[]? updatePool = null;
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            int len = end - start;
            if (len != lastBatchLength)
            {
                sql = BulkSql.UpdateBatch(D, len);
                lastBatchLength = len;
                cmd.Parameters.Clear();
                int p = 0;
                for (int r = start; r < end; r++)
                {
                    S1Row row = rows[r];
                    AddP(cmd, p++, row.Name);
                    AddP(cmd, p++, row.Qty);
                    AddP(cmd, p++, row.Price);
                    AddP(cmd, p++, row.Marker);
                    AddP(cmd, p++, row.Id);
                }
                updatePool = SnapshotParameters(cmd);
            }
            else if (updatePool is not null)
            {
                int p = 0;
                for (int r = start; r < end; r++)
                {
                    S1Row row = rows[r];
                    SetP(updatePool, p++, row.Name);
                    SetP(updatePool, p++, row.Qty);
                    SetP(updatePool, p++, row.Price);
                    SetP(updatePool, p++, row.Marker);
                    SetP(updatePool, p++, row.Id);
                }
            }

            cmd.CommandText = sql;
            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>SQLite 批量更新——逐条裹单事务（该方言实测最优：CASE WHEN 慢 6.4×）。
    /// 天花板写法：单命令跨行复用，每行只写参数 Value——逐行 CreateCommand/重加参数
    /// 是被 Dapper 反超的直接原因（门禁抓出后修正）。未传事务时自开，提交一次。</summary>
    private async Task<long> BulkUpdateRowByRowAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, DbTransaction? tran, CancellationToken ct)
    {
        bool ownsTransaction = tran is null;
        DbTransaction tx = tran ?? await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE {T} SET {Q("Name")} = {Dataset.P(0)}, {Q("Qty")} = {Dataset.P(1)}, "
                + $"{Q("Price")} = {Dataset.P(2)}, {Q("Marker")} = {Dataset.P(3)} WHERE {Q("Id")} = {Dataset.P(4)}";
            AddP(cmd, 0, default(string));
            AddP(cmd, 1, 0);
            AddP(cmd, 2, 0m);
            AddP(cmd, 3, 0L);
            AddP(cmd, 4, 0L);
            DbParameter[] rowPool = SnapshotParameters(cmd);
            long total = 0;
            foreach (S1Row row in rows)
            {
                SetP(rowPool, 0, row.Name);
                SetP(rowPool, 1, row.Qty);
                SetP(rowPool, 2, row.Price);
                SetP(rowPool, 3, row.Marker);
                SetP(rowPool, 4, row.Id);
                total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (ownsTransaction)
                await tx.CommitAsync(ct).ConfigureAwait(false);
            return total;
        }
        finally
        {
            if (ownsTransaction)
                await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>刷新 PG 的计划器统计——**只对 PG 生效**。
    /// <para><b>为什么必须有</b>：夹具会把行数改变一个数量级再改回来。实测（2026-09-23）：
    /// BulkDelete 在 tier 20000 档把表播到 200,000 行，期间的 autovacuum/ANALYZE 把
    /// reltuples 记成约 20 万；之后查询组 reset 回 20,000 行却**不刷新统计**，于是
    /// <c>SELECT COUNT(*)</c> 拿到 7 倍高估的行数估计，计划器选 Parallel Seq Scan（2 workers），
    /// Gather/worker 开销约 110 ms 峰值（实测 28.85 ms）压倒本应 1 ms 的串行扫描。
    /// 同一张表、同一条 SQL、同一条连接，只补一次 ANALYZE 就回到 1.62 ms。</para>
    /// <para><b>影响面不止 Count</b>：任何被计划器选成顺序扫描的项都会中招；此前的表征是
    /// <c>Count/PostgreSQL/t20000</c> 的比值在批次间翻转 20 倍、且间歇性。</para>
    /// <para><b>为什么只有 PG</b>：MySQL 的 InnoDB 持久统计由 innodb_stats_auto_recalc 自动维护
    /// （实测 MySQL 同项比值稳定在 0.97~1.06），SQLite 无基于行数估计的并行扫描决策。
    /// 这是**播种步骤**、不计入任何测量，因此不构成 §4.1 意义上的臂间口径差异。</para></summary>
    private async Task RefreshStatsAsync(DbConnection conn, CancellationToken ct)
    {
        if (D != Dialect.PostgreSql)
        {
            return;
        }

        await ExecSetupAsync(conn, $"ANALYZE {T}", ct).ConfigureAwait(false);
    }
    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
        => await BulkDeleteAsync(conn, keys, null, ct).ConfigureAwait(false);

    /// <summary>IN 分批删除，整批裹一个事务（与产品 BulkDeleteAsync 同语义——
    /// SQLite 上逐批 autocommit 每批一次 journal 同步，裹事务是数量级差异）。</summary>
    public async Task<long> BulkDeleteAsync(
        DbConnection conn, IReadOnlyList<object> keys, DbTransaction? tran, CancellationToken ct)
    {
        bool ownsTransaction = tran is null;
        DbTransaction tx = tran ?? await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            long total = await ExecuteBulkDeleteCoreAsync(conn, keys, tx, ct).ConfigureAwait(false);
            if (ownsTransaction)
                await tx.CommitAsync(ct).ConfigureAwait(false);
            return total;
        }
        finally
        {
            if (ownsTransaction)
                await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<long> ExecuteBulkDeleteCoreAsync(
        DbConnection conn, IReadOnlyList<object> keys, DbTransaction tx, CancellationToken ct)
    {
        long total = 0;
        const int batch = BulkSql.BatchRows;
        for (int start = 0; start < keys.Count; start += batch)
        {
            int end = Math.Min(start + batch, keys.Count);
            var sb = new StringBuilder();
            sb.Append("DELETE FROM ").Append(T).Append(" WHERE ").Append(Q("Id")).Append(" IN (");
            await using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            for (int i = start; i < end; i++)
            {
                if (i > start)
                {
                    sb.Append(", ");
                }

                sb.Append(Dataset.P(i - start));
            }
            sb.Append(')');
            cmd.CommandText = sb.ToString();
            for (int i = start; i < end; i++)
            {
                AddP(cmd, i - start, keys[i]);
            }

            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => $"SELECT {Cols} FROM {T} WHERE {Q("Id")} = {Dataset.P(0)}";

    public string BuildComplexQuerySql(DbConnection conn)
        => $"SELECT {Cols} FROM {T} WHERE ({Q("Qty")} > {Dataset.P(0)} AND {Q("Marker")} < {Dataset.P(1)}) "
         + $"OR {Q("Name")} LIKE {Dataset.P(2)} ORDER BY {Q("Marker")} DESC LIMIT 100";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T} WHERE {Q("Id")} > {Dataset.P(0)} ORDER BY {Q("Id")} LIMIT {pageSize}";
        AddP(cmd, 0, lastId);
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<S1Row>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Map(r));
        }

        return list;
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"SELECT {Cols} FROM {T} WHERE {Q("Id")} IN (");
        await using DbCommand cmd = conn.CreateCommand();
        for (int i = 0; i < ids.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(Dataset.P(i));
        }
        sb.Append(')');
        cmd.CommandText = sb.ToString();
        for (int i = 0; i < ids.Length; i++)
        {
            AddP(cmd, i, ids[i]);
        }

        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<S1Row>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Map(r));
        }

        return list;
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {T}";
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── 租户会话 / OwnedJson / SessionBatch ──

    /// <summary>bench_tenant 播种——首次全量（多值 INSERT 分批 + 建快照表），后续走
    /// 服务端快照 reset（与 perf_s1 的 SetupAsync 完全同构，只是列集换成 7 列）。</summary>
    private async Task TenantSetupAsync(DbConnection conn, int rows, CancellationToken ct)
    {
        if (TenantSeedSnapshots.ContainsKey((conn, rows)))
        {
            await TenantResetAsync(D, conn, rows, ct).ConfigureAwait(false);
            return;
        }

        await TenantSeedBeginAsync(D, conn, ct).ConfigureAwait(false);
        const int batch = 100;   // 7 列——SQLite 999 参数上限内的保守批宽（播种非测量路径）
        List<BenchTenantPost> seed = Dataset.TenantSeedRows(rows);
        for (int start = 0; start < seed.Count; start += batch)
        {
            int end = Math.Min(start + batch, seed.Count);
            var sb = new StringBuilder();
            sb.Append("INSERT INTO ").Append(TT).Append(" (")
                .Append(Dataset.TenantSelectColumns(D)).Append(") VALUES ");
            await using DbCommand cmd = conn.CreateCommand();
            cmd.CommandTimeout = SetupCommandTimeoutSeconds;
            for (int r = start; r < end; r++)
            {
                if (r > start)
                {
                    sb.Append(", ");
                }

                sb.Append('(');
                for (int c = 0; c < 7; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(Dataset.P(((r - start) * 7) + c));
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();
            int p = 0;
            for (int r = start; r < end; r++)
            {
                BenchTenantPost row = seed[r];
                AddP(cmd, p++, row.Id);
                AddP(cmd, p++, row.TenantId);
                AddP(cmd, p++, row.Text);
                AddP(cmd, p++, row.CreationDate);
                AddP(cmd, p++, row.Value);
                AddP(cmd, p++, row.DeletedAt);
                AddP(cmd, p++, Dataset.TenantPayloadJson(row));
            }
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await TenantSeedFinishAsync(D, conn, rows, ct).ConfigureAwait(false);
    }

    public async Task<long> TenantCountAsync(DbConnection conn, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {TT} {Dataset.WhereTenant(D)}";
        AddNamedP(cmd, Dataset.TenantParameter, Dataset.TenantArmId);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<long> TenantCountWhereAsync(DbConnection conn, int rows, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {TT} {Dataset.WhereTenantValue(D)}";
        AddNamedP(cmd, Dataset.TenantParameter, Dataset.TenantArmId);
        AddNamedP(cmd, Dataset.P(1), Dataset.TenantValueThreshold(rows));
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<List<BenchTenantPost>> TenantGetAllAsync(DbConnection conn, CancellationToken ct)
        => TenantQueryAsync(conn, "", ct);

    /// <summary>OwnedJson 读路径——Take(50) 固定窗口（LIMIT 50 三方言同款，与产品的
    /// From&lt;T&gt;().Take(50) 生成的 SQL 同构）。</summary>
    public Task<List<BenchTenantPost>> OwnedJsonQueryAsync(DbConnection conn, CancellationToken ct)
        => TenantQueryAsync(conn, " LIMIT 50", ct);

    /// <summary>租户会话读——产品臂在 WithTenant 下自动附加同款过滤（deleted_at IS NULL
    /// AND tenant_id = @__tenant0），地板臂显式拼 <c>Dataset.WhereTenant</c> 同构 SQL；
    /// limit 段由调用方给（空 = 全表）。Payload 列读 string 后经 BenchTenantJsonContext
    /// 手工反序列化（STJ 源生成路径，与产品 OwnedJson emit 同形态）。</summary>
    private async Task<List<BenchTenantPost>> TenantQueryAsync(
        DbConnection conn, string limit, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Dataset.TenantSelectColumns(D)} FROM {TT} {Dataset.WhereTenant(D)}{limit}";
        AddNamedP(cmd, Dataset.TenantParameter, Dataset.TenantArmId);
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<BenchTenantPost>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(MapTenant(r));
        }

        return list;
    }

    /// <summary>SessionBatch 路径——20 条单行 INSERT 一个 DbBatch（PG/MySQL 驱动的真
    /// 单往返批）。主键从 <paramref name="offset"/> 起，避开既有行与各轮已插段
    /// （Measure 的 prepare 不在每轮前调，写操作在计时循环内累积）。
    /// <para>SQLite 分叉：Microsoft.Data.Sqlite 未覆写 CreateBatch（基类抛
    /// NotSupportedException），回退为单命令复用逐条 ExecuteNonQuery——与产品 SessionBatch
    /// 的回退路径（L37）同构。</para></summary>
    public async Task<int> SessionBatchInsertsAsync(DbConnection conn, int offset, CancellationToken ct)
    {
        if (D == Dialect.Sqlite)
        {
            return await SessionBatchSequentialAsync(conn, offset, ct).ConfigureAwait(false);
        }

        await using DbBatch batch = conn.CreateBatch();
        using DbCommand factory = conn.CreateCommand();
        string sql = Dataset.TenantInsertSql(D);
        for (int k = 0; k < Dataset.SessionBatchRows; k++)
        {
            BenchTenantPost row = Dataset.TenantSeed(offset + k);
            DbBatchCommand command = batch.CreateBatchCommand();
            command.CommandText = sql;
            AddBatchP(factory, command, 0, row.Id);
            AddBatchP(factory, command, 1, row.TenantId);
            AddBatchP(factory, command, 2, row.Text);
            AddBatchP(factory, command, 3, row.CreationDate);
            AddBatchP(factory, command, 4, row.Value);
            AddBatchP(factory, command, 5, row.DeletedAt);
            AddBatchP(factory, command, 6, Dataset.TenantPayloadJson(row));
            batch.BatchCommands.Add(command);
        }

        return await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>DbBatchCommand 的参数添加——DbBatchCommand 无 CreateParameter 面，
    /// 参数对象从连接的普通命令工厂创建（驱动同型，可加入 batch.Parameters 集合）。</summary>
    private static void AddBatchP(
        DbCommand factory, DbBatchCommand command, int index, object? value)
    {
        DbParameter p = factory.CreateParameter();
        p.ParameterName = Dataset.P(index);
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    /// <summary>SQLite 的批路径回退——单命令跨行复用、每行只写参数 Value（与
    /// <see cref="BulkUpdateRowByRowAsync"/> 同模式）。</summary>
    private async Task<int> SessionBatchSequentialAsync(
        DbConnection conn, int offset, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = Dataset.TenantInsertSql(D);
        DbParameter[]? pool = null;
        int total = 0;
        for (int k = 0; k < Dataset.SessionBatchRows; k++)
        {
            BenchTenantPost row = Dataset.TenantSeed(offset + k);
            if (pool is null)
            {
                AddP(cmd, 0, row.Id);
                AddP(cmd, 1, row.TenantId);
                AddP(cmd, 2, row.Text);
                AddP(cmd, 3, row.CreationDate);
                AddP(cmd, 4, row.Value);
                AddP(cmd, 5, row.DeletedAt);
                AddP(cmd, 6, Dataset.TenantPayloadJson(row));
                pool = SnapshotParameters(cmd);
            }
            else
            {
                SetP(pool, 0, row.Id);
                SetP(pool, 1, row.TenantId);
                SetP(pool, 2, row.Text);
                SetP(pool, 3, row.CreationDate);
                SetP(pool, 4, row.Value);
                SetP(pool, 5, row.DeletedAt);
                SetP(pool, 6, Dataset.TenantPayloadJson(row));
            }

            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>bench_tenant 行物化——Payload 列按 OwnedJson 契约手工反序列化，
    /// 走 BenchTenantJsonContext 的 STJ 源生成路径（与产品 SourceGen OwnedJson emit 的
    /// 反序列化形态同源，零反射）。</summary>
    internal static BenchTenantPost MapTenant(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        TenantId = r.GetInt64(1),
        Text = r.IsDBNull(2) ? null : r.GetString(2),
        CreationDate = r.GetDateTime(3),
        Value = r.GetInt32(4),
        DeletedAt = r.IsDBNull(5) ? null : r.GetString(5),
        Payload = r.IsDBNull(6) ? null : JsonSerializer.Deserialize(
            r.GetString(6), BenchTenantJsonContext.Default.BenchTenantPayloadInfo)
    };

    /// <summary>具名参数添加——租户过滤参数 <see cref="Dataset.TenantParameter"/> 是产品同款
    /// 约定名（不是 <see cref="AddP"/> 的 @pN 序号名），DbType 显式化口径同 AddP。</summary>
    private static void AddNamedP(DbCommand cmd, string name, object? value)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        switch (value)
        {
            case long: p.DbType = System.Data.DbType.Int64; break;
            case int: p.DbType = System.Data.DbType.Int32; break;
            case string: p.DbType = System.Data.DbType.String; break;
            default: break;
        }

        cmd.Parameters.Add(p);
    }

    // ── 阶段 2 新增 ──

    /// <summary>批量 UPSERT——共用 BulkSql.UpsertBatch（与产品 BuildUpsertSqlShape 同形态），
    /// 参数池数组直写，整批裹事务。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        DbTransaction tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            long total = 0;
            int batch = BulkSql.EffectiveBatchRows(D);
            await using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            int lastBatchLength = -1;
            DbParameter[]? pool = null;
            for (int start = 0; start < rows.Count; start += batch)
            {
                int end = Math.Min(start + batch, rows.Count);
                int len = end - start;
                if (len != lastBatchLength)
                {
                    cmd.CommandText = BulkSql.UpsertBatch(D, len);
                    lastBatchLength = len;
                    cmd.Parameters.Clear();
                    for (int r = start; r < end; r++)
                    {
                        S1Row row = rows[r];
                        AddP(cmd, (r - start) * 5, row.Id);
                        AddP(cmd, ((r - start) * 5) + 1, row.Name);
                        AddP(cmd, ((r - start) * 5) + 2, row.Qty);
                        AddP(cmd, ((r - start) * 5) + 3, row.Price);
                        AddP(cmd, ((r - start) * 5) + 4, row.Marker);
                    }
                    pool = SnapshotParameters(cmd);
                }
                else if (pool is not null)
                {
                    for (int r = start; r < end; r++)
                    {
                        S1Row row = rows[r];
                        SetP(pool, (r - start) * 5, row.Id);
                        SetP(pool, ((r - start) * 5) + 1, row.Name);
                        SetP(pool, ((r - start) * 5) + 2, row.Qty);
                        SetP(pool, ((r - start) * 5) + 3, row.Price);
                        SetP(pool, ((r - start) * 5) + 4, row.Marker);
                    }
                }

                // MySQL ON DUPLICATE KEY 的 affectedRows 对更新行计 2——此处取驱动原始口径
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                total += len;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return total;
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>自增插入并回填主键——PG/SQLite 走 RETURNING 单 RTT，
    /// MySQL 走 INSERT;SELECT LAST_INSERT_ID() 合并单命令（同为 1 RTT）。</summary>
    public async Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = D == Dialect.MySql
            ? $"INSERT INTO {Dataset.AutoIncTable(D)} ({Q("Name")}, {Q("Qty")}) "
                + $"VALUES ({Dataset.P(0)}, {Dataset.P(1)}); SELECT LAST_INSERT_ID();"
            : $"INSERT INTO {Dataset.AutoIncTable(D)} ({Q("Name")}, {Q("Qty")}) "
                + $"VALUES ({Dataset.P(0)}, {Dataset.P(1)}) RETURNING {Q("Id")}";
        AddP(cmd, 0, name);
        AddP(cmd, 1, qty);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>宽表全表物化——19 列按序号取（按名取列是慢路径，S1 测不出的列数伸缩在此放大）。</summary>
    public async Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Dataset.WideSelectColumns(D)} FROM {Dataset.WideTable(D)}";
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        int n = 0;
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            _ = MapWide(r);
            n++;
        }
        return n;
    }

    /// <summary>1:N 装配——JOIN 单查询 + 客户端按父去重分组（无行放大的手工天花板形态）。</summary>
    public async Task<(int Parents, int Children)> IncludeJoinAsync(
        DbConnection conn, int parentCount, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols}, {Q("ChildId")}, {Q("Note")} FROM {T} "
            + $"INNER JOIN {Dataset.ChildTable(D)} ON {Dataset.ChildTable(D)}.{Q("ParentId")} = {T}.{Q("Id")} "
            + $"WHERE {T}.{Q("Id")} <= {Dataset.P(0)} ORDER BY {T}.{Q("Id")}";
        AddP(cmd, 0, (long)parentCount);
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        long lastParent = 0;
        int parents = 0, children = 0;
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            long id = r.GetInt64(0);
            if (id != lastParent)
            {
                parents++;
                lastParent = id;
            }
            _ = r.GetString(6);
            children++;
        }
        return (parents, children);
    }

    /// <summary>宽表行物化——19 列全读（测量物化成本，不物化成实体会被优化掉）。</summary>
    internal static WideRow MapWide(DbDataReader r) => new()
    {
        Id = r.GetInt64(0),
        C01 = r.GetInt64(1),
        C02 = r.GetInt32(2),
        C03 = r.GetInt16(3),
        C04 = r.GetString(4),
        C05 = r.GetBoolean(5),
        C06 = r.GetDecimal(6),
        C07 = r.GetDouble(7),
        C08 = r.GetFloat(8),
        C09 = r.GetDateTime(9),
        C10 = r.GetGuid(10),
        C11 = r.IsDBNull(11) ? null : r.GetInt32(11),
        C12 = r.IsDBNull(12) ? null : r.GetInt64(12),
        C13 = r.IsDBNull(13) ? null : r.GetString(13),
        C14 = r.IsDBNull(14) ? null : r.GetDecimal(14),
        C15 = r.IsDBNull(15) ? null : r.GetBoolean(15),
        C16 = r.GetByte(16),
        C17 = r.IsDBNull(17) ? null : r.GetInt16(17),
        C18 = r.IsDBNull(18) ? null : r.GetDouble(18),
        C19 = r.GetDateTime(19)
    };

    // ── 事务场景 ──
    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await InsertAsync(conn, row, tran, ct).ConfigureAwait(false);
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"INSERT INTO {T} ({Cols}) VALUES ({Dataset.P(0)}, {Dataset.P(1)}, {Dataset.P(2)}, {Dataset.P(3)}, {Dataset.P(4)})";
        foreach (S1Row row in rows)
        {
            cmd.Parameters.Clear();
            AddP(cmd, 0, row.Id);
            AddP(cmd, 1, row.Name);
            AddP(cmd, 2, row.Qty);
            AddP(cmd, 3, row.Price);
            AddP(cmd, 4, row.Marker);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>事务内 100 条逐条插入——与 <see cref="TxTenInsertsAsync"/> 同形态不同规模，
    /// 用独立实现以便在报告里分开登记（10 条 vs 100 条的每语句摊销差异正是要测的）。</summary>
    public Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => TxTenInsertsAsync(conn, rows, ct);

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await BulkInsertAsync(conn, rows, tran, ct).ConfigureAwait(false);
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"UPDATE {T} SET {Q("Qty")} = {Dataset.P(0)} WHERE {Q("Id")} = {Dataset.P(1)}";
        foreach (S1Row row in rows)
        {
            cmd.Parameters.Clear();
            AddP(cmd, 0, row.Qty + 1);
            AddP(cmd, 1, row.Id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tran.RollbackAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量路径的参数复用——命令参数池一次建好，逐批只写 Value（零新参数对象）。</summary>
    /// <summary>批量路径的参数复用——参数池数组直写（对齐产品的 CreateParameterArray +
    /// valuesBinder 模式）。不走 cmd.Parameters[index] 索引器：每次索引都是一次集合查找
    /// 与校验，批量路径每行 5 次 × 数千行会放大成可测差距（门禁抓出后修正）。</summary>
    private static void SetP(DbParameter[] pool, int index, object? value)
    {
        pool[index].Value = value ?? DBNull.Value;
    }

    /// <summary>命令当前参数快照为数组——批量路径的直写池。</summary>
    private static DbParameter[] SnapshotParameters(DbCommand cmd)
    {
        // 不用 CopyTo：SqliteParameterCollection.CopyTo 只接受 SqliteParameter[]，
        // DbParameter[] 会 InvalidCastException——逐项索引赋值三驱动通吃
        DbParameter[] pool = new DbParameter[cmd.Parameters.Count];
        for (int i = 0; i < pool.Length; i++)
        {
            pool[i] = cmd.Parameters[i];
        }

        return pool;
    }

    private static void AddP(DbCommand cmd, int index, object? value)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = Dataset.P(index);
        p.Value = value ?? DBNull.Value;
        // 显式 DbType：Npgsql 对 object 装箱的 long/int 可能推断为 text
        switch (value)
        {
            case long: p.DbType = System.Data.DbType.Int64; break;
            case int: p.DbType = System.Data.DbType.Int32; break;
            case decimal: p.DbType = System.Data.DbType.Decimal; break;
            case bool: p.DbType = System.Data.DbType.Boolean; break;
            case string: p.DbType = System.Data.DbType.String; break;
            default: break;
        }
        cmd.Parameters.Add(p);
    }

    /// <summary>播种/重置类语句的命令超时（秒）——**必须显式给**，否则用驱动默认 30 秒。
    /// <para>实测（2026-09-23，远程 MySQL 8.4，逐段探针 `.ai/perf-probe/MySqlSeedDiag.cs`）：
    /// 100 万行的 `INSERT INTO perf_s1_seed SELECT * FROM perf_s1` 单命令 94.6 s、
    /// `DELETE FROM perf_s1` 107.3 s、快照重置 114.0 s——**三条都超过 30 秒默认值**，
    /// MySqlConnector 中止 socket（SocketException 995）并把连接置为 Broken，
    /// 整个 MySQL 方言就此失败。播种不是被测操作（不计入任何指标），
    /// 不该因默认超时把方言打断；被测命令仍用驱动默认，保持"挂住就快速失败"。</para></summary>
    internal const int SetupCommandTimeoutSeconds = 600;

    /// <summary>播种类语句的执行——显式设 <see cref="SetupCommandTimeoutSeconds"/>。</summary>
    internal static async Task ExecSetupAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandTimeout = SetupCommandTimeoutSeconds;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>清空表——MySQL/PG 用 <c>TRUNCATE</c>，SQLite 用 <c>DELETE</c>（无 TRUNCATE 语句）。
    /// <para><b>MySQL 侧</b>：InnoDB 逐行删，实测删 100 万行要 107.3 s，TRUNCATE 是元数据操作、近瞬时。</para>
    /// <para><b>PG 侧（影响更大）</b>：DELETE 只标记死元组、**不回收页面**，于是表的物理体积由
    /// "历史上最大的那次播种"决定并跨臂单调膨胀。实测 2026-09-23：同一张 <c>perf_s1</c> 在三臂的
    /// <c>pg_relation_size</c> 是 15 MB → 76 MB → 139 KB（末臂小只因 autovacuum 恰好截断了它）。
    /// 而 <c>SELECT COUNT(*)</c> 的顺序扫描成本正比于页数，三臂因此差约 500 倍
    /// （1841 / 9306 / 17 个 buffer）。改用 TRUNCATE 后每轮恒定 0.13 MB、COUNT 恒定 0.56~0.64 ms
    /// （探针对照：DELETE 重置恒 7.00 MB / ~1.0 ms）。</para>
    /// <para>语义等价：两者都清空且保留表结构，且重置路径随后立即重新灌入确定内容；这是**播种步骤**、
    /// 不计入任何测量，不构成 §4.1 意义上的臂间口径差异。</para></summary>
    internal static async Task TruncateAsync(Dialect dialect, DbConnection conn, CancellationToken ct)
    {
        string sql = dialect == Dialect.Sqlite
            ? $"DELETE FROM {Dataset.Table(dialect)}"
            : $"TRUNCATE TABLE {Dataset.Table(dialect)}";
        await ExecSetupAsync(conn, sql, ct).ConfigureAwait(false);
    }

    internal static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static S1Row Map(DbDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Qty = r.GetInt32(2),
        Price = r.GetDecimal(3),
        Marker = r.GetInt64(4)
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Dapper——主流 micro-ORM 对照臂
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class DapperImpl(DialectInfo dialect) : IPerfImplementation
{
    public string Name => "Dapper";

    static DapperImpl()
    {
        // SQLite 把 Guid 存 TEXT、MySQL CHAR(36) 返回 string——Dapper 默认不做 string→Guid
        // 转换，注册 TypeHandler 是 Dapper 用户在方言库上的标准做法（真实用法的一部分）
        SqlMapper.AddTypeHandler(new StringToGuidHandler());
        // bench_tenant 的 int/DateTime 列同型：SQLite 的 INTEGER 读回 long、TEXT 时间列读回
        // string（PG/MySQL 的对应类型原生是 int/DateTime），TypeHandler 的三方言双分支
        // 统一两套 reader 形态——与 Guid handler 同范式
        SqlMapper.AddTypeHandler(new Int32FromInt64Handler());
        SqlMapper.AddTypeHandler(new StringToDateTimeHandler());
    }

    /// <summary>long → int 的 Dapper 类型处理器——覆盖 SQLite INTEGER（读回 long）与
    /// PG/MySQL INTEGER/INT（原生 int）两种 reader 形态。</summary>
    private sealed class Int32FromInt64Handler : SqlMapper.TypeHandler<int>
    {
        public override int Parse(object value)
            => value switch
            {
                int i => i,
                long l => (int)l,
                _ => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
            };

        public override void SetValue(System.Data.IDbDataParameter parameter, int value)
            => parameter.Value = value;
    }

    /// <summary>string ↔ DateTime 的 Dapper 类型处理器——覆盖 SQLite TEXT 时间列与
    /// PG TIMESTAMP/MySQL DATETIME 原生 DateTime 两种形态（固定格式，不用当前区域性）。</summary>
    private sealed class StringToDateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override DateTime Parse(object value)
            => value switch
            {
                DateTime dt => dt,
                string s => DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
                _ => Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture),
            };

        public override void SetValue(System.Data.IDbDataParameter parameter, DateTime value)
            => parameter.Value = value.ToString(
                "O", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>string ↔ Guid 的 Dapper 类型处理器——覆盖 SQLite TEXT 与 MySQL CHAR(36)
    /// 两种存储形态；PG UUID 由 Npgsql 原生返回 Guid，Parse 的 Guid 分支直通。</summary>
    private sealed class StringToGuidHandler : SqlMapper.TypeHandler<Guid>
    {
        public override Guid Parse(object value)
            => value switch
            {
                Guid guid => guid,
                string text => new Guid(text),
                _ => throw new InvalidCastException($"无法把 {value.GetType().Name} 转为 Guid"),
            };

        public override void SetValue(System.Data.IDbDataParameter parameter, Guid value)
            => parameter.Value = value.ToString();
    }

    private string T => Dataset.Table(dialect.Dialect);
    private string C(string c) => Dataset.Q(dialect.Dialect, c);
    private string Cols => Dataset.SelectColumns(dialect.Dialect);
    private string TT => Dataset.TenantTable(dialect.Dialect);

    // 灌数走 ADO.NET 臂的同一路径（perf_s1 与 bench_tenant 都含）——保证三实现的库内容逐位相同
    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
        => new AdoNetImpl(dialect).SetupAsync(conn, rows, ct);

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<S1Row>(
            $"SELECT {Cols} FROM {T} WHERE {C("Id")} = @id", new { id }).ConfigureAwait(false);

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        IEnumerable<S1Row> rows = await conn.QueryAsync<S1Row>($"SELECT {Cols} FROM {T}").ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
    {
        long n = 0;
        await foreach (S1Row? row in conn.QueryUnbufferedAsync<S1Row>($"SELECT {Cols} FROM {T}").ConfigureAwait(false))
        {
            await onRow(row).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", row).ConfigureAwait(false);

    /// <summary>批量插入——多值 VALUES 分批（v2 三臂契约）。
    /// <para><b>弃 Dapper multi-exec</b>（对 IEnumerable 逐行执行同一条 SQL = N 次往返）：
    /// 20000 行 MySQL 是 31 s/次，而手拼 VALUES 批是亚秒级——multi-exec 只在"顺手写 200 行"
    /// 的场景成立，不是批量插入的常规写法。Dapper 无 loader 封装，要用 COPY/LOAD DATA
    /// 的 Dapper 用户本来就直接写 ADO。</para></summary>
    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        long total = 0;
        int batch = BulkSql.EffectiveBatchRows(dialect.Dialect);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            total += await conn.ExecuteAsync(
                $"INSERT INTO {T} ({Cols}) VALUES {BulkSql.ValuesRows(end - start)}",
                InsertParameters(rows, start, end)).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>把一个批次的行装进 DynamicParameters——按列序（Id 在前，INSERT 序）。</summary>
    private static DynamicParameters InsertParameters(
        IReadOnlyList<S1Row> rows, int start, int end)
    {
        DynamicParameters dp = new();
        int p = 0;
        for (int r = start; r < end; r++)
        {
            S1Row row = rows[r];
            dp.Add(Dataset.P(p++), row.Id);
            dp.Add(Dataset.P(p++), row.Name);
            dp.Add(Dataset.P(p++), row.Qty);
            dp.Add(Dataset.P(p++), row.Price);
            dp.Add(Dataset.P(p++), row.Marker);
        }

        return dp;
    }

    /// <summary>把一个批次的行装进 DynamicParameters——按 UPDATE 序（Name/Qty/Price/Marker/Id，Id 在尾）。</summary>
    private static DynamicParameters UpdateParameters(
        IReadOnlyList<S1Row> rows, int start, int end)
    {
        DynamicParameters dp = new();
        int p = 0;
        for (int r = start; r < end; r++)
        {
            S1Row row = rows[r];
            dp.Add(Dataset.P(p++), row.Name);
            dp.Add(Dataset.P(p++), row.Qty);
            dp.Add(Dataset.P(p++), row.Price);
            dp.Add(Dataset.P(p++), row.Marker);
            dp.Add(Dataset.P(p++), row.Id);
        }

        return dp;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            $"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id",
            row).ConfigureAwait(false);

    /// <summary>批量更新——方言最优（v2 三臂契约，SQL 与 ADO 地板共用 BulkSql 真源）：
    /// PG 发 UPDATE FROM VALUES、MySQL 发 CASE WHEN；SQLite 逐条裹单事务
    /// （Dapper 无集合式封装，真实 Dapper 用户同样手拼这两类 SQL）。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (dialect.Dialect == Dialect.Sqlite)
        {
            await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            long rowTotal = 0;
            foreach (S1Row row in rows)
            {
                rowTotal += await conn.ExecuteAsync(
                    $"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id",
                    row, tran).ConfigureAwait(false);
            }

            await tran.CommitAsync(ct).ConfigureAwait(false);
            return rowTotal;
        }

        long total = 0;
        int batch = BulkSql.EffectiveBatchRows(dialect.Dialect);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            total += await conn.ExecuteAsync(
                BulkSql.UpdateBatch(dialect.Dialect, end - start),
                UpdateParameters(rows, start, end)).ConfigureAwait(false);
        }

        return total;
    }

    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        long total = 0;
        const int batch = BulkSql.BatchRows;
        for (int start = 0; start < keys.Count; start += batch)
        {
            int end = Math.Min(start + batch, keys.Count);
            long[] ids = new long[end - start];
            for (int i = start; i < end; i++)
            {
                ids[i - start] = Convert.ToInt64(keys[i], System.Globalization.CultureInfo.InvariantCulture);
            }

            total += await conn.ExecuteAsync(
                $"DELETE FROM {T} WHERE {C("Id")} IN ({Placeholders(ids.Length)})", Parameters(ids), tran)
                .ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
        return total;
    }

    /// <summary>生成 N 个参数占位符（@p0, @p1, ...）——三方言统一用 @pN，驱动各自接受。</summary>
    private static string Placeholders(int count)
        => string.Join(", ", Enumerable.Range(0, count).Select(static i => Dataset.P(i)));

    /// <summary>把 long[] 装进 DynamicParameters——按占位符名逐个绑定。</summary>
    private static DynamicParameters Parameters(long[] values)
    {
        DynamicParameters dp = new();
        for (int i = 0; i < values.Length; i++)
        {
            dp.Add(Dataset.P(i), values[i]);
        }

        return dp;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => $"SELECT {Cols} FROM {T} WHERE {C("Id")} = @id";

    public string BuildComplexQuerySql(DbConnection conn)
        => $"SELECT {Cols} FROM {T} WHERE ({C("Qty")} > @qty AND {C("Marker")} < @marker) OR {C("Name")} LIKE @name ORDER BY {C("Marker")} DESC LIMIT 100";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        IEnumerable<S1Row> rows = await conn.QueryAsync<S1Row>(
            $"SELECT {Cols} FROM {T} WHERE {C("Id")} > @lastId ORDER BY {C("Id")} LIMIT @pageSize",
            new { lastId, pageSize }).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        // 不用 Dapper 的 IN @ids 列表展开：它靠改写具名参数实现，Npgsql 是位置参数（$1），
        // 展开后 PG 报 42601 syntax error at or near "$1"。改为显式占位符 + DynamicParameters。
        IEnumerable<S1Row> rows = await conn.QueryAsync<S1Row>(
            $"SELECT {Cols} FROM {T} WHERE {C("Id")} IN ({Placeholders(ids.Length)})", Parameters(ids))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T}").ConfigureAwait(false);

    // ── 租户会话 / OwnedJson / SessionBatch ──

    /// <summary>bench_tenant 的 Dapper 投影 DTO——列名即属性名（含产品契约列的
    /// tenant_id/deleted_at 形态，Dapper 按列名匹配）。Payload 是 string：Dapper 无 JSON
    /// 列处理，反序列化在调用方（镜像产品 OwnedJson 的 STJ 源生成路径语义）。</summary>
    [SuppressMessage("Performance", "CA1812",
        Justification = "Dapper 经反射构造该 DTO（QueryAsync<BenchTenantDto> 的列映射载体），分析器静态不可见。")]
    private sealed record BenchTenantDto(
        int Id, long tenant_id, string? Text, DateTime CreationDate, int Value,
        string? deleted_at, string? Payload);

    /// <summary>租户过滤参数——@__tenant0（产品同款名）固定绑 <see cref="Dataset.TenantArmId"/>，
    /// 可选追加 <see cref="Dataset.P"/> 序号名的阈值参数。</summary>
    private static DynamicParameters TenantParameters(int? valueThreshold = null)
    {
        DynamicParameters dp = new();
        dp.Add(Dataset.TenantParameter, Dataset.TenantArmId);
        if (valueThreshold is { } threshold)
        {
            dp.Add(Dataset.P(1), threshold);
        }

        return dp;
    }

    public Task<long> TenantCountAsync(DbConnection conn, CancellationToken ct)
        => conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {TT} {Dataset.WhereTenant(dialect.Dialect)}",
            TenantParameters());

    public Task<long> TenantCountWhereAsync(DbConnection conn, int rows, CancellationToken ct)
        => conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {TT} {Dataset.WhereTenantValue(dialect.Dialect)}",
            TenantParameters(Dataset.TenantValueThreshold(rows)));

    public Task<List<BenchTenantPost>> TenantGetAllAsync(DbConnection conn, CancellationToken ct)
        => TenantQueryAsync(conn, "");

    /// <summary>OwnedJson 读路径——LIMIT 50 固定窗口（三方言同款，与地板臂同 SQL 形态）。</summary>
    public Task<List<BenchTenantPost>> OwnedJsonQueryAsync(DbConnection conn, CancellationToken ct)
        => TenantQueryAsync(conn, " LIMIT 50");

    /// <summary>租户会话读——select 到 DTO（Payload 为 string），客户端再经
    /// BenchTenantJsonContext 反序列化（STJ 源生成路径，与产品 OwnedJson emit 同形态；
    /// Dapper 的扩展方法无 CancellationToken 面，ct 在本臂不可达，与既有查询方法同口径）。</summary>
    private async Task<List<BenchTenantPost>> TenantQueryAsync(DbConnection conn, string limit)
    {
        IEnumerable<BenchTenantDto> rows = await conn.QueryAsync<BenchTenantDto>(
            $"SELECT {Dataset.TenantSelectColumns(dialect.Dialect)} FROM {TT} "
            + $"{Dataset.WhereTenant(dialect.Dialect)}{limit}",
            TenantParameters()).ConfigureAwait(false);
        var list = new List<BenchTenantPost>();
        foreach (BenchTenantDto dto in rows)
        {
            list.Add(new BenchTenantPost
            {
                Id = dto.Id,
                TenantId = dto.tenant_id,
                Text = dto.Text,
                CreationDate = dto.CreationDate,
                Value = dto.Value,
                DeletedAt = dto.deleted_at,
                Payload = dto.Payload is null
                    ? null
                    : JsonSerializer.Deserialize(
                        dto.Payload, BenchTenantJsonContext.Default.BenchTenantPayloadInfo)
            });
        }

        return list;
    }

    /// <summary>SessionBatch 路径——Dapper 无批 API，20 次 ExecuteAsync 循环就是要考的
    /// 形态差（产品/PG/MySQL 的 DbBatch 把 20 次往返压成 1 次，Dapper 用户只能逐条）。
    /// 主键从 <paramref name="offset"/> 起避开既有行与各轮已插段。</summary>
    public async Task<int> SessionBatchInsertsAsync(DbConnection conn, int offset, CancellationToken ct)
    {
        string sql = Dataset.TenantInsertSql(dialect.Dialect);
        int total = 0;
        for (int k = 0; k < Dataset.SessionBatchRows; k++)
        {
            BenchTenantPost row = Dataset.TenantSeed(offset + k);
            DynamicParameters dp = new();
            dp.Add(Dataset.P(0), row.Id);
            dp.Add(Dataset.P(1), row.TenantId);
            dp.Add(Dataset.P(2), row.Text);
            dp.Add(Dataset.P(3), row.CreationDate);
            dp.Add(Dataset.P(4), row.Value);
            dp.Add(Dataset.P(5), row.DeletedAt);
            dp.Add(Dataset.P(6), Dataset.TenantPayloadJson(row));
            total += await conn.ExecuteAsync(sql, dp).ConfigureAwait(false);
        }

        return total;
    }

    // ── 阶段 2 新增 ──

    /// <summary>批量 UPSERT——共用 BulkSql.UpsertBatch，整批裹事务（Dapper 无 UPSERT 封装，
    /// 真实 Dapper 用户同样手拼 ON CONFLICT / ON DUPLICATE KEY）。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        long total = 0;
        int batch = BulkSql.EffectiveBatchRows(dialect.Dialect);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            await conn.ExecuteAsync(
                BulkSql.UpsertBatch(dialect.Dialect, end - start),
                InsertParameters(rows, start, end), tran).ConfigureAwait(false);
            total += end - start;
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
        return total;
    }

    /// <summary>自增插入并回填主键——与 ADO 地板同 SQL 同 1 RTT 形态。</summary>
    public async Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
    {
        string sql = dialect.Dialect == Dialect.MySql
            ? $"INSERT INTO {T0()} ({C("Name")}, {C("Qty")}) VALUES (@name, @qty); SELECT LAST_INSERT_ID();"
            : $"INSERT INTO {T0()} ({C("Name")}, {C("Qty")}) VALUES (@name, @qty) RETURNING {C("Id")}";
        return await conn.ExecuteScalarAsync<long>(sql, new { name, qty }).ConfigureAwait(false);
    }

    private string T0() => Dataset.AutoIncTable(dialect.Dialect);

    /// <summary>宽表全表物化——Dapper 按列名映射（首行构建列映射，后续按序号读）。</summary>
    public async Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        IEnumerable<WideRow> rows = await conn.QueryAsync<WideRow>(
            $"SELECT {Dataset.WideSelectColumns(dialect.Dialect)} FROM {Dataset.WideTable(dialect.Dialect)}")
            .ConfigureAwait(false);
        int n = 0;
        foreach (WideRow _ in rows)
        {
            n++;
        }
        return n;
    }

    /// <summary>1:N 装配——multi-mapping JOIN（Dapper 的招牌能力：Query&lt;TParent,TChild&gt; +
    /// splitOn 按父去重），客户端字典配对。</summary>
    public async Task<(int Parents, int Children)> IncludeJoinAsync(
        DbConnection conn, int parentCount, CancellationToken ct)
    {
        string sql = $"SELECT {Cols}, {C("ChildId")}, {C("Note")} FROM {T} "
            + $"INNER JOIN {Dataset.ChildTable(dialect.Dialect)} ON "
            + $"{Dataset.ChildTable(dialect.Dialect)}.{C("ParentId")} = {T}.{C("Id")} "
            + $"WHERE {T}.{C("Id")} <= @parentCount ORDER BY {T}.{C("Id")}";
        Dictionary<long, S1Row> map = [];
        IEnumerable<S1Row> rows = await conn.QueryAsync<S1Row, ChildRow, S1Row>(
            sql,
            (parent, child) =>
            {
                if (map.TryGetValue(parent.Id, out S1Row? existing))
                {
                    return existing;
                }

                map.Add(parent.Id, parent);
                return parent;
            },
            new { parentCount },
            splitOn: "ChildId")
            .ConfigureAwait(false);
        int children = 0;
        foreach (S1Row _ in rows)
        {
            children++;
        }
        return (map.Count, children);
    }

    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", row, tran).ConfigureAwait(false);
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>事务内 10 条插入。Dapper 对 IEnumerable 参数走 multi-exec（一次调用 = N 次
    /// ExecuteNonQuery），10/100/批量三档共用同一路径，规模差异由调用方传入的 rows 决定。
    /// 每个入口用自己的契约守卫声明适用规模，避免三档在报告里混为一谈。</summary>
    public async Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows.Count, 10, nameof(rows));
        await TxInsertManyAsync(conn, rows, ct).ConfigureAwait(false);
    }

    /// <summary>事务内 100 条插入——与 <see cref="TxTenInsertsAsync"/> 同一路径不同规模。</summary>
    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        await TxInsertManyAsync(conn, rows, ct).ConfigureAwait(false);
    }

    private async Task TxInsertManyAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", rows, tran).ConfigureAwait(false);
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        // 事务内批量提交与无事务批量插入同构（多值 VALUES 分批），不再走 TxInsertManyAsync
        // 的 multi-exec——那是逐条提交语义的路径，10/100 条档专用。
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        int batch = BulkSql.EffectiveBatchRows(dialect.Dialect);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            await conn.ExecuteAsync(
                $"INSERT INTO {T} ({Cols}) VALUES {BulkSql.ValuesRows(end - start)}",
                InsertParameters(rows, start, end), tran).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (S1Row row in rows)
        {
            await conn.ExecuteAsync(
                $"UPDATE {T} SET {C("Qty")}=@Qty WHERE {C("Id")}=@Id", row, tran).ConfigureAwait(false);
        }

        await tran.RollbackAsync(ct).ConfigureAwait(false);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PalORM——被测对象
//
// 结构说明：PalORM 的 API 是 DataSession<TSession> 的实例方法，而 TSession 在三个 Provider 上
// 不同。泛型 helper 无法表达"同一 lambda 体套三种会话"（C# 泛型约束不支持
// where TSession : DataSession<TSession>），故按方言写三个具体分支。三者的 lambda 体逐位
// 相同——SQL 由 From<T>() 按方言自动生成，调用方无感。
// ─────────────────────────────────────────────────────────────────────────────

// DataSession.DisposeAsync 会关闭并释放构造时传入的 DbConnection
// （见 PalORM.Core DataSession.DisposeCoreAsync：先 CloseAsync 再 DisposeAsync）。
// 而 PerfHub 的三实现共用同一条已打开连接（B76：建连口径一致，否则比值被污染），
// 故本类的会话一律不释放：无活动事务/GridReader 时会话本身没有待释放资源，
// 连接由 RunAsync 的 await using 统一管理。按操作新建会话是与 Dapper 无状态
// 扩展方法对等的用法形态，测的正是"用户写完这行代码要付多少代价"。
[SuppressMessage("Reliability", "CA2000", Justification =
    "DataSession.DisposeAsync 会关闭共享连接；连接由外层统一管理，会话按操作新建是对等用法形态。")]
internal sealed class PalormImpl(DialectInfo dialect) : IPerfImplementation
{
    public string Name => "PalORM";

    private Dialect D => dialect.Dialect;

    /// <summary>方言分派的穷举兜底——正常不可达（<see cref="Dialect"/> 只有三个值）。
    /// 不带 paramName：<c>D</c> 是属性不是形参，用 <c>nameof(D)</c> 会触发
    /// CA2208/S3928（异常参数名必须对应真实形参）。</summary>
    private static NotSupportedException UnsupportedDialect(Dialect dialect)
        => new($"方言 '{dialect}' 不在 PerfHub 覆盖范围内（仅 SQLite / MySQL / PostgreSQL）。");

    /// <summary>按操作新建会话——与 Dapper 无状态扩展方法对等的用法形态。</summary>
    private static DataSession<TProvider> Session<TProvider>(DbConnection conn)
        where TProvider : IDbProvider, new()
        => new(conn, Opts(conn), [], null);

    /// <summary>批量插入的批行数——PG Binary COPY 无参数上限，整段单批 = 单次协议往返，
    /// 才是三臂契约的"行业最优调用"（此前固定 1000 是多值 VALUES 的变量上限思维：
    /// 20000 行 = 20 次 BeginBinaryImport/Complete 往返，实测比值 3.3× 由此而来，
    /// 2000 档 2 次 vs 1 次的 1.6× 与档位放大逐批吻合）；多值 VALUES 方言保持 1000。</summary>
    private static int BulkBatchRows(SqlDialect dialect, int rowCount)
        => dialect == SqlDialect.PostgreSql ? rowCount : 1000;

    private static DbOptions Opts(DbConnection conn)
        => new() { ConnectionString = conn.ConnectionString ?? "" };

    // ── 播种：DDL 走 PerfHub 自己的方言枚举，灌数走 PalORM 批量路径 ──
    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => SetupCoreAsync<SqliteProvider>(conn, rows, D, ct),
            Dialect.MySql => SetupCoreAsync<MySqlProvider>(conn, rows, D, ct),
            Dialect.PostgreSql => SetupCoreAsync<PostgreSqlProvider>(conn, rows, D, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static async Task SetupCoreAsync<TProvider>(
        DbConnection conn, int rows, Dialect dialect, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        await AdoNetImpl.ExecAsync(conn, Dataset.DropTableSql(dialect), ct).ConfigureAwait(false);
        await AdoNetImpl.ExecAsync(conn, Dataset.CreateTableSql(dialect), ct).ConfigureAwait(false);
        List<S1Row> seed = Dataset.SeedRows(rows);
        await Session<TProvider>(conn)
            .BulkInsertAsync(seed, BulkBatchRows(TProvider.Dialect, seed.Count), ct).ConfigureAwait(false);

        // bench_tenant：快照播种与 AdoNetImpl 同构（共享 TenantSeedSnapshots（连接, 行数）
        // 缓存）。首次进场本臂自己用产品 BulkInsertAsync 灌数（本臂最擅长路径）；命中缓存
        // 走服务端快照 reset——逐行重播全量是纯播种开销，不是被测路径。
        if (AdoNetImpl.TenantSeedSnapshots.ContainsKey((conn, rows)))
        {
            await AdoNetImpl.TenantResetAsync(dialect, conn, rows, ct).ConfigureAwait(false);
        }
        else
        {
            await AdoNetImpl.TenantSeedBeginAsync(dialect, conn, ct).ConfigureAwait(false);
            List<BenchTenantPost> tenantSeed = Dataset.TenantSeedRows(rows);
            await Session<TProvider>(conn)
                .BulkInsertAsync(tenantSeed, BulkBatchRows(TProvider.Dialect, tenantSeed.Count), ct)
                .ConfigureAwait(false);
            await AdoNetImpl.TenantSeedFinishAsync(dialect, conn, rows, ct).ConfigureAwait(false);
        }
    }

    public Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => GetByKeyCoreAsync<SqliteProvider>(conn, id, ct),
            Dialect.MySql => GetByKeyCoreAsync<MySqlProvider>(conn, id, ct),
            Dialect.PostgreSql => GetByKeyCoreAsync<PostgreSqlProvider>(conn, id, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<S1Row?> GetByKeyCoreAsync<TProvider>(DbConnection conn, long id, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>()
            .Where(Dataset.WhereId(TProvider.Dialect, id))
            .FirstOrDefaultAsync(ct).AsTask();

    public Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => QueryAllCoreAsync<SqliteProvider>(conn, ct),
            Dialect.MySql => QueryAllCoreAsync<MySqlProvider>(conn, ct),
            Dialect.PostgreSql => QueryAllCoreAsync<PostgreSqlProvider>(conn, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<List<S1Row>> QueryAllCoreAsync<TProvider>(DbConnection conn, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>().ToListAsync(ct).AsTask();

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => StreamAllCoreAsync<SqliteProvider>(conn, onRow, D, ct),
            Dialect.MySql => StreamAllCoreAsync<MySqlProvider>(conn, onRow, D, ct),
            Dialect.PostgreSql => StreamAllCoreAsync<PostgreSqlProvider>(conn, onRow, D, ct),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>流式全表——与 Dapper 的 <c>QueryUnbufferedAsync</c>、ADO.NET 的
    /// <c>ReadAsync</c> 循环同级对照。
    /// <para><b>为什么用 QueryAsyncEnumerable 而不是 ForEachAsync</b>：<c>ForEachAsync</c>
    /// 是 v5.8 才加的流式终结器（commit ee3e731），v5.5.1 基线没有它——用它就没法和基线对照。
    /// <c>QueryAsyncEnumerable</c> 两版都有，是流式对比的共同口径。</para>
    /// <para><b>列名/表名必须走文本段</b>（B78）：<c>QueryAsyncEnumerable</c> 收 FormattableString，
    /// 写成 <c>$"SELECT {列} FROM {表}"</c> 会把标识符当插值项参数化，拼出
    /// <c>SELECT @p0 FROM @p1</c>——SQLite 直接语法错误。故用 0 插值项的
    /// <c>FormattableStringFactory.Create</c> 承载已拼好的 SQL 文本。</para></summary>
    private static async Task<long> StreamAllCoreAsync<TProvider>(
        DbConnection conn, Func<S1Row, ValueTask> onRow, Dialect dialect, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        long n = 0;
        FormattableString sql = FormattableStringFactory.Create(
            "SELECT " + Dataset.SelectColumns(dialect) + " FROM " + Dataset.Table(dialect));
        await foreach (S1Row? row in Session<TProvider>(conn)
            .QueryAsyncEnumerable<S1Row>(sql, ct: ct).ConfigureAwait(false))
        {
            await onRow(row).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    public Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => InsertCoreAsync<SqliteProvider>(conn, row, ct),
            Dialect.MySql => InsertCoreAsync<MySqlProvider>(conn, row, ct),
            Dialect.PostgreSql => InsertCoreAsync<PostgreSqlProvider>(conn, row, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static async Task InsertCoreAsync<TProvider>(DbConnection conn, S1Row row, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        await Session<TProvider>(conn).InsertAsync(row, ct).ConfigureAwait(false);
    }

    public Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => BulkInsertCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => BulkInsertCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => BulkInsertCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<long> BulkInsertCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn)
            .BulkInsertAsync(rows, BulkBatchRows(TProvider.Dialect, rows.Count), ct).AsTask();

    public Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => UpdateCoreAsync<SqliteProvider>(conn, row, ct),
            Dialect.MySql => UpdateCoreAsync<MySqlProvider>(conn, row, ct),
            Dialect.PostgreSql => UpdateCoreAsync<PostgreSqlProvider>(conn, row, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<int> UpdateCoreAsync<TProvider>(DbConnection conn, S1Row row, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).UpdateAsync(row, ct).AsTask();

    public Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => BulkUpdateCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => BulkUpdateCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => BulkUpdateCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<long> BulkUpdateCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).BulkUpdateAsync(rows, ct).AsTask();

    public Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => BulkDeleteCoreAsync<SqliteProvider>(conn, keys, ct),
            Dialect.MySql => BulkDeleteCoreAsync<MySqlProvider>(conn, keys, ct),
            Dialect.PostgreSql => BulkDeleteCoreAsync<PostgreSqlProvider>(conn, keys, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<long> BulkDeleteCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).BulkDeleteAsync<S1Row>(keys, ct).AsTask();

    // ── SQL 构建（纯 ORM 开销，不执行）──
    public string BuildGetByKeySql(DbConnection conn, long id)
        => D switch
        {
            Dialect.Sqlite => BuildGetByKeyCore<SqliteProvider>(conn, id),
            Dialect.MySql => BuildGetByKeyCore<MySqlProvider>(conn, id),
            Dialect.PostgreSql => BuildGetByKeyCore<PostgreSqlProvider>(conn, id),
            _ => throw UnsupportedDialect(D)
        };

    private static string BuildGetByKeyCore<TProvider>(DbConnection conn, long id)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>()
            .Where(Dataset.WhereId(TProvider.Dialect, id))
            .AsDryRun().Sql;

    public string BuildComplexQuerySql(DbConnection conn)
        => D switch
        {
            Dialect.Sqlite => BuildComplexCore<SqliteProvider>(conn, D),
            Dialect.MySql => BuildComplexCore<MySqlProvider>(conn, D),
            Dialect.PostgreSql => BuildComplexCore<PostgreSqlProvider>(conn, D),
            _ => throw UnsupportedDialect(D)
        };

    private static string BuildComplexCore<TProvider>(DbConnection conn, Dialect dialect)
        where TProvider : IDbProvider, new()
    {
        // 列名必须走文本段（B78）：写成插值项会被参数化成字符串，PG 报 text = bigint
        FormattableString where = FormattableStringFactory.Create(
            $"{Dataset.Q(dialect, "Qty")} > {{0}} AND {Dataset.Q(dialect, "Marker")} < {{1}}", 100, 900000L);
        FormattableString like = FormattableStringFactory.Create(
            $"{Dataset.Q(dialect, "Name")} LIKE {{0}}", "row-%");
        return Session<TProvider>(conn).From<S1Row>()
            .Where(where)
            .OrWhere(like)
            // 用 OrderBy(descending: true) 而不是 OrderByDescending——后者是 v5.8 才补的
            // 便捷重载，v5.5.1 基线没有它，用了就没法和基线同口径对照。
            .OrderBy(static x => x.Marker, descending: true)
            .Take(100)
            .AsDryRun().Sql;
    }

    public Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => KeysetPageCoreAsync<SqliteProvider>(conn, lastId, pageSize, ct),
            Dialect.MySql => KeysetPageCoreAsync<MySqlProvider>(conn, lastId, pageSize, ct),
            Dialect.PostgreSql => KeysetPageCoreAsync<PostgreSqlProvider>(conn, lastId, pageSize, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<List<S1Row>> KeysetPageCoreAsync<TProvider>(
        DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>()
            .Where(Dataset.WhereIdGt(TProvider.Dialect, lastId))
            .OrderBy(static x => x.Id)
            .Take(pageSize)
            .ToListAsync(ct).AsTask();

    public Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => WhereInCoreAsync<SqliteProvider>(conn, ids, ct),
            Dialect.MySql => WhereInCoreAsync<MySqlProvider>(conn, ids, ct),
            Dialect.PostgreSql => WhereInCoreAsync<PostgreSqlProvider>(conn, ids, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<List<S1Row>> WhereInCoreAsync<TProvider>(
        DbConnection conn, long[] ids, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>().WhereIn(static x => x.Id, ids).ToListAsync(ct).AsTask();

    public Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => CountCoreAsync<SqliteProvider>(conn, ct),
            Dialect.MySql => CountCoreAsync<MySqlProvider>(conn, ct),
            Dialect.PostgreSql => CountCoreAsync<PostgreSqlProvider>(conn, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<long> CountCoreAsync<TProvider>(DbConnection conn, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).CountAsync<S1Row>(ct: ct).AsTask();

    // ── 租户会话 / OwnedJson / SessionBatch ──

    public Task<long> TenantCountAsync(DbConnection conn, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TenantCountCoreAsync<SqliteProvider>(conn, ct),
            Dialect.MySql => TenantCountCoreAsync<MySqlProvider>(conn, ct),
            Dialect.PostgreSql => TenantCountCoreAsync<PostgreSqlProvider>(conn, ct),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>租户会话 COUNT——WithTenant 让产品自动附加"deleted_at IS NULL AND
    /// tenant_id = @__tenant0"（与地板/Dapper 臂的同款 WHERE 段同源）。</summary>
    private static Task<long> TenantCountCoreAsync<TProvider>(DbConnection conn, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).WithTenant(Dataset.TenantArmId)
            .CountAsync<BenchTenantPost>(ct: ct).AsTask();

    public Task<long> TenantCountWhereAsync(DbConnection conn, int rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TenantCountWhereCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => TenantCountWhereCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => TenantCountWhereCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<long> TenantCountWhereCoreAsync<TProvider>(
        DbConnection conn, int rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        // 列名必须走文本段（B78）：写成插值项会被参数化成字符串，PG 报 operator does not exist
        FormattableString where = FormattableStringFactory.Create(
            Dataset.Q(Dataset.Of(TProvider.Dialect), "Value") + " > {0}",
            Dataset.TenantValueThreshold(rows));
        return Session<TProvider>(conn).WithTenant(Dataset.TenantArmId)
            .CountAsync<BenchTenantPost>(where, ct).AsTask();
    }

    public Task<List<BenchTenantPost>> TenantGetAllAsync(DbConnection conn, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TenantGetAllCoreAsync<SqliteProvider>(conn, ct),
            Dialect.MySql => TenantGetAllCoreAsync<MySqlProvider>(conn, ct),
            Dialect.PostgreSql => TenantGetAllCoreAsync<PostgreSqlProvider>(conn, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<List<BenchTenantPost>> TenantGetAllCoreAsync<TProvider>(
        DbConnection conn, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).WithTenant(Dataset.TenantArmId)
            .From<BenchTenantPost>().ToListAsync(ct).AsTask();

    public Task<List<BenchTenantPost>> OwnedJsonQueryAsync(DbConnection conn, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => OwnedJsonQueryCoreAsync<SqliteProvider>(conn, ct),
            Dialect.MySql => OwnedJsonQueryCoreAsync<MySqlProvider>(conn, ct),
            Dialect.PostgreSql => OwnedJsonQueryCoreAsync<PostgreSqlProvider>(conn, ct),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>OwnedJson 读路径——产品的 From&lt;T&gt;().Take(50)：租户 + 软删过滤自动附加，
    /// Payload 列由 SourceGen 的 OwnedJson 反序列化路径回填（BenchTenantJsonContext）。</summary>
    private static Task<List<BenchTenantPost>> OwnedJsonQueryCoreAsync<TProvider>(
        DbConnection conn, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).WithTenant(Dataset.TenantArmId)
            .From<BenchTenantPost>().Take(50).ToListAsync(ct).AsTask();

    public Task<int> SessionBatchInsertsAsync(DbConnection conn, int offset, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => SessionBatchCoreAsync<SqliteProvider>(conn, offset, ct),
            Dialect.MySql => SessionBatchCoreAsync<MySqlProvider>(conn, offset, ct),
            Dialect.PostgreSql => SessionBatchCoreAsync<PostgreSqlProvider>(conn, offset, ct),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>SessionBatch 路径——产品 CreateBatch + Append(FormattableString)：20 条单行
    /// INSERT 一个批操作（PG/MySQL 走 DbBatch 真单往返，SQLite 由产品内部回退顺序执行）。
    /// <para>INSERT 不加租户过滤（产品语义：Insert/Save 不过滤租户，实体自带 tenant_id
    /// 列值）；Payload 列传 JSON 文本——Append 只做参数绑定，序列化是调用方责任，
    /// 与产品的单行 Insert 路径同纪律。</para></summary>
    private static async Task<int> SessionBatchCoreAsync<TProvider>(
        DbConnection conn, int offset, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        Dialect dialect = Dataset.Of(TProvider.Dialect);
        // 用 format-item 版而不是 TenantInsertSql 的字面 @pN 版——产品的 FormattableString
        // 守卫拒绝字面占位符文本（"literal text '@p…' collides with PalORM's reserved
        // parameter naming"）。列名走文本段（B78），值只进 format items。
        string format = Dataset.TenantInsertRowFormat(dialect);
        DataSession<TProvider> session = Session<TProvider>(conn);
        using var batch = session.CreateBatch();
        for (int k = 0; k < Dataset.SessionBatchRows; k++)
        {
            BenchTenantPost row = Dataset.TenantSeed(offset + k);
            batch.Append(FormattableStringFactory.Create(
                format,
                row.Id, row.TenantId, row.Text, row.CreationDate, row.Value, row.DeletedAt,
                Dataset.TenantPayloadJson(row)));
        }

        return await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── 事务场景：真实业务形态（开启事务 → N 条写 → 提交/回滚）──
    // ── 阶段 2 新增 ──

    /// <summary>批量 UPSERT——产品的 BulkMergeAsync（集合化 ON CONFLICT / ON DUPLICATE KEY，
    /// BulkMergeSetBasedTests 锁口径：返回处理实体数而非驱动行数）。</summary>
    public Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => Session<SqliteProvider>(conn).BulkMergeAsync(rows, ct).AsTask(),
            Dialect.MySql => Session<MySqlProvider>(conn).BulkMergeAsync(rows, ct).AsTask(),
            Dialect.PostgreSql => Session<PostgreSqlProvider>(conn).BulkMergeAsync(rows, ct).AsTask(),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>自增插入并回填主键——产品 InsertAsync 的回填路径
    /// （PG/SQLite 经 RETURNING 返回完整行，MySQL 走 LAST_INSERT_ID 单往返合并；
    /// 回填后实体 Id 即生成主键）。</summary>
    public async Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
    {
        AutoIncRow row = new() { Name = name, Qty = qty };
        switch (D)
        {
            case Dialect.Sqlite:
                _ = await Session<SqliteProvider>(conn).InsertAsync(row, ct).ConfigureAwait(false);
                break;
            case Dialect.MySql:
                _ = await Session<MySqlProvider>(conn).InsertAsync(row, ct).ConfigureAwait(false);
                break;
            case Dialect.PostgreSql:
                _ = await Session<PostgreSqlProvider>(conn).InsertAsync(row, ct).ConfigureAwait(false);
                break;
            default:
                throw UnsupportedDialect(D);
        }

        return row.Id;
    }

    /// <summary>宽表全表物化——产品 ToListAsync（源生成绑定器按列数伸缩的测量点）。</summary>
    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => WideCoreAsync<SqliteProvider>(conn, ct),
            Dialect.MySql => WideCoreAsync<MySqlProvider>(conn, ct),
            Dialect.PostgreSql => WideCoreAsync<PostgreSqlProvider>(conn, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static async Task<int> WideCoreAsync<TProvider>(DbConnection conn, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        List<WideRow> rows = await Session<TProvider>(conn).From<WideRow>().ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary>1:N 装配——产品的 Include（生成 INNER JOIN）+ 客户端分组。
    /// <para><b>策略标注</b>：PalORM 的 Include 仅生成 JOIN 子句、不装配导航对象
    /// （与 EF 不同），客户端配对是调用方责任——三臂在此项都是 JOIN + 客户端装配，
    /// 差异在库帮你做多少（Dapper multi-mapping 半自动 / ADO 全手工 / PalORM JOIN 生成）。</para></summary>
    public async Task<(int Parents, int Children)> IncludeJoinAsync(
        DbConnection conn, int parentCount, CancellationToken ct)
    {
        switch (D)
        {
            case Dialect.Sqlite:
                return await JoinCoreAsync<SqliteProvider>(conn, parentCount, ct).ConfigureAwait(false);
            case Dialect.MySql:
                return await JoinCoreAsync<MySqlProvider>(conn, parentCount, ct).ConfigureAwait(false);
            case Dialect.PostgreSql:
                return await JoinCoreAsync<PostgreSqlProvider>(conn, parentCount, ct).ConfigureAwait(false);
            default:
                throw UnsupportedDialect(D);
        }
    }

    private static async Task<(int Parents, int Children)> JoinCoreAsync<TProvider>(
        DbConnection conn, int parentCount, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        // Include 生成 INNER JOIN；Select 列含父全列 + 子两列，行按父 Id 去重计数
        List<S1Row> flat = await Session<TProvider>(conn).From<S1Row>()
            .Include<ChildRow>(static p => p.Id, static c => c.ParentId)
            .Where(Dataset.WhereIdLe(DialectOf<TProvider>(), parentCount))
            .ToListAsync(ct).ConfigureAwait(false);
        long last = 0;
        int parents = 0;
        foreach (S1Row row in flat)
        {
            if (row.Id != last)
            {
                parents++;
                last = row.Id;
            }
        }
        return (parents, flat.Count);
    }

    private static Dialect DialectOf<TProvider>() where TProvider : IDbProvider, new()
        => Dataset.Of(TProvider.Dialect);

    public Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    => D switch
    {
        Dialect.Sqlite => TxSingleCoreAsync<SqliteProvider>(conn, row, ct),
        Dialect.MySql => TxSingleCoreAsync<MySqlProvider>(conn, row, ct),
        Dialect.PostgreSql => TxSingleCoreAsync<PostgreSqlProvider>(conn, row, ct),
        _ => throw UnsupportedDialect(D)
    };

    private static async Task TxSingleCoreAsync<TProvider>(DbConnection conn, S1Row row, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        DataSession<TProvider> session = Session<TProvider>(conn);
        await session.WithTransaction(
            async _ => await session.InsertAsync(row, ct).ConfigureAwait(false), ct: ct).ConfigureAwait(false);
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TxInsertManyCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => TxInsertManyCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => TxInsertManyCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    public Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TxInsertManyCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => TxInsertManyCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => TxInsertManyCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    public Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TxBulkCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => TxBulkCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => TxBulkCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>事务内批量提交——走产品的 BulkInsertAsync（PG COPY / MySQL 能力分流 /
    /// SQLite 多值），与 v2 三臂契约一致。此前误用逐行 InsertAsync（2000 行 = 2000 次往返，
    /// MySQL 实测 706 ms vs 地板 21.7 ms，门禁方向的对称暴露）。</summary>
    private static async Task TxBulkCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        DataSession<TProvider> session = Session<TProvider>(conn);
        await session.WithTransaction(
            async _ => await session.BulkInsertAsync(
                rows, BulkBatchRows(TProvider.Dialect, rows.Count), ct).ConfigureAwait(false), ct: ct)
            .ConfigureAwait(false);
    }

    /// <summary>事务内 N 条插入——10 / 100 / 批量三档共用同一路径，规模差异由调用方传入的
    /// <paramref name="rows"/> 决定（PalORM 没有独立的 bulk-in-transaction 入口，
    /// 这正是要与 Dapper multi-exec 对照的点）。</summary>
    private static async Task TxInsertManyCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        DataSession<TProvider> session = Session<TProvider>(conn);
        await session.WithTransaction(async _ =>
        {
            foreach (S1Row row in rows)
            {
                await session.InsertAsync(row, ct).ConfigureAwait(false);
            }
        }, ct: ct).ConfigureAwait(false);
    }

    public Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TxRollbackCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => TxRollbackCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => TxRollbackCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>事务内更新 N 行后主动抛异常触发回滚——测的是"撤销量"而非"提交量"。</summary>
    private static async Task TxRollbackCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        DataSession<TProvider> session = Session<TProvider>(conn);
        try
        {
            await session.WithTransaction(async _ =>
            {
                foreach (S1Row row in rows)
                {
                    await session.UpdateAsync(row, ct).ConfigureAwait(false);
                }
                throw new InvalidOperationException("intentional rollback");
            }, ct: ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // 预期的回滚路径——WithTransaction 把 callback 异常转成 rollback 后重新抛出
        }
    }
}
