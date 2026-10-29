using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>存储过程构建器的三方言形态（方案 §10 阶段 3）。
/// <para>SQLite 没有存储过程：<c>StoredProc</c> 的构建器可创建，但执行必须明确失败而不是静默无操作。
/// PG 与 MySQL 各建一个真实过程，用 <c>WithParam</c> 传参并断言过程内的写入生效。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class TriDialectStoredProcTests
{
    private static string NewProcedureName() => $"cf_echo_{Guid.NewGuid():N}";

    [Test]
    public async Task Sqlite_StoredProc_ExecuteFailsExplicitly()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("tri_proc_sqlite");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await MirrorTable.EnsureAsync(db, SqlDialect.Sqlite);

        // 构建期允许（DryRun/预览需要），执行期由驱动明确拒绝
        await Assert.That(async () => await db.StoredProc("cf_noop").ExecuteAsync())
            .Throws<Exception>();
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_StoredProc_WritesRow()
    {
        await using RemoteConformanceFixture<PostgreSqlProvider> fixture = await ConformanceDialects.PostgreSqlAsync();
        SqlDialect dialect = fixture.Dialect;
        DataSession<PostgreSqlProvider> db = fixture.Session;
        DbConnection raw = fixture.Raw;

        string procedureName = NewProcedureName();
        try
        {
            await MirrorTable.ExecuteAsync(raw, dialect,
                $"CREATE PROCEDURE {procedureName}(IN v BIGINT) LANGUAGE sql "
                + $"AS 'INSERT INTO \"cf_bulk_plain\" (\"Id\", \"name\", \"amount\") VALUES (v, ''pg-proc'', v)'");

            await db.StoredProc(procedureName).WithParam("v", 7L).ExecuteAsync();

            List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>().ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].Id).IsEqualTo(7L);
            await Assert.That(rows[0].Name).IsEqualTo("pg-proc");
        }
        finally
        {
            await MirrorTable.ExecuteAsync(raw, dialect, $"DROP PROCEDURE IF EXISTS {procedureName}");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_StoredProc_WritesRow()
    {
        await using RemoteConformanceFixture<MySqlProvider> fixture = await ConformanceDialects.MySqlAsync();
        SqlDialect dialect = fixture.Dialect;
        DataSession<MySqlProvider> db = fixture.Session;
        DbConnection raw = fixture.Raw;

        string procedureName = NewProcedureName();
        try
        {
            await MirrorTable.ExecuteAsync(raw, dialect,
                $"CREATE PROCEDURE {procedureName}(IN v BIGINT) "
                + "BEGIN INSERT INTO `cf_bulk_plain` (`Id`, `name`, `amount`) VALUES (v, 'my-proc', v); END");

            await db.StoredProc(procedureName).WithParam("v", 7L).ExecuteAsync();

            List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>().ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].Id).IsEqualTo(7L);
            await Assert.That(rows[0].Name).IsEqualTo("my-proc");
        }
        finally
        {
            await MirrorTable.ExecuteAsync(raw, dialect, $"DROP PROCEDURE IF EXISTS {procedureName}");
        }
    }
}