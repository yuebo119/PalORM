using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using PalORM;
using PalORM.Sqlite;

namespace EnumProbe;

// 探针实体（SourceGen 自动注册）
[Table("probe_enum")]
internal sealed partial class ProbeRow
{
    [Key(AutoIncrement = false)]
    [Column("Id")]
    public long Id { get; set; }

    [Column("Name")]
    public string Name { get; set; } = "";

    [Column("Status")]
    public ProbeStatus Status { get; set; }
}

internal enum ProbeStatus { Pending, Processing, Paid, Shipped, Delivered, Cancelled }

[Table("probe_upd")]
internal sealed partial class ProbeUpdRow
{
    [Key(AutoIncrement = false)]
    [Column("Id")]
    public long Id { get; set; }

    [Column("Name")]
    public string Name { get; set; } = "";

    [Column("Qty")]
    public int Qty { get; set; }
}

/// <summary>定向探针（2026-10-25）：事务内逐行 InsertAsync 的每行会话机械对地板的差距
/// 分解——四臂基准 SQLite EnumInserts x1.16 / BulkUpdate x1.08（边缘）的归因与收口。
/// 形态矩阵（每轮一个事务）：
///   插入族（100 行/轮）：
///     ins-ado-tostring  地板：单命令复用 + ToString 枚举转换（基准地板臂形态）
///     ins-ado-switch    地板 + 6 臂 switch 常量（隔离 ToString 成本，EnumStr 等价形态）
///     ins-per-row       产品推荐用法：WithTransaction + 逐行 InsertAsync（PL-2 晋升后）
///     ins-session-batch 产品 CreateBatch + Append ×100 + ExecuteNonQueryAsync
///   更新族（2000 行/轮）：
///     upd-ado           地板：单命令复用逐行 UPDATE
///     upd-bulk          产品 BulkUpdateAsync（SQLite = 逐行裹事务的既定路径）
/// 口径：warmup 50 轮 + timed 300 轮取中位（插入族）/ warmup 10 + timed 40（更新族），
/// µs/行；分配 GC.GetTotalAllocatedBytes 差值。</summary>
internal static class Program
{
    private const string Pragma =
        "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL; PRAGMA synchronous = NORMAL; "
        + "PRAGMA cache_size = -65536; PRAGMA temp_store = MEMORY; PRAGMA wal_autocheckpoint = 1000; "
        + "PRAGMA mmap_size = 268435456; PRAGMA analysis_limit = 400; PRAGMA optimize = 0x10002";

    private const int InsRows = 100;
    private const int InsWarmup = 50;
    private const int InsTimed = 300;
    private const int UpdRows = 2000;
    private const int UpdWarmup = 10;
    private const int UpdTimed = 40;

    private static long _nextId = 1;

    private static async Task<int> Main()
    {
        SQLitePCL.Batteries_V2.Init();
        string db = Path.Combine(Path.GetTempPath(), $"enum-probe-{Environment.ProcessId}.db");
        DeleteAll(db);
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        await conn.OpenAsync();
        await ExecAsync(conn, Pragma);
        await ExecAsync(conn,
            "CREATE TABLE probe_enum (\"Id\" INTEGER PRIMARY KEY, \"Name\" TEXT NOT NULL, \"Status\" TEXT NOT NULL)");
        await ExecAsync(conn,
            "CREATE TABLE probe_upd (\"Id\" INTEGER PRIMARY KEY, \"Name\" TEXT NOT NULL, \"Qty\" INTEGER NOT NULL)");
        await using var seedCmd = conn.CreateCommand();
        seedCmd.CommandText =
            "INSERT INTO probe_upd (\"Id\",\"Name\",\"Qty\") VALUES (@i,@n,@q)";
        var pi = seedCmd.CreateParameter(); pi.ParameterName = "@i";
        var pn = seedCmd.CreateParameter(); pn.ParameterName = "@n";
        var pq = seedCmd.CreateParameter(); pq.ParameterName = "@q";
        seedCmd.Parameters.Add(pi); seedCmd.Parameters.Add(pn); seedCmd.Parameters.Add(pq);
        await using var seedTx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync();
        seedCmd.Transaction = seedTx;
        for (long i = 1; i <= UpdRows; i++)
        {
            pi.Value = i; pn.Value = $"row-{i}"; pq.Value = (int)(i % 1000);
            await seedCmd.ExecuteNonQueryAsync();
        }
        await seedTx.CommitAsync();

        var session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source={db}" });
        // 预热会话绑定到同一连接（CreateAsync 自建连接；本探针只测会话机械，连接口径一致）
        var results = new List<(string, double, double)>();

        Console.WriteLine($"插入族：{InsRows} 行/轮 × ({InsWarmup}+{InsTimed}) 轮；更新族：{UpdRows} 行/轮 × ({UpdWarmup}+{UpdTimed}) 轮");

        // 插入族
        await RunAsync(results, "ins-ado-tostring", InsWarmup, InsTimed, InsRows,
            () => InsAdoAsync(conn, useSwitch: false));
        await RunAsync(results, "ins-ado-switch", InsWarmup, InsTimed, InsRows,
            () => InsAdoAsync(conn, useSwitch: true));
        await RunAsync(results, "ins-per-row", InsWarmup, InsTimed, InsRows,
            () => InsPerRowAsync(session));
        await RunAsync(results, "ins-session-batch", InsWarmup, InsTimed, InsRows,
            () => InsSessionBatchAsync(session));

        // 更新族
        var entities = (await session.GetAllAsync<ProbeUpdRow>())
            .OrderBy(static r => r.Id).ToList();
        await RunAsync(results, "upd-ado", UpdWarmup, UpdTimed, UpdRows,
            () => UpdAdoAsync(conn));
        await RunAsync(results, "upd-bulk", UpdWarmup, UpdTimed, UpdRows,
            () => UpdBulkAsync(session, entities));

        Console.WriteLine();
        Console.WriteLine("| 形态 | µs/行 | B/行 |");
        Console.WriteLine("|---|---:|---:|");
        foreach ((string form, double us, double alloc) in results)
            Console.WriteLine($"| {form} | {us:F3} | {alloc:F0} |");
        double insAdo = results.First(r => r.Item1 == "ins-ado-switch").Item2;
        double insPerRow = results.First(r => r.Item1 == "ins-per-row").Item2;
        double insBatch = results.First(r => r.Item1 == "ins-session-batch").Item2;
        Console.WriteLine($"插入分解：per-row − 地板(switch) = {(insPerRow - insAdo) * 1000:F0} ns/行；session-batch − 地板 = {(insBatch - insAdo) * 1000:F0} ns/行");
        double updAdo = results.First(r => r.Item1 == "upd-ado").Item2;
        double updBulk = results.First(r => r.Item1 == "upd-bulk").Item2;
        Console.WriteLine($"更新分解：bulk − 地板 = {(updBulk - updAdo) * 1000:F0} ns/行");

        await session.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { DeleteAll(db); } catch (IOException) { }
        return 0;
    }

    private static void DeleteAll(string db)
    {
        foreach (string s in new[] { "", "-wal", "-shm" })
        {
            string f = db + s;
            if (File.Exists(f)) File.Delete(f);
        }
    }

    private static async Task RunAsync(
        List<(string, double, double)> results, string form,
        int warmup, int timed, int rowsPerRound, Func<Task> round)
    {
        for (int i = 0; i < warmup; i++) await round();
        var samples = new List<(double Ms, double Alloc)>(timed);
        for (int i = 0; i < timed; i++)
        {
            long a0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            await round();
            sw.Stop();
            samples.Add((sw.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - a0));
        }
        samples.Sort(static (a, b) => a.Ms.CompareTo(b.Ms));
        (double ms, double alloc) = samples[samples.Count / 2];
        double usPerRow = ms * 1000.0 / rowsPerRound;
        results.Add((form, usPerRow, alloc / rowsPerRound));
        Console.WriteLine($"{form,-18} {ms,9:F3} ms/round  {usPerRow,7:F3} µs/row  {alloc / rowsPerRound,7:F0} B/row");
    }

    // ── 插入族形态 ──

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task InsAdoAsync(DbConnection conn, bool useSwitch)
    {
        await using DbTransaction tx = await conn.BeginTransactionAsync();
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO probe_enum (\"Id\",\"Name\",\"Status\") VALUES (@i,@n,@s)";
        var p0 = cmd.CreateParameter(); p0.ParameterName = "@i";
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@n";
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@s";
        cmd.Parameters.Add(p0); cmd.Parameters.Add(p1); cmd.Parameters.Add(p2);
        for (int k = 0; k < InsRows; k++)
        {
            ProbeStatus st = (ProbeStatus)(k % 6);
            p0.Value = _nextId + k;
            p1.Value = "row-" + (_nextId + k).ToString(CultureInfo.InvariantCulture);
            p2.Value = useSwitch ? SwitchName(st) : st.ToString();
            await cmd.ExecuteNonQueryAsync();
        }
        _nextId += InsRows;
        await tx.CommitAsync();
    }

    private static string SwitchName(ProbeStatus s) => s switch
    {
        ProbeStatus.Pending => "Pending",
        ProbeStatus.Processing => "Processing",
        ProbeStatus.Paid => "Paid",
        ProbeStatus.Shipped => "Shipped",
        ProbeStatus.Delivered => "Delivered",
        _ => "Cancelled",
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task InsPerRowAsync(DataSession<SqliteProvider> session)
    {
        long baseId = _nextId;
        _nextId += InsRows;
        await session.WithTransaction<object?>(async ct =>
        {
            for (int k = 0; k < InsRows; k++)
                await session.InsertAsync(new ProbeRow
                {
                    Id = baseId + k,
                    Name = "row-" + (baseId + k).ToString(CultureInfo.InvariantCulture),
                    Status = (ProbeStatus)(k % 6),
                }, ct);
            return null;
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task InsSessionBatchAsync(DataSession<SqliteProvider> session)
    {
        long baseId = _nextId;
        _nextId += InsRows;
        await session.WithTransaction<object?>(async ct =>
        {
            using var batch = session.CreateBatch();
            for (int k = 0; k < InsRows; k++)
                batch.Append($"INSERT INTO probe_enum (\"Id\",\"Name\",\"Status\") VALUES ({baseId + k}, {"row-" + (baseId + k).ToString(CultureInfo.InvariantCulture)}, {SwitchName((ProbeStatus)(k % 6))})");
            await batch.ExecuteNonQueryAsync(ct);
            return null;
        });
    }

    // ── 更新族形态 ──

    private static int _round;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task UpdAdoAsync(DbConnection conn)
    {
        int v = _round++;
        await using DbTransaction tx = await conn.BeginTransactionAsync();
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE probe_upd SET \"Name\"=@n, \"Qty\"=@q WHERE \"Id\"=@i";
        var pn = cmd.CreateParameter(); pn.ParameterName = "@n";
        var pq = cmd.CreateParameter(); pq.ParameterName = "@q";
        var pi = cmd.CreateParameter(); pi.ParameterName = "@i";
        cmd.Parameters.Add(pn); cmd.Parameters.Add(pq); cmd.Parameters.Add(pi);
        for (long i = 1; i <= UpdRows; i++)
        {
            pn.Value = "u" + ((i + v) % 97).ToString(CultureInfo.InvariantCulture);
            pq.Value = (int)((i + v) % 1000);
            pi.Value = i;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task UpdBulkAsync(DataSession<SqliteProvider> session, List<ProbeUpdRow> entities)
    {
        int v = _round++;
        foreach (ProbeUpdRow r in entities)
        {
            r.Name = "u" + ((r.Id + v) % 97).ToString(CultureInfo.InvariantCulture);
            r.Qty = (int)((r.Id + v) % 1000);
        }
        await session.BulkUpdateAsync(entities);
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
