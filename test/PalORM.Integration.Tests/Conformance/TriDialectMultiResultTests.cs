using System.Data;
using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>多结果集族的三方言对照（方案 §10 阶段 3）。
/// <para>QueryMultiple：PalORM 的 GridReader 逐段读，手写侧用裸 ADO 的 <c>NextResultAsync</c> 对齐；
/// GetRawConnection：经裸连接写入，PalORM 侧必须读到同一行（证明逃生舱与原会话指向同一个库）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class TriDialectMultiResultTests
{
    private static readonly string[] BulkColumns = ["Id", "name", "amount"];

    private static async Task WithSqliteAsync(
        Func<SqlDialect, DataSession<SqliteProvider>, DbConnection, Task> body)
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("tri_multi");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await MirrorTable.EnsureAsync(db, SqlDialect.Sqlite);
        await body(SqlDialect.Sqlite, db, db.GetRawConnection());
    }

    private static async Task WithPostgreSqlAsync(
        Func<SqlDialect, DataSession<PostgreSqlProvider>, DbConnection, Task> body)
    {
        await using RemoteConformanceFixture<PostgreSqlProvider> fixture = await ConformanceDialects.PostgreSqlAsync();
        await body(SqlDialect.PostgreSql, fixture.Session, fixture.Raw);
    }

    private static async Task WithMySqlAsync(
        Func<SqlDialect, DataSession<MySqlProvider>, DbConnection, Task> body)
    {
        await using RemoteConformanceFixture<MySqlProvider> fixture = await ConformanceDialects.MySqlAsync();
        await body(SqlDialect.MySql, fixture.Session, fixture.Raw);
    }

    private static string MultiSelectSql(SqlDialect dialect)
    {
        string columns = DialectSql.ColumnList(dialect, BulkColumns);
        string table = DialectSql.Quote(dialect, MirrorTable.BulkTable);
        string amount = DialectSql.Quote(dialect, "amount");
        return $"SELECT {columns} FROM {table} WHERE {amount} > 1; SELECT {columns} FROM {table}";
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "SQL 由 DialectSql 按方言常量模板拼装，不接受外部输入；手写侧必须绕过 PalORM 才能独立验证")]
    private static async Task QueryMultipleCoreAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw)
        where TProvider : IDbProvider
    {
        await db.BulkInsertAsync(
            [new CfBulkPlainEntity { Id = 1, Name = "a", Amount = 1 },
             new CfBulkPlainEntity { Id = 2, Name = "b", Amount = 2 }]);

        string sql = MultiSelectSql(dialect);
        // 纯 SQL 必须用 FormattableStringFactory 包：$"{sql}" 会把整段 SQL 变成参数值
        FormattableString query = System.Runtime.CompilerServices.FormattableStringFactory.Create(sql);
        List<CfBulkPlainEntity> first;
        List<CfBulkPlainEntity> second;
        // GridReader 持有命令与连接：必须先释放，手写侧才能在同一个连接上执行
        await using (GridReader reader = await db.From<CfBulkPlainEntity>().QueryMultipleAsync(query))
        {
            first = await reader.ReadAsync<CfBulkPlainEntity>();
            second = await reader.ReadAsync<CfBulkPlainEntity>();
        }

        // 手写侧：同一 SQL，裸 ADO 逐结果集读主键
        await using DbCommand command = raw.CreateCommand();
        command.CommandText = sql;
        await using DbDataReader dataReader = await command.ExecuteReaderAsync();
        List<long> firstIds = [];
        while (await dataReader.ReadAsync()) firstIds.Add(dataReader.GetInt64(0));
        await dataReader.NextResultAsync();
        List<long> secondIds = [];
        while (await dataReader.ReadAsync()) secondIds.Add(dataReader.GetInt64(0));

        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(first[0].Id).IsEqualTo(2L);
        await Assert.That(second.Count).IsEqualTo(2);
        await Assert.That(string.Join(",", first.Select(e => e.Id).OrderBy(id => id)))
            .IsEqualTo(string.Join(",", firstIds.OrderBy(id => id)));
        await Assert.That(string.Join(",", second.Select(e => e.Id).OrderBy(id => id)))
            .IsEqualTo(string.Join(",", secondIds.OrderBy(id => id)));
    }

    private static async Task RawConnectionCoreAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw)
        where TProvider : IDbProvider
    {
        await Assert.That(raw.State).IsEqualTo(ConnectionState.Open);

        // 经裸连接写入 PalORM 的表，PalORM 侧必须读到
        await MirrorTable.ExecuteAsync(
            raw, dialect, DialectSql.Insert(dialect, MirrorTable.BulkTable, BulkColumns, 1),
            [("@p0", 99L), ("@p1", "raw-written"), ("@p2", 99L)]);

        List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>().ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("raw-written");
        await Assert.That(rows[0].Amount).IsEqualTo(99L);
    }

    [Test]
    public async Task Sqlite_QueryMultiple() => await WithSqliteAsync(QueryMultipleCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_QueryMultiple() => await WithPostgreSqlAsync(QueryMultipleCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_QueryMultiple() => await WithMySqlAsync(QueryMultipleCoreAsync);

    [Test]
    public async Task Sqlite_GetRawConnection() => await WithSqliteAsync(RawConnectionCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_GetRawConnection() => await WithPostgreSqlAsync(RawConnectionCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_GetRawConnection() => await WithMySqlAsync(RawConnectionCoreAsync);
}