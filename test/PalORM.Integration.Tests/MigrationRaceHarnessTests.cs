using System.Text.RegularExpressions;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// 迁移并发竞态回归装置（2026-10-06，V601 诊断轮 H-B 的断言化形态）：
/// 造一个空库（等价 CI 全新容器）→ 6 路并发 MigrateAsync → 断言零异常。
/// 修复前实证形态：CREATE TABLE IF NOT EXISTS 的存在性检查与目录插入非原子，
/// 竞态败者收 23505（pg_type/pg_class 目录索引，已有兜底）或 42P07（duplicate_table，
/// 原匹配器盲区——CI PG 17 实测穿透）；修复后两形态均被 IsDuplicateSchemaObject
/// 识别并走逐条幂等兜底。
/// </summary>
public sealed class MigrationRaceHarnessTests
{
    [Test]
    public async Task FreshDatabase_ConcurrentMigrations_AreRaceFree()
    {
        // 固定装置库名（无插值洞：CREATE/DROP DATABASE 不收参数）；FORCE 幂等清残留连接
        const string raceDb = "palorm_v601_race";
        string baseCs = TestEnvironment.ResolvePostgreSqlConnectionString();
        string raceCs = ReplaceDatabase(baseCs, raceDb);

        await using (var admin = await DataSession<PostgreSqlProvider>.CreateAsync(
            new DbOptions { ConnectionString = baseCs }))
        {
            // 无插值（FormattableString 的洞会被参数化，CREATE/DROP DATABASE 不收参数）：
            // 库名为固定装置名，字面量直书；ReplaceDatabase 只改连接串
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS palorm_v601_race WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE palorm_v601_race");
        }

        try
        {
            var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
            {
                try
                {
                    await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(
                        new DbOptions { ConnectionString = raceCs });
                    await db.MigrateAsync();
                }
                catch (Exception ex)
                {
                    errors.Add($"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                }
            }));

            await Assert.That(errors).IsEmpty()
                .Because($"并发迁移竞态复现（{errors.Count} 项）：{string.Join(" | ", errors.Take(4))}");
        }
        finally
        {
            await using var cleanup = await DataSession<PostgreSqlProvider>.CreateAsync(
                new DbOptions { ConnectionString = baseCs });
            await cleanup.ExecuteAsync($"DROP DATABASE IF EXISTS palorm_v601_race WITH (FORCE)");
        }
    }

    /// <summary>替换连接串的 Database 键（不存在则追加）——装置用库与主库同实例。</summary>
    private static string ReplaceDatabase(string connectionString, string databaseName)
    {
        if (Regex.IsMatch(connectionString, @"Database=[^;]+", RegexOptions.IgnoreCase))
            return Regex.Replace(connectionString, @"Database=[^;]+", $"Database={databaseName}", RegexOptions.IgnoreCase);
        return $"{connectionString};Database={databaseName}";
    }
}
