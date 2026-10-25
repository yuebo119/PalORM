using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DeleteProbe;

/// <summary>C 方案裁决探针——SQLite 批量删除三形态每键成本（2026-10-25）。
/// 形态矩阵（各形态独立键段，DeleteAsync 全键段，事务包裹）：
///   Param-IN       参数化 IN 999 键/批（PalORM/地板现形态：每键一个参数对象+绑定）
///   Literal-IN     字面量内联（FreeSql NoneParameter 真实形态：值拼进 SQL 文本）
///   JsonEach-1P    单参数 json_each：DELETE ... WHERE "Id" IN (SELECT value FROM
///                  json_each(@ids))，@ids 为 JSON 数组文本（全参数化 + 单绑定）
/// 口径：每轮删 2000 键，2 预热 + 10 计时取中位；与 UpsertProbe 同法。</summary>
internal static class Program
{
    private const string Pragma =
        "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL; PRAGMA synchronous = NORMAL; "
        + "PRAGMA cache_size = -65536; PRAGMA temp_store = MEMORY; PRAGMA wal_autocheckpoint = 1000; "
        + "PRAGMA mmap_size = 268435456; PRAGMA analysis_limit = 400; PRAGMA optimize = 0x10002";

    private const int Keys = 2000;
    private const int Warmup = 2;
    private const int Timed = 10;

    private static int Main()
    {
        SQLitePCL.Batteries_V2.Init();
        string db = Path.Combine(Path.GetTempPath(), $"del-probe-{Environment.ProcessId}.db");
        Delete(db);
        using var conn = new SqliteConnection($"Data Source={db}");
        conn.Open();
        Exec(conn, Pragma);
        Exec(conn, "CREATE TABLE perf_s1 (\"Id\" INTEGER PRIMARY KEY, \"Name\" TEXT NOT NULL, "
            + "\"Qty\" INTEGER NOT NULL, \"Price\" TEXT NOT NULL, \"Marker\" INTEGER NOT NULL)");

        var results = new List<(string Form, double MedianMs, double UsPerKey, double AllocPerKey)>();
        int ordinal = 0;
        foreach (string form in new[] { "Param-IN", "Literal-IN", "JsonEach-1P" })
        {
            Run(conn, results, ordinal * 1_000_000L, form);
            ordinal++;
        }

        Console.WriteLine();
        Console.WriteLine($"keys/round={Keys:N0}  warmup={Warmup}  timed={Timed}（中位）  batch(Param-IN)=999");
        Console.WriteLine("| 形态 | 中位 ms | µs/键 | 分配 B/键 |");
        Console.WriteLine("|---|---:|---:|---:|");
        foreach ((string form, double ms, double us, double alloc) in results)
        {
            Console.WriteLine($"| {form} | {ms:F2} | {us:F2} | {alloc:F0} |");
        }

        SqliteConnection.ClearAllPools();
        conn.Dispose();
        try { Delete(db); } catch (IOException) { }
        return 0;
    }

    private static void Delete(string db)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string f = db + suffix;
            if (File.Exists(f)) File.Delete(f);
        }
    }

    private static void Run(
        SqliteConnection conn,
        List<(string, double, double, double)> results,
        long keyBase,
        string form)
    {
        var samples = new List<(double Ms, double Alloc)>(Timed);
        for (int round = 0; round < Warmup + Timed; round++)
        {
            // 每轮补种本形态键段（上一轮已删）：键 keyBase+1 .. keyBase+Keys
            Exec(conn, $"INSERT INTO perf_s1 SELECT \"Id\"+{Keys}, \"Name\", \"Qty\", \"Price\", \"Marker\" "
                + $"FROM perf_s1 WHERE \"Id\" BETWEEN {keyBase + 1} AND {keyBase + Keys}");
            long allocBefore = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();

            using DbTransaction tran = conn.BeginTransaction();
            switch (form)
            {
                case "Param-IN":
                    RunParamIn(conn, tran, keyBase);
                    break;
                case "Literal-IN":
                    RunLiteralIn(conn, tran, keyBase);
                    break;
                case "JsonEach-1P":
                    RunJsonEach(conn, tran, keyBase);
                    break;
            }

            tran.Commit();
            sw.Stop();
            long alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;
            if (round >= Warmup) samples.Add((sw.Elapsed.TotalMilliseconds, alloc));
        }

        // 正确性守卫：本轮删完后键段应为空
        object? count = Scalar(conn,
            $"SELECT COUNT(*) FROM perf_s1 WHERE \"Id\" BETWEEN {keyBase + 1} AND {keyBase + Keys}");
        if (Convert.ToInt64(count, CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException($"{form} 删除守卫失败：残留 {count}");

        samples.Sort(static (a, b) => a.Ms.CompareTo(b.Ms));
        (double ms, double medianAlloc) = samples[samples.Count / 2];
        results.Add((form, ms, ms * 1000.0 / Keys, medianAlloc / Keys));
        Console.WriteLine($"{form,-12} {ms,8:F2} ms  {ms * 1000.0 / Keys,6:F2} µs/key  {medianAlloc / Keys,8:F0} B/key");
    }

    /// <summary>参数化 IN（PalORM/地板现形态）：999 键/批，命令与参数池每批建立后跨批复用值。</summary>
    private static void RunParamIn(SqliteConnection conn, DbTransaction tran, long keyBase)
    {
        const int batch = 999;
        for (int start = 1; start <= Keys; start += batch)
        {
            int size = Math.Min(batch, Keys - start + 1);
            using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            var sb = new StringBuilder(16 * size);
            sb.Append("DELETE FROM perf_s1 WHERE \"Id\" IN (");
            for (int i = 0; i < size; i++) sb.Append(i > 0 ? ",@p" : "@p").Append(i);
            sb.Append(')');
            cmd.CommandText = sb.ToString();
            DbParameter[] ps = new DbParameter[size];
            for (int i = 0; i < size; i++)
            {
                ps[i] = cmd.CreateParameter();
                ps[i].ParameterName = $"@p{i}";
                cmd.Parameters.Add(ps[i]);
            }

            for (int i = 0; i < size; i++) ps[i].Value = keyBase + start + i;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>字面量内联（FreeSql 形态）：值拼进 SQL 文本，无参数对象。</summary>
    private static void RunLiteralIn(SqliteConnection conn, DbTransaction tran, long keyBase)
    {
        const int batch = 999;
        for (int start = 1; start <= Keys; start += batch)
        {
            int size = Math.Min(batch, Keys - start + 1);
            var sb = new StringBuilder(16 * size);
            sb.Append("DELETE FROM perf_s1 WHERE \"Id\" IN (");
            for (int i = 0; i < size; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append((keyBase + start + i).ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(')');
            using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>单参数 json_each：@ids 传 JSON 数组文本，服务端展开（全参数化 + 单绑定）。</summary>
    private static void RunJsonEach(SqliteConnection conn, DbTransaction tran, long keyBase)
    {
        long[] keys = new long[Keys];
        for (int i = 0; i < Keys; i++) keys[i] = keyBase + 1 + i;
        // long[] → "[1,2,...]"：手写拼接（探针工具，避开反射序列化的 IL2026/3050）
        var jsonSb = new StringBuilder(8 * Keys + 2).Append('[');
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0) jsonSb.Append(',');
            jsonSb.Append(keys[i].ToString(CultureInfo.InvariantCulture));
        }

        string json = jsonSb.Append(']').ToString();
        using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = "DELETE FROM perf_s1 WHERE \"Id\" IN (SELECT value FROM json_each(@ids))";
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = "@ids";
        p.Value = json;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection conn, string sql)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
