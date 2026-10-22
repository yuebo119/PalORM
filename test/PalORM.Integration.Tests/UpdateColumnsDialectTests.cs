using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

[Table("palorm_partial_mysql")]
internal sealed partial class PartialUpdateMySqlRow
{
    [Key(AutoIncrement = false)][Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("score")] public long score { get; set; }
    [ConcurrencyCheck][Column("version")] public long version { get; set; }
}

[Table("palorm_partial_pg")]
internal sealed partial class PartialUpdatePgRow
{
    [Key(AutoIncrement = false)][Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("score")] public long score { get; set; }
    [ConcurrencyCheck][Column("version")] public long version { get; set; }
}

/// <summary>列子集更新的 PostgreSQL 真库校验：生成 UPDATE 文本的 WHERE/版本列解析、
/// 参数裁剪与重编号在非 SQLite 方言上同样成立。</summary>
[NotInParallel("ExtBulkTable")]
internal sealed class UpdateColumnsDialectTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task MySql_UpdateColumns_OnlySelectedColumn_AndVersionGuard()
    {
        await using var session = await DataSession<MySqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_partial_mysql");
        await session.ExecuteAsync(
            $"CREATE TABLE palorm_partial_mysql (id BIGINT PRIMARY KEY, name VARCHAR(64) NOT NULL, score BIGINT NOT NULL, version BIGINT NOT NULL)");
        try
        {
            await session.InsertAsync(new PartialUpdateMySqlRow { id = 1, name = "before", score = 42, version = 1 });
            var entity = new PartialUpdateMySqlRow { id = 1, name = "after", score = 999, version = 1 };
            int affected = await session.UpdateColumnsAsync(entity, x => x.name);
            await Assert.That(affected).IsEqualTo(1);
            PartialUpdateMySqlRow? stored = await session.GetAsync<PartialUpdateMySqlRow>(1L);
            await Assert.That(stored?.name).IsEqualTo("after");
            await Assert.That(stored?.score).IsEqualTo(42L);
            await Assert.That(stored?.version).IsEqualTo(2L);

            var stale = new PartialUpdateMySqlRow { id = 1, name = "stale", score = 0, version = 1 };
            await Assert.ThrowsAsync<ConcurrencyConflictException>(
                async () => await session.UpdateColumnsAsync(stale, x => x.name));
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_partial_mysql");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task PostgreSql_UpdateColumns_OnlySelectedColumn_AndVersionGuard()
    {
        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_partial_pg");
        await session.ExecuteAsync(
            $"CREATE TABLE palorm_partial_pg (id BIGINT PRIMARY KEY, name TEXT NOT NULL, score BIGINT NOT NULL, version BIGINT NOT NULL)");
        try
        {
            await session.InsertAsync(new PartialUpdatePgRow { id = 1, name = "before", score = 42, version = 1 });

            var entity = new PartialUpdatePgRow { id = 1, name = "after", score = 999, version = 1 };
            int affected = await session.UpdateColumnsAsync(entity, x => x.name);
            await Assert.That(affected).IsEqualTo(1);
            await Assert.That(entity.version).IsEqualTo(2L);

            PartialUpdatePgRow? stored = await session.GetAsync<PartialUpdatePgRow>(1L);
            await Assert.That(stored?.name).IsEqualTo("after");
            await Assert.That(stored?.score).IsEqualTo(42L);
            await Assert.That(stored?.version).IsEqualTo(2L);

            var stale = new PartialUpdatePgRow { id = 1, name = "stale", score = 0, version = 1 };
            await Assert.ThrowsAsync<ConcurrencyConflictException>(
                async () => await session.UpdateColumnsAsync(stale, x => x.name));
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_partial_pg");
        }
    }
}
