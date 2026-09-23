using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>READ-002（2026-09-23）：原始 SQL 家族的读路由 opt-in。
/// <para>默认（缺省 <c>readFromReplica: false</c>）与既有行为逐位一致；显式 true 且无事务时走读连接。
/// 用两个独立 SQLite 内存库区分主/副本（各自建表并塞不同数据），断言"读到了哪一份"。</para>
/// <para>每个用例用 Guid 唯一库名——内存库按连接串共享，复用常量名会让并行用例互相干扰。</para>
/// <para>只读入口提供该开关；<c>ExecuteAsync</c>（DML 入口）不提供——加读路由开关是误用诱因。</para></summary>
internal sealed class ReadRoutingTests
{
    private static async Task<(
        DataSession<SqliteProvider> Session,
        SqliteConnection PrimaryKeeper,
        SqliteConnection ReplicaKeeper,
        string Primary,
        string Replica)> CreateIsolatedAsync()
    {
        string primary = $"Data Source=route_p_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        string replica = $"Data Source=route_r_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        // 内存库随最后一个连接释放而消失：各库用 keeper 连接保活（与本仓既有内存库用例同法）
        var primaryKeeper = new SqliteConnection(primary);
        await primaryKeeper.OpenAsync();
        var replicaKeeper = new SqliteConnection(replica);
        await replicaKeeper.OpenAsync();
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = primary,
            ReadConnectionString = replica
        });
        return (session, primaryKeeper, replicaKeeper, primary, replica);
    }

    private static async Task SeedAsync(string connectionString, long id, string value)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE routing_rows (Id INTEGER PRIMARY KEY, v TEXT NOT NULL)";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = "INSERT INTO routing_rows (Id, v) VALUES ($id, $v)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task RawSql_ReadFromReplica_ReadsReplicaDatabase()
    {
        var (session, primaryKeeper, replicaKeeper, primary, replica) = await CreateIsolatedAsync();
        await using (session)
        using (primaryKeeper)
        using (replicaKeeper)
        {
            await SeedAsync(primary, 1, "primary");
            await SeedAsync(replica, 1, "replica");

            // opt-in：显式 readFromReplica 走副本；缺省走主库（既有行为不变）
            List<RoutingRow> fromReplica = await session.QueryAsync<RoutingRow>(
                $"SELECT Id, v FROM routing_rows", readFromReplica: true);
            List<RoutingRow> fromPrimary = await session.QueryAsync<RoutingRow>(
                $"SELECT Id, v FROM routing_rows");

            await Assert.That(fromReplica.Count).IsEqualTo(1);
            await Assert.That(fromReplica[0].V).IsEqualTo("replica");
            await Assert.That(fromPrimary.Count).IsEqualTo(1);
            await Assert.That(fromPrimary[0].V).IsEqualTo("primary");
        }
    }

    [Test]
    public async Task RawSql_ScalarFromReplica_ReadsReplicaDatabase()
    {
        var (session, primaryKeeper, replicaKeeper, primary, replica) = await CreateIsolatedAsync();
        await using (session)
        using (primaryKeeper)
        using (replicaKeeper)
        {
            await SeedAsync(primary, 1, "primary");
            await SeedAsync(replica, 2, "replica");

            string? replicaValue = await session.ScalarAsync<string>(
                $"SELECT v FROM routing_rows WHERE Id = {2}", readFromReplica: true);
            string? primaryValue = await session.ScalarAsync<string>(
                $"SELECT v FROM routing_rows WHERE Id = {2}");

            await Assert.That(replicaValue).IsEqualTo("replica");
            await Assert.That(primaryValue).IsNull();   // 主库没有 Id=2
        }
    }
}

[Table("routing_rows")]
internal sealed partial class RoutingRow
{
    [Key] public long Id { get; set; }
    [Column("v")] public string V { get; set; } = "";
}
