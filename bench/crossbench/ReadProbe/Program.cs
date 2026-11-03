using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ReadProbe;

/// <summary>SQLite 读侧批量形态裁决探针（2026-11-03）。
/// <para><b>要回答的问题</b>：账本记录的 0.21~0.25µs/键 是**删除**形态的键成本
/// （DeleteProbe 2026-10-25）。读侧是否同量级收益未知——读有逐行物化成本，
/// 删除没有。本探针用同键段、同表形、同物化，把命令开销单独量出来。</para>
/// <para><b>形态矩阵</b>（每形态独立读同一键段，只读不改，故无需重播种）：
/// PerKey-NewCmd  每键新建命令（未晋升形态：命令新建 + 参数绑定 + reader 打开，每键一次）
/// PerKey-Reuse   单命令跨键复用，只改参数值（PL-2 晋升后的稳态形态）
/// Param-IN-999   参数化 IN，999 键/批（PalORM WhereIn 现形态）
/// JsonEach-1P    单参数 json_each（批量删除在用、读侧未用的形态）</para>
/// <para><b>公平性纪律</b>：四形态物化路径逐位相同（同一复用 List、同一 typed getter、
/// 同一行类），List 每轮 Clear 复用，故计时区不含 List 分配。差值即命令侧开销。</para>
/// <para>口径：2 预热 + 10 计时取中位，与 UpsertProbe/DeleteProbe 同法。</para></summary>
internal static class Program
{
    private const string Pragma =
        "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL; PRAGMA synchronous = NORMAL; "
        + "PRAGMA cache_size = -65536; PRAGMA temp_store = MEMORY; PRAGMA wal_autocheckpoint = 1000; "
        + "PRAGMA mmap_size = 268435456; PRAGMA analysis_limit = 400; PRAGMA optimize = 0x10002";

    private const int Warmup = 2;
    private const int Timed = 10;
    private static readonly int[] Scales = [100, 1000];

    private static int Main()
    {
        SQLitePCL.Batteries_V2.Init();
        string db = Path.Combine(Path.GetTempPath(), $"read-probe-{Environment.ProcessId}.db");
        Delete(db);
        using var conn = new SqliteConnection($"Data Source={db}");
        conn.Open();
        Exec(conn, Pragma);
        Exec(conn, "CREATE TABLE perf_s1 (\"Id\" INTEGER PRIMARY KEY, \"Name\" TEXT NOT NULL, "
            + "\"Qty\" INTEGER NOT NULL, \"Price\" TEXT NOT NULL, \"Marker\" INTEGER NOT NULL)");

        Console.WriteLine();
        Console.WriteLine($"warmup={Warmup}  timed={Timed}（中位）  物化路径四形态相同");
        Console.WriteLine("| 档 | 形态 | 中位 ms | µs/键 | 分配 B/键 |");
        Console.WriteLine("|---|---|---:|---:|---:|");

        foreach (int scale in Scales)
        {
            Seed(conn, scale);
            foreach (string form in new[] { "PerKey-NewCmd", "PerKey-Reuse", "Param-IN-999", "JsonEach-1P" })
            {
                Run(conn, form, scale);
            }
        }

        SqliteConnection.ClearAllPools();
        conn.Dispose();
        try { Delete(db); } catch (IOException) { }
        return 0;
    }

    /// <summary>播种 1..scale 键段（只读探针，一次播种跨形态复用）。</summary>
    private static void Seed(SqliteConnection conn, int scale)
    {
        Exec(conn, "DELETE FROM perf_s1");
        using DbTransaction tran = conn.BeginTransaction();
        using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = "INSERT INTO perf_s1 (\"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\") "
            + "VALUES (@id, @name, @qty, @price, @marker)";
        DbParameter pId = cmd.CreateParameter();
        pId.ParameterName = "@id";
        DbParameter pName = cmd.CreateParameter();
        pName.ParameterName = "@name";
        DbParameter pQty = cmd.CreateParameter();
        pQty.ParameterName = "@qty";
        DbParameter pPrice = cmd.CreateParameter();
        pPrice.ParameterName = "@price";
        DbParameter pMarker = cmd.CreateParameter();
        pMarker.ParameterName = "@marker";
        cmd.Parameters.Add(pId);
        cmd.Parameters.Add(pName);
        cmd.Parameters.Add(pQty);
        cmd.Parameters.Add(pPrice);
        cmd.Parameters.Add(pMarker);
        for (int i = 1; i <= scale; i++)
        {
            pId.Value = (long)i;
            pName.Value = "name-" + i.ToString(CultureInfo.InvariantCulture);
            pQty.Value = (long)(i % 100);
            pPrice.Value = "12.34";
            pMarker.Value = (long)(i % 7);
            cmd.ExecuteNonQuery();
        }

        tran.Commit();
    }

    private static void Run(SqliteConnection conn, string form, int scale)
    {
        var samples = new List<(double Ms, double Alloc)>(Timed);
        // 物化目标：复用同一 List（每轮 Clear），四形态共享，故计时区无 List 分配
        var rows = new List<Row>(scale);
        for (int round = 0; round < Warmup + Timed; round++)
        {
            rows.Clear();
            long allocBefore = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();

            switch (form)
            {
                case "PerKey-NewCmd":
                    ReadPerKeyNewCommand(conn, scale, rows);
                    break;
                case "PerKey-Reuse":
                    ReadPerKeyReuseCommand(conn, scale, rows);
                    break;
                case "Param-IN-999":
                    ReadParamIn(conn, scale, rows);
                    break;
                case "JsonEach-1P":
                    ReadJsonEach(conn, scale, rows);
                    break;
            }

            sw.Stop();
            long alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;
            if (round >= Warmup) samples.Add((sw.Elapsed.TotalMilliseconds, alloc));
        }

        // 正确性守卫：四形态必须都读到完整键段，且抽样值正确
        if (rows.Count != scale)
            throw new InvalidOperationException($"{form}/{scale} 行数守卫失败：读到 {rows.Count}");
        if (rows[0].Id != 1 || rows[^1].Id != scale)
            throw new InvalidOperationException(
                $"{form}/{scale} 键段守卫失败：首 {rows[0].Id} 末 {rows[^1].Id}");
        foreach (Row r in rows)
        {
            if (r.Qty != (int)(r.Id % 100) || r.Marker != (int)(r.Id % 7))
                throw new InvalidOperationException($"{form}/{scale} 值守卫失败：Id={r.Id}");
        }

        samples.Sort(static (a, b) => a.Ms.CompareTo(b.Ms));
        (double ms, double medianAlloc) = samples[samples.Count / 2];
        Console.WriteLine($"| {scale,4:N0} | {form} | {ms:F3} | {ms * 1000.0 / scale:F2} | {medianAlloc / scale:F0} |");
    }

    /// <summary>每键新建命令：未晋升形态（命令对象 + 参数对象 + reader 每键一次）。</summary>
    private static void ReadPerKeyNewCommand(SqliteConnection conn, int scale, List<Row> rows)
    {
        for (int i = 1; i <= scale; i++)
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = SelectSql;
            DbParameter p = cmd.CreateParameter();
            p.ParameterName = "@id";
            p.Value = (long)i;
            cmd.Parameters.Add(p);
            using DbDataReader reader = cmd.ExecuteReader();
            if (reader.Read()) rows.Add(ReadRow(reader));
        }
    }

    /// <summary>单命令跨键复用：PL-2 晋升后的稳态形态（只改参数值，命令与参数对象复用）。</summary>
    private static void ReadPerKeyReuseCommand(SqliteConnection conn, int scale, List<Row> rows)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql;
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = "@id";
        cmd.Parameters.Add(p);
        for (int i = 1; i <= scale; i++)
        {
            p.Value = (long)i;
            using DbDataReader reader = cmd.ExecuteReader();
            if (reader.Read()) rows.Add(ReadRow(reader));
        }
    }

    /// <summary>参数化 IN，999 键/批（WhereIn 现形态：每键一个参数对象）。</summary>
    private static void ReadParamIn(SqliteConnection conn, int scale, List<Row> rows)
    {
        const int batch = 999;
        for (int start = 1; start <= scale; start += batch)
        {
            int size = Math.Min(batch, scale - start + 1);
            var sb = new StringBuilder(24 * size);
            sb.Append("SELECT \"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\" FROM perf_s1 WHERE \"Id\" IN (");
            for (int i = 0; i < size; i++) sb.Append(i > 0 ? ",@p" : "@p").Append(i);
            sb.Append(')');
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sb.ToString();
            DbParameter[] ps = new DbParameter[size];
            for (int i = 0; i < size; i++)
            {
                ps[i] = cmd.CreateParameter();
                ps[i].ParameterName = $"@p{i}";
                cmd.Parameters.Add(ps[i]);
            }

            for (int i = 0; i < size; i++) ps[i].Value = (long)(start + i);
            using DbDataReader reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(ReadRow(reader));
        }
    }

    /// <summary>单参数 json_each（全参数化 + 单绑定，SQL 文本与值数解耦）。</summary>
    private static void ReadJsonEach(SqliteConnection conn, int scale, List<Row> rows)
    {
        var jsonSb = new StringBuilder(8 * scale + 2).Append('[');
        for (int i = 1; i <= scale; i++)
        {
            if (i > 1) jsonSb.Append(',');
            jsonSb.Append(i.ToString(CultureInfo.InvariantCulture));
        }

        string json = jsonSb.Append(']').ToString();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\" FROM perf_s1 "
            + "WHERE \"Id\" IN (SELECT value FROM json_each(@ids))";
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = "@ids";
        p.Value = json;
        cmd.Parameters.Add(p);
        using DbDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadRow(reader));
    }

    private const string SelectSql =
        "SELECT \"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\" FROM perf_s1 WHERE \"Id\" = @id";

    /// <summary>typed getter 物化（镜像 RowFactory emit 形态：逐列定型读取，无装箱）。</summary>
    private static Row ReadRow(DbDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4));

    private sealed record Row(long Id, string Name, int Qty, string Price, int Marker);

    private static void Exec(DbConnection conn, string sql)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Delete(string db)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string f = db + suffix;
            if (File.Exists(f)) File.Delete(f);
        }
    }
}
