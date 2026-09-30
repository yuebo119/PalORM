using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>[ForeignKey] DDL 内联往返（ADR-B 补充实施）——锁定三层行为：
/// ① DDL 生成（快照基线评审三方言表级子句形态，SnapshotTests）；② SQLite 引擎强制
/// （引擎默认 PRAGMA foreign_keys=OFF，测试显式开启后孤儿插入必须被拒、CASCADE 真传播）；
/// ③ PG/MySQL 真库强制 + 二次迁移幂等。已建表不补齐 FK 属迁移系统既定口径
/// （CREATE IF NOT EXISTS 幂等新建哲学，与"已建表不加列"同限制），文档载明。</summary>
public sealed class ForeignKeyDdlTests
{
    // ─── SQLite（本地，PRAGMA 显式开启后引擎强制） ───

    [Test]
    public async Task Fk_PragmaOn_RejectsOrphanInsert()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.ExecuteAsync($"PRAGMA foreign_keys = ON");
        await db.MigrateAsync();

        await db.InsertAsync(new FkItx0Parent { Name = "p1" });
        // 合法引用成功
        await db.InsertAsync(new FkItx1Child { ParentId = 1 });
        // 孤儿插入被引擎拒绝（SQLite 错误 787 FOREIGN KEY constraint failed，
        // 原生驱动异常冒出——InsertAsync 只翻译唯一冲突族）
        await Assert.That(async () =>
            await db.InsertAsync(new FkItx1Child { ParentId = 999 }))
            .Throws<Microsoft.Data.Sqlite.SqliteException>();
        await Assert.That(await db.CountAsync<FkItx1Child>()).IsEqualTo(1);
    }

    [Test]
    public async Task Fk_CascadeDelete_RemovesChildRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.ExecuteAsync($"PRAGMA foreign_keys = ON");
        await db.MigrateAsync();

        await db.InsertAsync(new FkItx0Parent { Name = "p1" });
        await db.InsertAsync(new FkItx1Child { ParentId = 1 });
        await db.InsertAsync(new FkItx1Child { ParentId = 1 });
        await Assert.That(await db.CountAsync<FkItx1Child>()).IsEqualTo(2);

        // OnDelete = Cascade 真传播：删 parent 行连带清 child。
        // DELETE 的列匹配用全表删（本测试仅一行）——插值洞会被参数化，
        // 把列名 {"Id"} 放洞里会变成 WHERE 'Id' = 1 恒假（静默删 0 行）
        await db.ExecuteAsync($"DELETE FROM fk_itx_parent");
        await Assert.That(await db.CountAsync<FkItx1Child>()).IsEqualTo(0);
    }

    [Test]
    public async Task Fk_MigrateTwice_Idempotent()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.ExecuteAsync($"PRAGMA foreign_keys = ON");
        // 内联 FK 随 CREATE TABLE IF NOT EXISTS 幂等——二次迁移无重名约束问题
        await db.MigrateAsync();
        await db.MigrateAsync();

        long fkCount = await db.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM pragma_foreign_key_list('fk_itx_child')");
        // FkItx1Child 声明 1 个 FK（快照里的 FkChildEntity 是 3 个，别混）
        await Assert.That(fkCount).IsEqualTo(1);
    }

    // ─── PG / MySQL（真库，FK 默认强制） ───

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task Pg_FkDdl_EnforcedByDatabase()
    {
        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
        });
        // 不做开头自清理：TUnit 并行下其他测试的 MigrateAsync 依赖 fk 表存在性，
        // 开头 DROP 会与并行建表竞态（42P01）。空库起点自洽由字典序保证（FkItx0Parent 先建）
        try
        {
            await session.MigrateAsync();
            await session.InsertAsync(new FkItx0Parent { Name = "p1" });
            await session.InsertAsync(new FkItx1Child { ParentId = 1 });
            // 孤儿插入被真库拒绝（PG 23503 foreign_key_violation）
            await Assert.That(async () =>
                await session.InsertAsync(new FkItx1Child { ParentId = 999 }))
                .Throws<Npgsql.NpgsqlException>();
            await Assert.That(await session.CountAsync<FkItx1Child>()).IsEqualTo(1);
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS fk_itx_child");
            await session.ExecuteAsync($"DROP TABLE IF EXISTS fk_itx_parent");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_FkDdl_EnforcedByDatabase()
    {
        await using var session = await DataSession<MySqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
        });
        // 不做开头自清理：并行竞态同 PG 侧注释
        try
        {
            await session.MigrateAsync();
            await session.InsertAsync(new FkItx0Parent { Name = "p1" });
            await session.InsertAsync(new FkItx1Child { ParentId = 1 });
            // 孤儿插入被真库拒绝（MySQL 1452 Cannot add or update a child row）
            await Assert.That(async () =>
                await session.InsertAsync(new FkItx1Child { ParentId = 999 }))
                .Throws<MySqlConnector.MySqlException>();
            await Assert.That(await session.CountAsync<FkItx1Child>()).IsEqualTo(1);
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS fk_itx_child");
            await session.ExecuteAsync($"DROP TABLE IF EXISTS fk_itx_parent");
        }
    }
}

#region Test Entities
// 表名前缀 fk_itx_ 避免与真库其他夹具冲突；引用表名经 PALORM003 存在性校验（同编译单元）。
// Type 名数字后缀是刻意为之：MigrateAsync 按 Type 名字典序建表，被引用表（0Parent）必须
// 先于引用表（1Child）建，空库上前向引用会报 errno 1824（ADR-B 实施记录二·限制③）
[Table("fk_itx_parent")]
public partial class FkItx0Parent
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

[Table("fk_itx_child")]
public partial class FkItx1Child
{
    [Key] public long Id { get; set; }
    // 引用列 "Id" 必须与被引用实体的实际映射列名逐字对齐——PG 引号标识符大小写敏感，
    // 写 "id" 在 PG 上报 42703（Integration 全实体共享 Registry，一处坏 FK 炸全部 MigrateAsync）
    [Column("parent_id")]
    [ForeignKey("fk_itx_parent", "Id", OnDelete = DeleteAction.Cascade)]
    public long ParentId { get; set; }
}
#endregion
