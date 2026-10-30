using PalORM;

namespace PalORM.Core.Tests;

/// <summary>语句形态的 SQL 文本断言（《测试体系增强方案-2026-10-30》§5.4 的 UPSERT 三条
/// 与 §5.3 坐标登记的证据来源）。
/// <para><b>为什么断言生成载荷而不是 DryRun</b>：<c>SaveAsync</c>/<c>BulkMergeAsync</c> 是写路径，
/// 没有 DryRun 入口；UPSERT 的 SQL 是编译期生成的常量（<c>PalORM_Runtime.CommandSqlsByDialect</c>，
/// 由 PL-3/B2 载荷契约保证"只发射会被读到的方言族"），断言载荷等于断言实际执行的语句文本。</para>
/// <para><b>三方言冲突子句形态</b>：PG/SQLite 都是 <c>ON CONFLICT (pk) DO UPDATE SET col = excluded.col</c>
/// （SQLite 复用 PG 实例，逐位相同）；MySQL 是 <c>ON DUPLICATE KEY UPDATE col = VALUES(col)</c>，
/// 自增主键另追加 <c>LAST_INSERT_ID(pk)</c> 回填。</para></summary>
internal sealed class StatementSqlShapeTests
{
    private const string TwoRowInsertExpected =
        "INSERT INTO \"t\" (\"a\", \"b\") VALUES (@p0, @p1), (@p2, @p3)";

    [Test]
    public async Task InsertReturningPayloads_CarryReturningClause()
    {
        CommandSqlByDialect sets = PalORM_Runtime.CommandSqlsByDialect[typeof(UpsertShapeProbe)];

        await Assert.That(sets.Sqlite.InsertReturning).Contains("INSERT INTO");
        await Assert.That(sets.Sqlite.InsertReturning).Contains("RETURNING");
        await Assert.That(sets.PostgreSql.InsertReturning).Contains("RETURNING");
        // MySQL 无 RETURNING：自增键回填走 LAST_INSERT_ID 族载荷
        await Assert.That(sets.MySql.InsertWithLastInsertId).Contains("LAST_INSERT_ID");
        await Assert.That(sets.MySql.InsertReturning).IsEmpty();
    }

    [Test]
    public async Task PostgreSql_Upsert_ContainsOnConflictAndConflictColumn()
    {
        CommandSqlSet pg = PalORM_Runtime.CommandSqlsByDialect[typeof(UpsertShapeProbe)].PostgreSql;

        // T14 判据：不只断言 "ON CONFLICT" 四个字，冲突列名与 DO UPDATE 赋值形态一并锁住
        await Assert.That(pg.UpsertReturning).Contains("ON CONFLICT (\"Id\") DO UPDATE SET");
        await Assert.That(pg.UpsertReturning).Contains("\"name\" = excluded.\"name\"");
    }

    [Test]
    public async Task MySql_Upsert_ContainsOnDuplicateKeyUpdate()
    {
        CommandSqlSet mySql = PalORM_Runtime.CommandSqlsByDialect[typeof(UpsertShapeProbe)].MySql;

        // T15 判据：MySQL 无冲突列语法（唯一索引全表生效），锁 VALUES(col) 赋值与自增回填
        await Assert.That(mySql.UpsertMySql).Contains("ON DUPLICATE KEY UPDATE");
        await Assert.That(mySql.UpsertMySql).Contains("`name` = VALUES(`name`)");
        await Assert.That(mySql.UpsertMySql).Contains("LAST_INSERT_ID(`Id`)");
    }

    [Test]
    public async Task Sqlite_Upsert_ContainsOnConflictDoUpdate()
    {
        CommandSqlSet sqlite = PalORM_Runtime.CommandSqlsByDialect[typeof(UpsertShapeProbe)].Sqlite;

        // T16 判据：SQLite 与 PG 同形，冲突列 + DO UPDATE 两个片段都要在
        await Assert.That(sqlite.UpsertReturning).Contains("ON CONFLICT (\"Id\")");
        await Assert.That(sqlite.UpsertReturning).Contains("DO UPDATE SET");
    }

    [Test]
    public async Task MultiValueInsert_EmitsOneValueTuplePerRow()
    {
        string sql = BuildTwoRowInsert();

        // 多值 INSERT 的全部形态：表名/列清单引用、行内参数平铺、行间 ", " 分隔
        await Assert.That(sql).IsEqualTo(TwoRowInsertExpected);
    }

    /// <summary>非 async 帮助方法：<see cref="ValueStringBuilder"/> 是 ref struct，
    /// 栈缓冲不能跨 await 存活（同 MultiValueBulkInsert.BuildBatchSql 的收帧做法）。</summary>
    private static string BuildTwoRowInsert()
    {
        Span<char> buffer = stackalloc char[256];
        var builder = new ValueStringBuilder(buffer);
        try
        {
            MultiValueBulkInsert.AppendInsertStatement(ref builder, "\"t\"", "\"a\", \"b\"", 2, 2);
            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }
}

[Table("upsert_shape_probe")]
internal sealed partial class UpsertShapeProbe
{
    [Key]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("qty")]
    public int Qty { get; set; }
}