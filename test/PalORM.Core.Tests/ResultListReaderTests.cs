using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>ResultListReader（2026-10-02）：结果集物化内核的边界——空结果、恰好装满初始容量、
/// 首次溢出、多次扩容、首行回调只在有行时调用一次、物化异常原样上抛。
/// 用 SQLite 递归 CTE 现造 N 行，断言行序、行数与溢出路径的精确容量。</summary>
public sealed class ResultListReaderTests
{
    private sealed class Row
    {
        public long Id { get; init; }
    }

    private static Row Map(DbDataReader reader) => new() { Id = reader.GetInt64(0) };

    [Test]
    [Arguments(0, 16)]
    [Arguments(1, 16)]
    [Arguments(16, 16)]
    [Arguments(17, 16)]
    [Arguments(5000, 16)]
    [Arguments(300, 1)]
    public async Task ReadAll_ReturnsEveryRowInOrder(int rowCount, int initialCapacity)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using DbDataReader reader = await QueryRowsAsync(conn, rowCount);

        List<Row> rows = await ResultListReader.ReadAllAsync(reader, Map, initialCapacity, CancellationToken.None);

        await Assert.That(rows.Count).IsEqualTo(rowCount);
        for (int i = 0; i < rows.Count; i++)
            await Assert.That(rows[i].Id).IsEqualTo(i + 1);
        // 溢出路径一次精确分配；未溢出时容量即初始容量（与原 new List<T>(n) 形态一致）
        int expectedCapacity = rowCount > initialCapacity ? rowCount : initialCapacity;
        await Assert.That(rows.Capacity).IsEqualTo(expectedCapacity);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 1)]
    [Arguments(40, 1)]
    public async Task ReadAll_InvokesFirstRowCallbackOnlyWhenRowsExist(int rowCount, int expectedCalls)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using DbDataReader reader = await QueryRowsAsync(conn, rowCount);
        int calls = 0;

        _ = await ResultListReader.ReadAllAsync(reader, Map, 16, CancellationToken.None, _ => calls++);

        await Assert.That(calls).IsEqualTo(expectedCalls);
    }

    [Test]
    public async Task ReadAll_FactoryThrowsInOverflowPath_PropagatesOriginalException()
    {
        await using SqliteConnection conn = await OpenAsync();
        await using DbDataReader reader = await QueryRowsAsync(conn, 200);

        await Assert.That(async () => await ResultListReader.ReadAllAsync<Row>(
                reader,
                r => r.GetInt64(0) == 150 ? throw new InvalidOperationException("row 150") : Map(r),
                16,
                CancellationToken.None))
            .Throws<InvalidOperationException>()
            .WithMessage("row 150", StringComparison.Ordinal);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<DbDataReader> QueryRowsAsync(SqliteConnection conn, int rowCount)
    {
        DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "WITH RECURSIVE s(i) AS (SELECT 1 WHERE @n > 0 UNION ALL SELECT i + 1 FROM s WHERE i < @n) "
            + "SELECT i FROM s ORDER BY i";
        DbParameter n = cmd.CreateParameter();
        n.ParameterName = "@n";
        n.Value = rowCount;
        cmd.Parameters.Add(n);
        return await cmd.ExecuteReaderAsync();
    }
}
