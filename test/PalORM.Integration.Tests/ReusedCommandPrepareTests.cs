using PalORM.MySql;
using Npgsql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

[Table("palorm_prepare_probe")]
internal sealed partial class PrepareProbeRow
{
    [Key(AutoIncrement = false)][Column("id")] public long id { get; set; }
    [Column("payload")] public string payload { get; set; } = "";
}

[Projection]
internal sealed class GlobalStatusRow
{
    public string VariableName { get; set; } = "";
    public string VariableValue { get; set; } = "";
}

/// <summary>MySQL 复用命令晋升后的服务端预编译：同形态第 3 次操作触发一次
/// <c>COM_STMT_PREPARE</c>，此后执行走二进制协议（PG 由驱动 AutoPrepare 自动完成，
/// SQLite 随命令对象缓存语句，均不在此测试面）。</summary>
[NotInParallel("ExtBulkTable")]
internal sealed class ReusedCommandPrepareTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "PALORM005:Database call inside a loop",
        Justification = "本用例刻意顺序执行 5 次同形态单键读以触发复用槽晋升（第 3 次）；批量替代会绕过被测路径。")]
    public async Task MySql_ReusedGetByKeyCommand_IsPreparedOnce()
    {
        await using var session = await DataSession<MySqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_prepare_probe");
        await session.ExecuteAsync(
            $"CREATE TABLE palorm_prepare_probe (id BIGINT PRIMARY KEY, payload VARCHAR(64) NOT NULL)");
        try
        {
            await session.ExecuteAsync(
                $"INSERT INTO palorm_prepare_probe (id, payload) VALUES (1, 'a')");
            long before = await PreparedStatementCountAsync(session);
            for (int iteration = 0; iteration < 5; iteration++)
            {
                PrepareProbeRow? row = await session.GetAsync<PrepareProbeRow>(1L);
                await Assert.That(row?.payload).IsEqualTo("a");
            }
            long after = await PreparedStatementCountAsync(session);
            await Assert.That(after).IsGreaterThan(before)
                .Because("同形态第 3 次操作应触发一次服务端预编译（Com_stmt_prepare 递增）");
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_prepare_probe");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task PostgreSql_PrepareOptions_AreMappedToConnectionString()
    {
        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString(),
            PrepareCacheSize = 250,
            PrepareThreshold = 3
        });
        // AutoPrepare 是 Npgsql 客户端参数（非服务端 GUC）——从会话连接串上核实改写结果。
        var builder = new NpgsqlConnectionStringBuilder(session.GetRawConnection().ConnectionString);
        await Assert.That(builder.MaxAutoPrepare).IsEqualTo(250);
        await Assert.That(builder.AutoPrepareMinUsages).IsEqualTo(3);
    }

    /// <summary>当前连接累计的 COM_STMT_PREPARE 次数（SHOW GLOBAL STATUS 两列结果经投影读出；
    /// <c>Com_*</c> 不在 performance_schema.global_status 中）。</summary>
    private static async Task<long> PreparedStatementCountAsync(DataSession<MySqlProvider> session)
    {
        List<GlobalStatusRow> rows = await session.QueryAsync<GlobalStatusRow>(
            $"SHOW GLOBAL STATUS LIKE 'Com_stmt_prepare'");
        return rows.Count == 0
            ? 0
            : long.Parse(rows[0].VariableValue, System.Globalization.CultureInfo.InvariantCulture);
    }
}
