using System.Data.Common;
using System.Text.Json;
using FreeSql;
using PalORM.PerfHub;

namespace PalORM.PerfHub;

/// <summary>交叉基准 Runner——操作集、prepare 语义、迭代上限与操作顺序逐位对齐
/// PerfHub 的 RunOneImplAsync（PalORM 原测试协议），仅把 Dapper 臂换成 FreeSql 臂。
/// 测量引擎（Measure.SingleAsync：预热时间预算 + 自适应迭代 + 分配/堆采样）与 PerfHub 同源。
/// 每方言每档两遍反序（pass 0 正序、pass 1 反序），取两遍中位数聚合，消除臂顺序相位差。</summary>
internal static class Program
{
    internal const string Pragma =
        "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL; PRAGMA synchronous = NORMAL; "
        + "PRAGMA cache_size = -65536; PRAGMA temp_store = MEMORY; PRAGMA wal_autocheckpoint = 1000; "
        + "PRAGMA mmap_size = 268435456; PRAGMA analysis_limit = 400; PRAGMA optimize = 0x10002";

    /// <summary>仓库根定位（向上找 PalORM.slnx，与 PerfHub.Program.FindRepoRoot 同法）。</summary>
    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("未找到仓库根（PalORM.slnx）——从仓内运行 dotnet run。");
    }

    /// <summary>结果目录：仓内 bench/crossbench/results（基线与原始批 JSON 落此处入库）。</summary>
    internal static string ResultsDir() => Path.Combine(FindRepoRoot(), "bench", "crossbench", "results");

    internal static string CurrentCommit() => "crossbench";

    private const int BulkDeleteMaxRounds = 50;
    private const int BulkDeleteSeedRowBudget = 200_000;

    private static async Task<int> Main(string[] args)
    {
        int[] tiers = args.Where(static a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
        if (tiers.Length == 0)
        {
            tiers = [2_000, 20_000];
        }

        string[] dialectFilter = [];
        int dialectIndex = Array.IndexOf(args, "--dialects");
        if (dialectIndex >= 0 && dialectIndex + 1 < args.Length)
        {
            dialectFilter = args[dialectIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        List<Dialect> dialects = dialectFilter.Length == 0
            ? [Dialect.Sqlite, Dialect.MySql, Dialect.PostgreSql]
            : [.. dialectFilter.Select(ParseDialect)];

        string[] opFilter = [];
        int opsIndex = Array.IndexOf(args, "--ops");
        if (opsIndex >= 0 && opsIndex + 1 < args.Length)
        {
            opFilter = args[opsIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        SQLitePCL.Batteries_V2.Init();

        var results = new List<Measurement>();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        foreach (Dialect dialect in dialects)
        {
            DialectInfo info = DialectInfo.Of(dialect);
            string cs = Connections.Resolve(info);
            await using DbConnection conn = info.OpenConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);
            if (dialect == Dialect.Sqlite)
            {
                await ExecAsync(conn, Pragma).ConfigureAwait(false);
            }

            IPerfImplementation[] impls =
                [new AdoNetImpl(info), new DapperImpl(info), new FreeSqlImpl(info), new PalormImpl(info)];
            try
            {
                foreach (int rows in tiers)
                {
                    int bulkDeleteRounds = Math.Clamp(BulkDeleteSeedRowBudget / rows, 3, BulkDeleteMaxRounds);
                    // 每档开始：重建 perf_s1 + 两张快照（rows 档 / BulkDelete 的 rows×rounds 档）
                    await BuildSnapshotsAsync(conn, dialect, rows, bulkDeleteRounds).ConfigureAwait(false);

                    long[] whereInIds = BuildWhereInIds(rows);
                    long[] keys = Dataset.KeySet(rows);

                    Console.WriteLine();
                    Console.WriteLine($"── {info.DisplayName} rows={rows:N0} ──");

                    for (int pass = 0; pass < 2; pass++)
                    {
                        // 读不变量：每个读 perf_s1 的测量都从「表内恰好 rows 行」开始——
                        // 两遍的读操作前各重置一次（PerfHub 的交错读同语义）
                        await RestoreAsync(conn, dialect, "perf_s1_seed").ConfigureAwait(false);

                        IPerfImplementation[] order = pass == 0 ? impls : [.. impls.Reverse()];

                        // 读正确性守卫（每方言一次，不计入测量）：Fluent 配置的 FreeSql 实体
                        // 映射必须与 ADO 同值——防列错位静默产生错误数据
                        if (pass == 0 && rows == tiers[0])
                        {
                            S1Row? probe = await impls[2].GetByKeyAsync(conn, 1, CancellationToken.None)
                                .ConfigureAwait(false);
                            List<S1Row> all = await impls[2].QueryAllAsync(conn, CancellationToken.None)
                                .ConfigureAwait(false);
                            if (probe?.Name != "row-0" || probe.Price != 0m || probe.Marker != 0
                                || all.Count != rows || all[^1].Id != rows)
                            {
                                throw new InvalidOperationException(
                                    $"FreeSql 读映射校验失败：probe={probe?.Name}/{probe?.Price}/{probe?.Marker} count={all.Count} last={all[^1].Id}");
                            }
                            Console.WriteLine("    FreeSql 读映射校验 OK");
                        }

                        foreach (Op op in BuildOperations(dialect, rows, keys, whereInIds, bulkDeleteRounds))
                        {
                            if (op.MinTierOnly && rows != tiers.Min())
                            {
                                continue;
                            }

                            if (opFilter.Length > 0 && !opFilter.Contains(op.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            foreach (IPerfImplementation impl in order)
                            {
                                Measurement m = await Measure.SingleAsync(
                                    info, impl, op.Name, op.Group, rows, op.Action, conn, op.MaxIterations,
                                    CancellationToken.None, op.Prepare,
                                    budgetSeconds: 1.5,
                                    scale: 1.0,
                                    requireWarmupFloor: rows == tiers[0]).ConfigureAwait(false);
                                results.Add(m);
                                Console.WriteLine(
                                    $"p{pass} {op.Name,-16} {impl.Name,-9} {m.MedianNs / 1000.0,10:F1} us  "
                                    + $"{m.AllocatedBytesPerOp,10:F0} B  iters={m.Iterations,6}  err={m.ErrorRatio:P1}");
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (IPerfImplementation impl in impls)
                {
                    (impl as IDisposable)?.Dispose();
                }
            }
        }

        string dir = ResultsDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"crossbench-{stamp}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);
        Console.WriteLine($"WROTE {path}");
        return 0;
    }

    private static Dialect ParseDialect(string value) => value.ToLowerInvariant() switch
    {
        "sqlite" => Dialect.Sqlite,
        "mysql" => Dialect.MySql,
        "pg" or "postgres" or "postgresql" => Dialect.PostgreSql,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "dialect 只支持 sqlite/mysql/pg")
    };

    private static List<Op> BuildOperations(
        Dialect dialect, int rows, long[] keys, long[] whereInIds, int bulkDeleteRounds) =>
    [
        // ── 读（pass 开始时表恰好 rows 行，三臂同状态）──
        new("GetByKey", "CRUD", 200,
            (impl, c, i) => impl.GetByKeyAsync(c, keys[i * 7919 % keys.Length], CancellationToken.None)),
        new("GetAllAsync", "CRUD", 200,
            (impl, c, _) => impl.GetAllAsync(c, CancellationToken.None)),
        new("QueryAll", "CRUD", 200,
            (impl, c, _) => impl.QueryAllAsync(c, CancellationToken.None)),

        // ── 写（PerfHub 顺序：Insert → Update → BulkUpdate → BulkInsert → BulkDelete）──
        new("Insert", "CRUD", 200,
            (impl, c, i) => impl.InsertAsync(c, Dataset.Seed(rows + i), CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed")),
        new("Update", "CRUD", 200,
            (impl, c, i) => impl.UpdateAsync(c, Dataset.Seed(i * 7919 % rows), CancellationToken.None)),
        new("BulkUpdate", "CRUD", 200,
            (impl, c, _) => impl.BulkUpdateAsync(c, Dataset.SeedRows(rows, 0), CancellationToken.None)),
        new("BulkInsert", "CRUD", 200,
            (impl, c, i) => impl.BulkInsertAsync(
                c, Dataset.SeedRows(rows, (long)rows * (i + 1)), CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed")),
        new("BulkDelete", "CRUD", bulkDeleteRounds,
            (impl, c, i) =>
            {
                object[] batch = new object[rows];
                for (int k = 0; k < rows; k++)
                {
                    batch[k] = (long)((i * rows) + k + 1);
                }

                return impl.BulkDeleteAsync(c, batch, CancellationToken.None);
            },
            c => RestoreAsync(c, dialect, "perf_s1_seed_bd")),

        // ── Query（BulkDelete 之后都带 reset，恢复「恰好 rows 行」）──
        new("KeysetPage", "Query", 200,
            (impl, c, i) => impl.KeysetPageAsync(c, i % 20 * 100, 100, CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed")),
        new("WhereIn", "Query", 200,
            (impl, c, _) => impl.WhereInAsync(c, whereInIds, CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed")),
        new("Count", "Query", 200,
            (impl, c, _) => impl.CountAsync(c, CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed")),

        // ── Upsert（对已存在主键段整批 UPSERT）──
        new("UpsertBatch", "CRUD", 200,
            (impl, c, _) => impl.UpsertBatchAsync(c, Dataset.SeedRows(rows, 0), CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed")),

        // ── 事务族（仅最小档，PerfHub RunsAtTier 语义）──
        new("TxSingleInsert", "Transaction", 200,
            (impl, c, i) => impl.TxSingleInsertAsync(c, Dataset.Seed(rows + i), CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed"),
            MinTierOnly: true),
        new("TxHundredInserts", "Transaction", 200,
            (impl, c, i) => impl.TxHundredInsertsAsync(
                c, Dataset.SeedRows(100, rows + ((long)i * 100)), CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed"),
            MinTierOnly: true),
        new("TxBulkInsert", "Transaction", 200,
            (impl, c, i) => impl.TxBulkInsertAsync(
                c, Dataset.SeedRows(rows, (long)rows * (i + 1)), CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed"),
            MinTierOnly: true),
        new("TxRollback", "Transaction", 200,
            (impl, c, i) => impl.TxRollbackAsync(
                c, Dataset.SeedRows(Math.Min(rows, 500), 0), CancellationToken.None),
            c => RestoreAsync(c, dialect, "perf_s1_seed"),
            MinTierOnly: true),
        new("EnumInserts", "Transaction", 200,
            (impl, c, i) => impl.EnumInsertsAsync(
                c, Dataset.SeedEnumRows(100, (long)i * 100), CancellationToken.None),
            c => AdoNetImpl.SetupPerfEnumTableAsync(dialect, c, CancellationToken.None),
            MinTierOnly: true),
    ];

    private static long[] BuildWhereInIds(int rows)
    {
        const int count = 100;
        long[] ids = new long[count];
        long step = Math.Max(1, rows / count);
        for (int i = 0; i < count; i++)
        {
            ids[i] = (i * step) + 1;
        }

        return ids;
    }

    /// <summary>建两张快照：perf_s1_seed（rows 行，常规 reset）与 perf_s1_seed_bd
    /// （rows×rounds 行，BulkDelete 专用）。快照带主键 DDL，恢复是 DELETE + INSERT..SELECT，
    /// 主键约束不丢（CREATE TABLE AS SELECT 会丢 PK，故显式建表再灌数）。</summary>
    private static async Task BuildSnapshotsAsync(DbConnection conn, Dialect dialect, int rows, int bulkDeleteRounds)
    {
        string ddlBase = Dataset.CreateTableSql(dialect);
        await ExecAsync(conn, $"DROP TABLE IF EXISTS {Q(dialect, "perf_s1_seed_bd")}").ConfigureAwait(false);
        await ExecAsync(conn, $"DROP TABLE IF EXISTS {Q(dialect, "perf_s1_seed")}").ConfigureAwait(false);
        await ExecAsync(conn, Dataset.DropTableSql(dialect)).ConfigureAwait(false);
        await ExecAsync(conn, ddlBase).ConfigureAwait(false);

        string seedDdl = ddlBase.Replace("perf_s1", "perf_s1_seed");
        string seedBdDdl = ddlBase.Replace("perf_s1", "perf_s1_seed_bd");
        await ExecAsync(conn, seedDdl).ConfigureAwait(false);
        await ExecAsync(conn, seedBdDdl).ConfigureAwait(false);

        await InsertRowsAsync(conn, dialect, "perf_s1_seed", Dataset.SeedRows(rows)).ConfigureAwait(false);
        await InsertRowsAsync(
            conn, dialect, "perf_s1_seed_bd", Dataset.SeedRows(rows * bulkDeleteRounds)).ConfigureAwait(false);

        await RestoreAsync(conn, dialect, "perf_s1_seed").ConfigureAwait(false);
    }

    /// <summary>恢复 = DROP + CREATE（带主键 DDL）+ INSERT..SELECT。不能用
    /// DELETE + INSERT..SELECT：pass 0 的批量写把表撑大后，DELETE 留下的死元组
    /// 会让 pass 1 的 PG 顺序扫描慢 40~56×（2026-10-24 实测三臂同慢），物理重建无此问题。</summary>
    private static Task RestoreAsync(DbConnection conn, Dialect dialect, string snapshot)
        => RestoreFromAsync(conn, dialect, snapshot, Dataset.Table(dialect));

    private static async Task RestoreFromAsync(DbConnection conn, Dialect dialect, string snapshot, string target)
    {
        string ddl = Dataset.CreateTableSql(dialect);
        if (target != Dataset.Table(dialect))
        {
            ddl = ddl.Replace("perf_s1", target);
        }

        await ExecAsync(conn, $"DROP TABLE IF EXISTS {target}").ConfigureAwait(false);
        await ExecAsync(conn, ddl).ConfigureAwait(false);
        await ExecAsync(
            conn,
            $"INSERT INTO {target} SELECT * FROM {Q(dialect, snapshot)}").ConfigureAwait(false);
    }

    private static async Task InsertRowsAsync(
        DbConnection conn, Dialect dialect, string table, IReadOnlyList<S1Row> rows)
    {
        string columns = string.Join(
            ", ",
            new[] { "Id", "Name", "Qty", "Price", "Marker" }.Select(c => Dataset.Q(dialect, c)));
        await using DbTransaction tran = await conn.BeginTransactionAsync().ConfigureAwait(false);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"INSERT INTO {Q(dialect, table)} ({columns}) VALUES (@Id,@Name,@Qty,@Price,@Marker)";
        DbParameter pId = AddParameter(cmd, "@Id");
        DbParameter pName = AddParameter(cmd, "@Name");
        DbParameter pQty = AddParameter(cmd, "@Qty");
        DbParameter pPrice = AddParameter(cmd, "@Price");
        DbParameter pMarker = AddParameter(cmd, "@Marker");
        foreach (S1Row row in rows)
        {
            pId.Value = row.Id;
            pName.Value = row.Name;
            pQty.Value = row.Qty;
            pPrice.Value = row.Price;
            pMarker.Value = row.Marker;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await tran.CommitAsync().ConfigureAwait(false);
    }

    private static DbParameter AddParameter(DbCommand cmd, string name)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    private static string Q(Dialect dialect, string identifier) => Dataset.Q(dialect, identifier);

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed record Op(
        string Name,
        string Group,
        int MaxIterations,
        Func<IPerfImplementation, DbConnection, int, Task> Action,
        Func<DbConnection, Task>? Prepare = null,
        bool MinTierOnly = false);
}
