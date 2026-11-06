using System.Globalization;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>共享表全表对账（修订版方案 §4 基建 2）。
/// <para><b>补的缺口</b>：<c>SessionFanOutStressTests.cs:49-50</c> 只核总数不核值。
/// 本类整表读回后按主键分区，逐行核 Owner 与 Seq，使「写者互相覆盖 / 主键串到别人的行 /
/// 丢行 / 重复行」都能在具体值上被抓到。</para>
/// <para><b>判据独立性</b>：表名与列名全部硬编码，不复用 PalORM_Runtime 或 [Column] 注解，
/// 否则产品元数据错时两侧同错，判据失去判别力。</para></summary>
internal static class ConcurrencyRowVerifier
{
    internal const string TableName = "cs_shared_rows";

    internal const string CreateTableSql =
        "CREATE TABLE cs_shared_rows (" +
        "Id INTEGER PRIMARY KEY, " +
        "Owner INTEGER NOT NULL, " +
        "Seq INTEGER NOT NULL, " +
        "Marker TEXT NOT NULL)";

    /// <summary>建专用表。走裸 ADO，不经过产品 Migrate。</summary>
    internal static async ValueTask CreateTableAsync(SharedSqliteDb shared, CancellationToken ct = default)
        => await shared.ExecuteRawAsync(CreateTableSql, ct: ct).ConfigureAwait(false);

    /// <summary>整表读回并逐行对账。
    /// <param name="shared">共享内存库夹具。</param>
    /// <param name="expectedRows">期望总行数（写者数 × 每写者行数）。</param>
    /// <param name="workers">写者数。</param>
    /// <param name="rowsPerWorker">每写者行数；Seq 必须在 0..rowsPerWorker-1。</param>
    /// <param name="marker">本轮专属标记，防止读到别的用例遗留行。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>差异清单，空表示全部对账通过。</returns></summary>
    internal static async ValueTask<IReadOnlyList<string>> VerifyAllRowsAsync(
        SharedSqliteDb shared,
        int expectedRows,
        int workers,
        int rowsPerWorker,
        string marker,
        CancellationToken ct = default)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await shared.QueryRawAsync(
            "SELECT Id, Owner, Seq, Marker FROM cs_shared_rows", ct: ct).ConfigureAwait(false);

        var differences = new List<string>();
        if (rows.Count != expectedRows)
            differences.Add($"行数 {rows.Count}，期望 {expectedRows}");

        CheckRows(rows, workers, rowsPerWorker, marker, differences);
        CheckCompleteness(rows, workers, rowsPerWorker, differences);
        return differences;
    }

    private static void CheckRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        int workers,
        int rowsPerWorker,
        string marker,
        List<string> differences)
    {
        var seenKeys = new HashSet<long>();
        var seenPairs = new HashSet<(long Owner, long Seq)>();
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            long id = Convert.ToInt64(row["Id"], CultureInfo.InvariantCulture);
            long owner = Convert.ToInt64(row["Owner"], CultureInfo.InvariantCulture);
            long seq = Convert.ToInt64(row["Seq"], CultureInfo.InvariantCulture);
            string rowMarker = Convert.ToString(row["Marker"], CultureInfo.InvariantCulture) ?? "";

            if (!seenKeys.Add(id))
                differences.Add($"重复主键 {id}");
            if (!seenPairs.Add((owner, seq)))
                differences.Add($"重复 (Owner,Seq)=({owner},{seq})");
            CheckBounds(id, owner, seq, workers, rowsPerWorker, differences);
            if (!string.Equals(rowMarker, marker, StringComparison.Ordinal))
                differences.Add($"Id={id} 的 Marker={rowMarker}，期望 {marker}");
        }
    }

    private static void CheckBounds(
        long id, long owner, long seq, int workers, int rowsPerWorker, List<string> differences)
    {
        if (owner < 0 || owner >= workers)
            differences.Add($"Id={id} 的 Owner={owner} 越界（写者数 {workers}）");
        if (seq < 0 || seq >= rowsPerWorker)
            differences.Add($"Id={id} 的 Seq={seq} 越界（每写者 {rowsPerWorker} 行）");
    }

    /// <summary>逐写者核 Seq 完整性：0..rowsPerWorker-1 一个不少，才算没有静默丢行。</summary>
    private static void CheckCompleteness(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        int workers,
        int rowsPerWorker,
        List<string> differences)
    {
        var byOwner = new Dictionary<int, HashSet<long>>();
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            int owner = Convert.ToInt32(row["Owner"], CultureInfo.InvariantCulture);
            if (!byOwner.TryGetValue(owner, out HashSet<long>? seqs))
                byOwner[owner] = seqs = [];
            seqs.Add(Convert.ToInt64(row["Seq"], CultureInfo.InvariantCulture));
        }

        for (int worker = 0; worker < workers; worker++)
        {
            if (!byOwner.TryGetValue(worker, out HashSet<long>? seqs))
                seqs = [];
            for (int seq = 0; seq < rowsPerWorker; seq++)
                if (!seqs.Contains(seq))
                    differences.Add($"写者 {worker} 缺 Seq={seq}");
        }
    }

    /// <summary>写一行。走裸 ADO，绕开产品写路径，使期望值独立于被测代码。</summary>
    internal static async ValueTask InsertRowAsync(
        SharedSqliteDb shared,
        long id,
        int owner,
        int seq,
        string marker,
        CancellationToken ct = default)
        => await shared.ExecuteRawAsync(
            "INSERT INTO cs_shared_rows (Id, Owner, Seq, Marker) VALUES ($id, $owner, $seq, $marker)",
            [("id", id), ("owner", owner), ("seq", seq), ("marker", marker)],
            ct).ConfigureAwait(false);

    /// <summary>写一行到指定连接（并发写专用）。
    /// <para><b>为什么必须每 worker 一条连接</b>：<c>Microsoft.Data.Sqlite</c> 的单个
    /// <c>SqliteConnection</c> 不支持并发命令；共用 keeper 连接并发写会在
    /// <c>SqliteConnection.Close()</c> 触发 NRE（实测）。共享缓存内存库允许多连接同库，
    /// 每条连接各自开命令才构成真实并发写。</para></summary>
    internal static async ValueTask InsertRowAsync(
        System.Data.Common.DbConnection connection,
        long id,
        int owner,
        int seq,
        string marker,
        CancellationToken ct = default)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);
        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO cs_shared_rows (Id, Owner, Seq, Marker) VALUES ($id, $owner, $seq, $marker)";
        foreach ((string name, object value) in new[]
        {
            ("$id", (object)id), ("$owner", owner), ("$seq", seq), ("$marker", marker),
        })
        {
            System.Data.Common.DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
