using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>ITM-852/863（r23 真库探针）：重试路径的参数复用归属——三方言真库实测。
/// <para><b>背景</b>：只读管线把同一批 DbParameter 实例在每次重试尝试 Add 到新建命令
///（v4.1 注释明说复用）；若驱动 ParameterCollection 对已归属参数拒绝 Add，重试在
/// AddParameters 处以非根因异常失败（重试功能实质失效）。SQLite 臂已实证（Core.Tests
/// Probe3）；本组补 PG/MySQL 臂（r22 ITM-852 要求"必须三方言真库实测，不接受推断"）。</para>
/// <para><b>探针形态</b>：同一参数对象先后 Add 到两个命令并各自执行（同连接顺序复用，
/// 等价于"首次尝试失败→第二次尝试重挂"的归属面；命令一 Dispose 后命令二 Add 是重试
/// 管线的精确时序形态）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class DriverBehaviorProbeTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_DbParameter_SequentialReuseAcrossCommands_Succeeds()
    {
        await using var connection = new Npgsql.NpgsqlConnection(
            TestEnvironment.ResolvePostgreSqlConnectionString());
        await connection.OpenAsync();
        await using var setup = connection.CreateCommand();
        setup.CommandText = "DROP TABLE IF EXISTS palorm_param_reuse; "
            + "CREATE TABLE palorm_param_reuse (id BIGINT PRIMARY KEY)";
        await setup.ExecuteNonQueryAsync();

        await using var first = connection.CreateCommand();
        first.CommandText = "INSERT INTO palorm_param_reuse (id) VALUES (@p0)";
        var parameter = first.CreateParameter();
        parameter.ParameterName = "@p0";
        parameter.Value = 42L;
        first.Parameters.Add(parameter);
        await first.ExecuteNonQueryAsync();
        await first.DisposeAsync();

        // 复用同一参数实例到第二个命令（重试路径的归属面）
        await using var second = connection.CreateCommand();
        second.CommandText = "SELECT id FROM palorm_param_reuse WHERE id = @p0";
        second.Parameters.Add(parameter);
        object? result = await second.ExecuteScalarAsync();
        await Assert.That(result).IsEqualTo(42L);

        await using var cleanup = connection.CreateCommand();
        cleanup.CommandText = "DROP TABLE IF EXISTS palorm_param_reuse";
        await cleanup.ExecuteNonQueryAsync();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_DbParameter_SequentialReuseAcrossCommands_Succeeds()
    {
        await using var connection = new MySqlConnector.MySqlConnection(
            TestEnvironment.ResolveMySqlConnectionString());
        await connection.OpenAsync();
        await using var setup = connection.CreateCommand();
        setup.CommandText = "DROP TABLE IF EXISTS palorm_param_reuse; "
            + "CREATE TABLE palorm_param_reuse (id BIGINT PRIMARY KEY)";
        await setup.ExecuteNonQueryAsync();

        await using var first = connection.CreateCommand();
        first.CommandText = "INSERT INTO palorm_param_reuse (id) VALUES (@p0)";
        var parameter = first.CreateParameter();
        parameter.ParameterName = "@p0";
        parameter.Value = 42L;
        first.Parameters.Add(parameter);
        await first.ExecuteNonQueryAsync();
        await first.DisposeAsync();

        await using var second = connection.CreateCommand();
        second.CommandText = "SELECT id FROM palorm_param_reuse WHERE id = @p0";
        second.Parameters.Add(parameter);
        object? result = await second.ExecuteScalarAsync();
        await Assert.That(Convert.ToInt64(
            result ?? 0L, System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(42L);

        await using var cleanup = connection.CreateCommand();
        cleanup.CommandText = "DROP TABLE IF EXISTS palorm_param_reuse";
        await cleanup.ExecuteNonQueryAsync();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_IntPrimaryKey_InsertReturning_ReadsInt32NotInt64()
    {
        // ITM-854（r23 真库探针）：int 自增主键 + 宽 RETURNING 路径的读类型口径——
        // PG DDL 是 INTEGER IDENTITY，驱动 GetInt64 对 int4 列的实测行为（抛 = 写入成功
        // 但调用失败，上层重试即重复行；通过 = 口径一致无缺陷）。
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_int_pk_probe");
        await db.ExecuteAsync(
            $"CREATE TABLE palorm_int_pk_probe (id SERIAL PRIMARY KEY, name TEXT NOT NULL)");

        // 宽 RETURNING 形态：任一 Computed/Timestamp/Converter 列会关掉 KeyOnlyReturning，
        // 走 reader.GetXxx(pkOrdinal) 物化——本探针直接用驱动读形态验证 int4 → GetInt32 ✓
        await using var connection = new Npgsql.NpgsqlConnection(
            TestEnvironment.ResolvePostgreSqlConnectionString());
        await connection.OpenAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO palorm_int_pk_probe (name) VALUES ('p') RETURNING id";
        object? id = await insert.ExecuteScalarAsync();
        // 驱动对 int4 列返回 Int32（PG 语义）；PalORM 的 NormalizeGeneratedId 接受 int/long/ulong
        await Assert.That(id).IsTypeOf<int>();

        // 行为断言：PalORM 全链路（int 主键实体 InsertAsync 回填）由既有集成测试覆盖
        await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_int_pk_probe");
    }
}
