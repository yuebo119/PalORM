using System.Data;
using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>DDL 语义族（方案 §4.6 第六族）：约束在库里是否真的成立。
/// <para>快照锁生成物文本、对称性锁方言差异，都不验证"约束生效"。本组用裸 ADO 直接写入，
/// 绕开 PalORM 的默认值填充与计算列排除，因此测的是数据库侧的真实约束。</para>
/// <para>只在 SQLite 上做：三方言的 DDL 形态已由 <c>TriDialect*</c> 的建表与批量写入覆盖，
/// 这里补的是"约束语义"这一层。</para></summary>
public sealed class DdlSemanticsTests
{
    private static async ValueTask<(SharedSqliteDb Shared, DataSession<SqliteProvider> Db, DbConnection Raw)>
        OpenAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
        await db.MigrateAsync(ct);
        await MirrorTable.CleanAsync(db.GetRawConnection(), SqlDialect.Sqlite, ct);
        return (shared, db, db.GetRawConnection());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "语句是本文件的编译期常量（表名列名全内联），不接受外部输入")]
    private static async Task ExpectFailureAsync(
        DbConnection raw, string statement, string expectedFragment, CancellationToken ct = default)
    {
        await using DbCommand command = raw.CreateCommand();
        command.CommandText = statement;
        try
        {
            await command.ExecuteNonQueryAsync(ct);
            await Assert.That($"{statement} 未失败，期望 {expectedFragment}").IsEmpty();
        }
        catch (DbException ex)
        {
            await Assert.That(ex.Message).Contains(expectedFragment, StringComparison.OrdinalIgnoreCase);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "语句是本文件的编译期常量（表名列名全内联），不接受外部输入")]
    private static async Task<long> ScalarCountAsync(
        DbConnection raw, string statement, CancellationToken ct = default)
    {
        await using DbCommand command = raw.CreateCommand();
        command.CommandText = statement;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Test]
    public async Task NotNullColumn_RejectsNullValue()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("ddl_notnull");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        // cf_soft_delete 的 name 是 [Required]：DDL 里是 NOT NULL
        await ExpectFailureAsync(raw,
            """INSERT INTO "cf_soft_delete" ("name", "deleted_at") VALUES (NULL, NULL)""",
            "NOT NULL");
        await Assert.That(await ScalarCountAsync(raw, "SELECT COUNT(*) FROM \"cf_soft_delete\"")).IsEqualTo(0L);
    }

    [Test]
    public async Task UniqueColumn_RejectsDuplicateValue()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("ddl_unique");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        await MirrorTable.ExecuteAsync(raw, SqlDialect.Sqlite,
            """INSERT INTO "cf_composite_index" ("category", "price", "sku") VALUES ('c', 1, 'dup')""");
        await ExpectFailureAsync(raw,
            """INSERT INTO "cf_composite_index" ("category", "price", "sku") VALUES ('c', 2, 'dup')""",
            "UNIQUE");
    }

    [Test]
    public async Task CompositeIndex_And_UniqueIndex_ExistInCatalog()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("ddl_index_catalog");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        long indexCount = await ScalarCountAsync(raw,
            """SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'cf_composite_index'""");
        await Assert.That(indexCount).IsGreaterThanOrEqualTo(2);

        long namedIndex = await ScalarCountAsync(raw,
            """SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_cf_composite_index_cat_price'""");
        await Assert.That(namedIndex).IsEqualTo(1L);
    }

    [Test]
    public async Task ForeignKey_RejectsOrphanRow()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("ddl_fk");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        // SQLite 的外键约束默认关闭，必须显式打开才生效（连接级 PRAGMA）
        await MirrorTable.ExecuteAsync(raw, SqlDialect.Sqlite, "PRAGMA foreign_keys = ON");

        await ExpectFailureAsync(raw,
            """INSERT INTO "cf_fk_child" ("parent_id") VALUES (999)""",
            "FOREIGN KEY");
    }

    [Test]
    public async Task DefaultValue_AppliesWhenColumnOmitted()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("ddl_default");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        // 只给 base_value，让 dflt_value 走 DDL 的 DEFAULT 7
        await MirrorTable.ExecuteAsync(raw, SqlDialect.Sqlite,
            """INSERT INTO "cf_computed" ("base_value") VALUES (10)""");

        long defaultValue = await ScalarCountAsync(raw,
            """SELECT COUNT(*) FROM "cf_computed" WHERE "dflt_value" = 7""");
        await Assert.That(defaultValue).IsEqualTo(1L);
    }

    [Test]
    public async Task ComputedColumn_IsGeneratedByDatabase()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("ddl_computed");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        await MirrorTable.ExecuteAsync(raw, SqlDialect.Sqlite,
            """INSERT INTO "cf_computed" ("base_value") VALUES (10)""");

        long computed = await ScalarCountAsync(raw,
            """SELECT COUNT(*) FROM "cf_computed" WHERE "calc_value" = 11""");
        await Assert.That(computed).IsEqualTo(1L);
    }

    [Test]
    public async Task AutoIncrement_ProducesIncreasingKeys()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db, DbConnection raw) = await OpenAsync("ddl_autoincrement");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        await MirrorTable.ExecuteAsync(raw, SqlDialect.Sqlite,
            """INSERT INTO "cf_soft_delete" ("name") VALUES ('a'), ('b'), ('c')""");

        // 三行主键必须是 1、2、3（自增连续）
        long matched = await ScalarCountAsync(raw,
            """SELECT COUNT(*) FROM "cf_soft_delete" WHERE "Id" IN (1, 2, 3)""");
        await Assert.That(matched).IsEqualTo(3L);
    }
}