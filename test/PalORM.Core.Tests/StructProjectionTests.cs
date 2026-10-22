using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

[Projection]
internal readonly record struct StructProjectionRow(long id, string name, long value);

/// <summary>结构体投影直查：行物化为连续结构体数组（每行零堆对象），列按声明序映射；
/// 与类投影的 RowFactory 生成路径共用、但不进表/CRUD 元数据。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "连接生命周期移交 DataSession；keeper 由测试释放。")]
public sealed class StructProjectionTests
{
    private static async Task<(DataSession<SqliteProvider> Session, SqliteConnection Keeper)> OpenAsync()
    {
        string cs = $"Data Source=structproj_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keeper = new SqliteConnection(cs);
        await keeper.OpenAsync();
        await using (DbCommand init = keeper.CreateCommand())
        {
            init.CommandText =
                "CREATE TABLE struct_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL, value INTEGER NOT NULL);"
                + "INSERT INTO struct_rows (id, name, value) VALUES (1, 'a', 10), (2, 'b', 20), (3, 'c', 30);";
            await init.ExecuteNonQueryAsync();
        }
        var options = new DbOptions { ConnectionString = cs };
        var session = await DataSession<SqliteProvider>.CreateAsync(options);
        return (session, keeper);
    }

    [Test]
    public async Task QueryStructsAsync_MapsRowsInDeclarationOrder()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync();
        await using var _keeper = keeper;
        await using var _session = session;

        List<StructProjectionRow> rows = await session.QueryStructsAsync<StructProjectionRow>(
            $"SELECT id, name, value FROM struct_rows ORDER BY id");

        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[0]).IsEqualTo(new StructProjectionRow(1, "a", 10));
        await Assert.That(rows[1]).IsEqualTo(new StructProjectionRow(2, "b", 20));
        await Assert.That(rows[2]).IsEqualTo(new StructProjectionRow(3, "c", 30));
    }

    [Test]
    public async Task PipelineQueryStructs_BindsParametersAndReturnsRows()
    {
        (DataSession<SqliteProvider> session, SqliteConnection keeper) = await OpenAsync();
        await using var _keeper = keeper;
        await using var _session = session;

        var pipeline = session.CreatePipeline();
        PipelineStructQuery<StructProjectionRow, SqliteProvider> filtered =
            pipeline.QueryStructs<StructProjectionRow>(
                $"SELECT id, name, value FROM struct_rows WHERE id >= {2L} ORDER BY id");
        PipelineStructQuery<StructProjectionRow, SqliteProvider> single =
            pipeline.QueryStructs<StructProjectionRow>(
                $"SELECT id, name, value FROM struct_rows WHERE id = {1L}");
        await pipeline.ExecuteAsync();

        await Assert.That(filtered.Value.Count).IsEqualTo(2);
        await Assert.That(filtered.Value[0]).IsEqualTo(new StructProjectionRow(2, "b", 20));
        await Assert.That(filtered.Value[1]).IsEqualTo(new StructProjectionRow(3, "c", 30));
        await Assert.That(single.Value.Count).IsEqualTo(1);
        await Assert.That(single.Value[0]).IsEqualTo(new StructProjectionRow(1, "a", 10));
        pipeline.Dispose();
    }
}
