using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace UpsertProbe;

/// <summary>B 探针——SQLite 批量形态每行成本实测（2026-10-25，v2 修正口径）。
/// 形态矩阵（全部对齐 PerfHub 地板纪律「命令与参数池一次建好、跨批只写 Value」）：
///   MV-Param-INSERT    参数化多值 INSERT（1000 行/批，PalORM/地板现形态）
///   Row-Prepared-INSERT 预编译单行 INSERT（一命令 5 参数循环改值，A 方案）
///   MV-Literal-INSERT  字面量多值 INSERT（FreeSql TxBulkInsert 参照形态，无参数）
///   MV-Param-Upsert-*  参数化多值 ON CONFLICT DO UPDATE（199 行/批，PalORM 现形态）
///   Row-Prepared-Upsert-* 预编译单行 ON CONFLICT DO UPDATE（B 方案）
///   *-InsertPath = 键不存在（插入分支，每轮换键段）；*-UpdatePath = 键已存在（更新分支，固定键段）
/// 口径：2000 行/轮，2 轮预热 + 10 轮计时取中位；每轮一个事务；命令/参数池每轮建一次
///（对应真实 per-call 实现）；各形态独立键段（formOrdinal * 1M）。</summary>
internal static class Program
{
    private const string Pragma =
        "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL; PRAGMA synchronous = NORMAL; "
        + "PRAGMA cache_size = -65536; PRAGMA temp_store = MEMORY; PRAGMA wal_autocheckpoint = 1000; "
        + "PRAGMA mmap_size = 268435456; PRAGMA analysis_limit = 400; PRAGMA optimize = 0x10002";

    private const int Rows = 2000;
    private const int WarmupRounds = 2;
    private const int TimedRounds = 10;
    private const string ColList = "\"Id\",\"Name\",\"Qty\",\"Price\",\"Marker\"";
    private const string UpsertSuffix = " ON CONFLICT(\"Id\") DO UPDATE SET "
        + "\"Name\"=excluded.\"Name\",\"Qty\"=excluded.\"Qty\","
        + "\"Price\"=excluded.\"Price\",\"Marker\"=excluded.\"Marker\"";

    private static int Main()
    {
        SQLitePCL.Batteries_V2.Init();
        string db = Path.Combine(Path.GetTempPath(), $"upsert-probe-{Environment.ProcessId}.db");
        Delete(db);
        using var conn = new SqliteConnection($"Data Source={db}");
        conn.Open();
        Exec(conn, Pragma);
        Exec(conn,
            "CREATE TABLE perf_s1 (\"Id\" INTEGER PRIMARY KEY, \"Name\" TEXT NOT NULL, "
            + "\"Qty\" INTEGER NOT NULL, \"Price\" TEXT NOT NULL, \"Marker\" INTEGER NOT NULL)");

        var results = new List<(string Form, double MedianMs, double UsPerRow, double AllocPerRow)>();
        int formOrdinal = 0;
        foreach (var (form, kind, upsert, updatePath) in new[]
        {
            ("MV-Param-INSERT", "mv", false, false),
            ("Row-Prepared-INSERT", "row", false, false),
            ("MV-Literal-INSERT", "literal", false, false),
            ("MV-Param-Upsert-InsertPath", "mv", true, false),
            ("Row-Prepared-Upsert-InsertPath", "row", true, false),
            ("MV-Param-Upsert-UpdatePath", "mv", true, true),
            ("Row-Prepared-Upsert-UpdatePath", "row", true, true),
            ("Dapper-MultiExec-Sim", "multiexec", false, false),
        })
        {
            Run(conn, results, formOrdinal * 1_000_000L, form, kind, upsert, updatePath);
            formOrdinal++;
        }

        Console.WriteLine();
        Console.WriteLine($"rows/round={Rows:N0}  warmup={WarmupRounds}  timed={TimedRounds}（中位）  batch: mv-insert=1000, mv-upsert=199");
        Console.WriteLine("| 形态 | 中位 ms | µs/行 | 分配 B/行 |");
        Console.WriteLine("|---|---:|---:|---:|");
        foreach ((string form, double ms, double us, double alloc) in results)
        {
            Console.WriteLine($"| {form} | {ms:F2} | {us:F2} | {alloc:F0} |");
        }

        SqliteConnection.ClearAllPools();
        conn.Dispose();
        try
        {
            Delete(db);
        }
        catch (IOException)
        {
            // 清理失败不影响结果（临时目录文件）
        }

        return 0;
    }

    private static void Delete(string db)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string f = db + suffix;
            if (File.Exists(f))
            {
                File.Delete(f);
            }
        }
    }

    private sealed record Row(long Id, string Name, int Qty, decimal Price, long Marker);

    private static List<Row> Seed(long offset, int count)
    {
        var list = new List<Row>(count);
        for (long i = 0; i < count; i++)
        {
            list.Add(new Row(offset + i + 1, $"row-{i}", (int)(i % 1000), i * 0.25m, i));
        }

        return list;
    }

    private static void Run(
        SqliteConnection conn,
        List<(string, double, double, double)> results,
        long keyBase,
        string form,
        string kind,
        bool upsert,
        bool updatePath)
    {
        var samples = new List<(double Ms, double Alloc)>(TimedRounds);
        for (int round = 0; round < WarmupRounds + TimedRounds; round++)
        {
            // InsertPath 每轮换键段（不冲突）；UpdatePath 固定键段（全命中冲突分支）
            long offset = updatePath ? keyBase : keyBase + (long)(round + 1) * Rows;
            List<Row> rows = Seed(offset, Rows);
            long allocBefore = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();

            using DbTransaction tran = conn.BeginTransaction();
            switch (kind)
            {
                case "row":
                    RunRowPrepared(conn, tran, rows, upsert);
                    break;
                case "multiexec":
                    RunMultiExecSim(conn, tran, rows);
                    break;
                case "mv":
                    RunMultiValueParam(conn, tran, rows, upsert);
                    break;
                case "literal":
                    RunMultiValueLiteral(conn, tran, rows);
                    break;
            }

            tran.Commit();
            sw.Stop();
            long alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;
            if (round >= WarmupRounds)
            {
                samples.Add((sw.Elapsed.TotalMilliseconds, alloc));
            }
        }

        // 正确性守卫：InsertPath 的键段 offset = (round+1)*Rows（round 0..N-1），
        // 键上界 = keyBase + (N+1)*Rows；UpdatePath 固定键段 1..Rows
        int totalRounds = WarmupRounds + TimedRounds;
        long upper = keyBase + (updatePath ? Rows : (totalRounds + 1) * Rows);
        object? count = Scalar(conn,
            $"SELECT COUNT(*) FROM perf_s1 WHERE \"Id\" > {keyBase.ToString(CultureInfo.InvariantCulture)} "
            + $"AND \"Id\" <= {upper.ToString(CultureInfo.InvariantCulture)}");
        long expected = updatePath ? Rows : totalRounds * Rows;
        if (Convert.ToInt64(count, CultureInfo.InvariantCulture) != expected)
        {
            throw new InvalidOperationException($"{form} 行数校验失败：{count}（期望 {expected}）");
        }

        samples.Sort(static (a, b) => a.Ms.CompareTo(b.Ms));
        (double ms, double medianAlloc) = samples[samples.Count / 2];
        results.Add((form, ms, ms * 1000.0 / Rows, medianAlloc / Rows));
        Console.WriteLine($"{form,-34} {ms,8:F2} ms  {ms * 1000.0 / Rows,6:F2} µs/row  {medianAlloc / Rows,8:F0} B/row");
    }

    /// <summary>Dapper multi-exec 的等价形态（SqlMapper.ExecuteImpl 的集合重载语义）：
    /// 单次调用内一个命令对象跨行复用，每行 Parameters.Clear() 后重新 Add 五个参数，
    /// 再 ExecuteNonQuery——参数对象每行新建，命令文本不变（命中语句缓存）。</summary>
    private static void RunMultiExecSim(SqliteConnection conn, DbTransaction tran, List<Row> rows)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"INSERT INTO perf_s1 ({ColList}) VALUES (@p0,@p1,@p2,@p3,@p4)";
        foreach (Row r in rows)
        {
            cmd.Parameters.Clear();
            for (int c = 0; c < 5; c++)
            {
                DbParameter p = cmd.CreateParameter();
                p.ParameterName = $"@p{c}";
                p.Value = c switch
                {
                    0 => r.Id,
                    1 => r.Name,
                    2 => r.Qty,
                    3 => r.Price,
                    _ => r.Marker
                };
                cmd.Parameters.Add(p);
            }

            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A/B 方案形态：单行预编译语句，命令与 5 参数每轮建一次，循环只写值。</summary>
    private static void RunRowPrepared(SqliteConnection conn, DbTransaction tran, List<Row> rows, bool upsert)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText =
            $"INSERT INTO perf_s1 ({ColList}) VALUES (@p0,@p1,@p2,@p3,@p4)"
            + (upsert ? UpsertSuffix : "");
        DbParameter[] ps = new DbParameter[5];
        for (int i = 0; i < 5; i++)
        {
            ps[i] = cmd.CreateParameter();
            ps[i].ParameterName = $"@p{i}";
            cmd.Parameters.Add(ps[i]);
        }

        foreach (Row r in rows)
        {
            ps[0].Value = r.Id;
            ps[1].Value = r.Name;
            ps[2].Value = r.Qty;
            ps[3].Value = r.Price;
            ps[4].Value = r.Marker;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>PalORM/地板现形态：参数化多值。同宽批共享一个命令与参数池（建一次、
    /// 跨批只写 Value）——对齐地板「命令与参数池一次建好」的纪律，宽批在前窄尾批在后。</summary>
    private static void RunMultiValueParam(SqliteConnection conn, DbTransaction tran, List<Row> rows, bool upsert)
    {
        int batch = 199; // SQLite 有效批宽：999 参数上限 ÷ 5 列（BulkSql.EffectiveBatchRows 同口径）
        int fullBatches = rows.Count / batch;
        int trailing = rows.Count % batch;

        if (fullBatches > 0)
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = MultiValueSql(batch) + (upsert ? UpsertSuffix : "");
            DbParameter[] ps = new DbParameter[batch * 5];
            for (int i = 0; i < batch * 5; i++)
            {
                ps[i] = cmd.CreateParameter();
                ps[i].ParameterName = $"@p{i}";
                cmd.Parameters.Add(ps[i]);
            }

            for (int b = 0; b < fullBatches; b++)
            {
                int baseIdx = b * batch;
                for (int i = 0; i < batch; i++)
                {
                    Row r = rows[baseIdx + i];
                    ps[i * 5].Value = r.Id;
                    ps[i * 5 + 1].Value = r.Name;
                    ps[i * 5 + 2].Value = r.Qty;
                    ps[i * 5 + 3].Value = r.Price;
                    ps[i * 5 + 4].Value = r.Marker;
                }

                cmd.ExecuteNonQuery();
            }
        }

        if (trailing > 0)
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = MultiValueSql(trailing) + (upsert ? UpsertSuffix : "");
            DbParameter[] ps = new DbParameter[trailing * 5];
            for (int i = 0; i < trailing * 5; i++)
            {
                ps[i] = cmd.CreateParameter();
                ps[i].ParameterName = $"@p{i}";
                cmd.Parameters.Add(ps[i]);
            }

            int baseIdx = fullBatches * batch;
            for (int i = 0; i < trailing; i++)
            {
                Row r = rows[baseIdx + i];
                ps[i * 5].Value = r.Id;
                ps[i * 5 + 1].Value = r.Name;
                ps[i * 5 + 2].Value = r.Qty;
                ps[i * 5 + 3].Value = r.Price;
                ps[i * 5 + 4].Value = r.Marker;
            }

            cmd.ExecuteNonQuery();
        }
    }

    private static string MultiValueSql(int size)
    {
        var sb = new StringBuilder(48 * size);
        sb.Append("INSERT INTO perf_s1 (").Append(ColList).Append(") VALUES ");
        for (int i = 0; i < size; i++)
        {
            sb.Append(i > 0 ? ",(" : "(")
              .Append("@p").Append(i * 5).Append(",@p").Append(i * 5 + 1)
              .Append(",@p").Append(i * 5 + 2).Append(",@p").Append(i * 5 + 3)
              .Append(",@p").Append(i * 5 + 4).Append(')');
        }

        return sb.ToString();
    }

    /// <summary>FreeSql NoneParameter 参照形态：值内联进 SQL 文本，无参数对象。</summary>
    private static void RunMultiValueLiteral(SqliteConnection conn, DbTransaction tran, List<Row> rows)
    {
        const int batch = 1000;
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            var sb = new StringBuilder(64 * (end - start));
            sb.Append("INSERT INTO perf_s1 (").Append(ColList).Append(") VALUES ");
            for (int i = start; i < end; i++)
            {
                if (i > start)
                {
                    sb.Append(',');
                }

                sb.Append('(').Append(rows[i].Id.ToString(CultureInfo.InvariantCulture))
                  .Append(",'").Append(rows[i].Name).Append('\'')
                  .Append(',').Append(rows[i].Qty.ToString(CultureInfo.InvariantCulture))
                  .Append(",'").Append(rows[i].Price.ToString(CultureInfo.InvariantCulture)).Append('\'')
                  .Append(',').Append(rows[i].Marker.ToString(CultureInfo.InvariantCulture))
                  .Append(')');
            }

            using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        }
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
