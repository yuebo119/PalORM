using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>ITM-876（r23 真库探针）：MySQL ODKU 的 VALUES() 弃用路线观测。
/// <para><b>背景</b>：BulkMergeAsync 的 MySQL 分支生成 ON DUPLICATE KEY UPDATE c = VALUES(c)，
/// MySQL 8.0.20+ 对该形态发 deprecation warning（推荐 row alias 新语法），8.4/9.x 处于移除路线。
/// 本探针锁定两件事：① 真库版本（决定版本分派阈值的事实）；② 当前 VALUES() 形态是否已产生
/// deprecation warning（升级行动的触发信号——有 warning 时分派新语法的收益变为正值）。
/// 经 MySqlConnector 原生命令观测（MySqlCommand.Warnings），SHOW WARNINGS 不是表不可 From 查。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class OdkuDeprecationProbeTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_OdkuValuesForm_VersionAndDeprecationSignal()
    {
        await using var connection = new MySqlConnector.MySqlConnection(
            TestEnvironment.ResolveMySqlConnectionString());
        await connection.OpenAsync();

        // ① 真库版本（事实记录，非断言——版本由环境决定）
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT VERSION()";
        string version = (string)(await versionCommand.ExecuteScalarAsync())!;
        await Assert.That(string.IsNullOrWhiteSpace(version)).IsFalse();

        await using var setupCommand = connection.CreateCommand();
        setupCommand.CommandText =
            "DROP TABLE IF EXISTS palorm_odku_probe; "
            + "CREATE TABLE palorm_odku_probe (id INT PRIMARY KEY, v INT NOT NULL); "
            + "INSERT INTO palorm_odku_probe (id, v) VALUES (1, 10)";
        await setupCommand.ExecuteNonQueryAsync();

        // ② VALUES() 形态执行 + 驱动层 Warnings 观测
        await using var odkuCommand = connection.CreateCommand();
        odkuCommand.CommandText =
            "INSERT INTO palorm_odku_probe (id, v) VALUES (1, 20) "
            + "ON DUPLICATE KEY UPDATE v = VALUES(v)";
        int affected = await odkuCommand.ExecuteNonQueryAsync();

        // 行为断言：ODKU 语义正确（已存在键 → 更新为 20；MySQL ODKU 对更新计 2 行）
        await Assert.That(affected).IsEqualTo(2);
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT v FROM palorm_odku_probe WHERE id = 1";
        long value = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(value).IsEqualTo(20L);

        // 弃用信号观测：SHOW WARNINGS 结果集（Level/Code/Message 三列）。不判红——形态仍被
        // 服务端接受，弃用≠失败；一旦未来版本报错，上面的 ExecuteNonQueryAsync 直接失败，
        // 本测试自动转为哨兵。reader 用独立作用域（MySqlConnector 连接互斥：reader 未关时
        // 同连接后续命令报 "already in use"）。
        List<string> warningLines = await ReadWarningsAsync(connection);
        Console.WriteLine($"ODKU-PROBE: version={version} warnings={warningLines.Count}");
        foreach (string line in warningLines)
            Console.WriteLine($"ODKU-WARNING: {line}");

        await using var cleanupCommand = connection.CreateCommand();
        cleanupCommand.CommandText = "DROP TABLE IF EXISTS palorm_odku_probe";
        await cleanupCommand.ExecuteNonQueryAsync();
    }

    /// <summary>读取 SHOW WARNINGS 结果集——独立方法保证 reader 完全释放后再返回
    ///（MySqlConnector 连接互斥：reader 未关时同连接后续命令报 "already in use"）。</summary>
    private static async Task<List<string>> ReadWarningsAsync(
        MySqlConnector.MySqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW WARNINGS";
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add($"level={reader.GetString(0)} code={reader.GetInt32(1)} {reader.GetString(2)}");
        return lines;
    }
}
