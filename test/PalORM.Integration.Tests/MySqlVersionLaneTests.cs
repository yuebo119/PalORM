using System.Data.Common;
using System.Text;
using PalORM.Integration.Tests.Conformance;
using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

// P1-C（方案 §6.6 / COMPAT-1）：MySQL 版本 lane。
// 夜间 job 用 mysql:8.0.18 / 8.0.19 / 8.4 三套服务端各跑一次：
// 形态断言（general_log 抓实际 SQL：8.0.18 走 CASE WHEN，8.0.19+ 走 JOIN VALUES ROW）
// 与结果断言（批量更新、Upsert、批量插入、分页）同时存在。
// 版本从服务端 SELECT VERSION() 读取，测试自适应当前 lane，不按环境变量写死。

[NotInParallel("ExtBulkTable")]
public sealed class MySqlVersionLaneTests
{
    private static async Task<string> ServerVersionAsync(DataSession<MySqlProvider> db)
        => (await db.ScalarAsync<string>($"SELECT VERSION()"))!;

    private static Version ParseVersion(string text)
    {
        // 形如 8.4.0 / 8.0.19-0ubuntu0.22.04.1
        int cut = text.IndexOfAny('-', '+');
        string numeric = cut < 0 ? text : text[..cut];
        return Version.TryParse(numeric, out Version? version) ? version : new Version(0, 0, 0);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_VersionLane_BatchUpdateFormAndCorePaths()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        await db.MigrateAsync();
        string versionText = await ServerVersionAsync(db);
        Version version = ParseVersion(versionText);
        bool joinValuesRow = version >= new Version(8, 0, 19);

        await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
        await db.ExecuteAsync($"DELETE FROM cf_versioned");
        await db.ExecuteAsync($"DELETE FROM cf_tenant");
        try
        {
            // ── 抓形态：批量更新语句经 general_log 留痕 ──
            await db.ExecuteAsync($"SET GLOBAL log_output = 'TABLE'");
            await db.ExecuteAsync($"TRUNCATE TABLE mysql.general_log");
            await db.ExecuteAsync($"SET GLOBAL general_log = 'ON'");
            try
            {
                CfBulkPlainEntity[] rows = [.. Enumerable.Range(1, 100).Select(i =>
                    new CfBulkPlainEntity { Id = i, Name = $"before-{i}", Amount = i })];
                await db.BulkInsertAsync(rows);
                foreach (CfBulkPlainEntity row in rows) row.Name = $"after-{row.Id}";
                long affected = await db.BulkUpdateAsync(rows);
                await Assert.That(affected).IsEqualTo(100L);
            }
            finally
            {
                await db.ExecuteAsync($"SET GLOBAL general_log = 'OFF'");
            }

            // mysql.general_log.argument 经驱动读回是 byte[]（不是 IConvertible），
            // ScalarAsync<string> 会在 Convert.ChangeType 处抛 InvalidCastException，故走裸 ADO。
            DbConnection rawConnection = db.GetRawConnection();
            string? batchUpdateSql;
            await using (DbCommand logCommand = rawConnection.CreateCommand())
            {
                logCommand.CommandText =
                    "SELECT argument FROM mysql.general_log "
                    + "WHERE argument LIKE 'UPDATE%cf_bulk_plain%' ORDER BY event_time DESC LIMIT 1";
                object? value = await logCommand.ExecuteScalarAsync();
                batchUpdateSql = value switch
                {
                    null or DBNull => null,
                    string text => text,
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    _ => value.ToString()
                };
            }
            await Assert.That(batchUpdateSql).IsNotNull()
                .Because($"general_log 未捕获批量更新语句（版本 {versionText}）");

            if (joinValuesRow)
            {
                await Assert.That(batchUpdateSql!).Contains("JOIN (VALUES")
                    .Because($"MySQL {versionText} ≥ 8.0.19 应走 UPDATE JOIN table value constructor");
                await Assert.That(batchUpdateSql!).DoesNotContain("CASE WHEN");
            }
            else
            {
                await Assert.That(batchUpdateSql!).Contains("CASE")
                    .Because($"MySQL {versionText} < 8.0.19 应回退 CASE WHEN");
            }

            // ── 结果断言：Upsert ──
            await db.SaveAsync(new CfBulkPlainEntity { Id = 1, Name = "upserted", Amount = 9 });
            await Assert.That((await db.GetAsync<CfBulkPlainEntity>(1L))!.Name).IsEqualTo("upserted");

            // ── 结果断言：批量插入（含跨批）──
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
            CfBulkPlainEntity[] inserts = [.. Enumerable.Range(1, 1200).Select(i =>
                new CfBulkPlainEntity { Id = i, Name = $"i{i}", Amount = i })];
            await Assert.That(await db.BulkInsertAsync(inserts, batchSize: 500)).IsEqualTo(1200L);
            await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1200L);

            // ── 结果断言：分页 ──
            (List<CfBulkPlainEntity> first, long total) = await db.From<CfBulkPlainEntity>()
                .ToPageAsync(100, e => e.Id, descending: false);
            (List<CfBulkPlainEntity> second, _) = await db.From<CfBulkPlainEntity>()
                .ToPageAsync(100, e => e.Id, lastValue: first[^1].Id, descending: false);
            await Assert.That(total).IsEqualTo(1200L);
            await Assert.That(first.Count).IsEqualTo(100);
            await Assert.That(second[0].Id).IsEqualTo(101L);

            Console.WriteLine(
                $"MYSQL-VERSION-LANE version={versionText} form={(joinValuesRow ? "JoinValuesRow" : "CaseWhen")}");
        }
        finally
        {
            await db.ExecuteAsync($"SET GLOBAL general_log = 'OFF'");
            await db.ExecuteAsync($"SET GLOBAL log_output = 'FILE'");
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
            await db.ExecuteAsync($"DELETE FROM cf_versioned");
        }
    }
}