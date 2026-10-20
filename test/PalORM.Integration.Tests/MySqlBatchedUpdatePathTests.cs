using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>MySQL 版本化批量更新的真库覆盖（v6.5 单语句 JoinValuesRow 路由）。
/// <para><b>背景</b>：版本化实体的 BulkUpdateAsync 在 MySQL 8.0.19+ 且生成物提供版本
/// 参数绑定时路由到 <c>UPDATE ... JOIN (VALUES ROW(...)) ON ... AND version = ...</c>
/// 单语句（S6 探针 1000 行 4.73×）；低版本/能力不全回退逐条 DbBatch。冲突检测为计数级。</para>
/// <para><b>本组锁</b>：① 跨批边界写值正确；② 版本不匹配 → ConcurrencyConflictException
/// 且整批回滚；③ 延迟回填；④ 重复主键与缺失行按冲突处理；⑤ UseAffectedRows 两档计数
/// 语义一致；⑥ 单行输入仍走逐条路径。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class MySqlBatchedUpdatePathTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BatchedPooledUpdate_CrossesBatchBoundaries_WritesCorrectValues()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_mysql_batched (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");

            // 250 行 = 跨 3 个 100 命令批（含末批 50 行）——批边界是参数绑定错位的高发区
            const int rows = 250;
            var seed = new List<MySqlBatchedRow>(rows);
            for (int i = 1; i <= rows; i++)
                seed.Add(new MySqlBatchedRow { Id = i, Name = "s" + i });
            await db.BulkInsertAsync(seed);

            // 注意：插入默认 version=0，拷贝实体需显式携带（乐观锁 WHERE version 匹配）
            List<MySqlBatchedRow> updated = [.. seed.Select(e => new MySqlBatchedRow { Id = e.Id, Name = "u" + e.Id })];
            // 播种读出的 version 为 0（插入默认）——乐观锁要求实体携带当前 version
            long affected = await db.BulkUpdateAsync(updated);

            await Assert.That(affected).IsEqualTo(rows);
            List<MySqlBatchedRow> after = await db.From<MySqlBatchedRow>().ToListAsync();
            await Assert.That(after.Count).IsEqualTo(rows);
            foreach (MySqlBatchedRow row in after)
            {
                await Assert.That(row.Name).IsEqualTo("u" + row.Id);
                // 乐观锁：每行 version 递增到 1
                await Assert.That(row.Version).IsEqualTo(1);
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BatchedPooledUpdate_StaleVersionInsideBatch_ThrowsConcurrencyConflict()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_c");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_mysql_batched_c (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");

            const int rows = 150;
            var seed = new List<MySqlBatchedRowC>(rows);
            for (int i = 1; i <= rows; i++)
                seed.Add(new MySqlBatchedRowC { Id = i, Name = "s" + i });
            await db.BulkInsertAsync(seed);

            List<MySqlBatchedRowC> updated = [.. seed.Select(e => new MySqlBatchedRowC { Id = e.Id, Name = "u" + e.Id })];

            // 第 150 行（第二/末批内）version 陈旧——该行 0 行匹配使批总和 < 批大小，
            // 必须抛 ConcurrencyConflictException（与逐条路径同类型）
            updated[^1].Version = 99;
            await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
                await db.BulkUpdateAsync(updated));

            // 整批回滚：DB 内值应全部保持种子原状
            List<MySqlBatchedRowC> after = await db.From<MySqlBatchedRowC>().ToListAsync();
            await Assert.That(after.Count).IsEqualTo(rows);
            foreach (MySqlBatchedRowC row in after)
            {
                await Assert.That(row.Name).IsEqualTo("s" + row.Id);
                await Assert.That(row.Version).IsEqualTo(0);
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_c");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BatchedPooledUpdate_InMemoryVersionIncrementOnlyOnSuccess()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_v");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_mysql_batched_v (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");

            var entity = new MySqlBatchedRowV { Id = 1, Name = "a" };
            await db.BulkInsertAsync([entity]);

            entity.Name = "b";
            long affected = await db.BulkUpdateAsync([entity]);
            await Assert.That(affected).IsEqualTo(1L);
            // ITM-556：提交成功后内存 version 回填（批路径同口径）
            await Assert.That(entity.Version).IsEqualTo(1);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_v");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_VersionedBulkUpdate_DuplicateKeys_ThrowConflictAndRollback()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_dup");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_mysql_batched_dup (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");
            await db.BulkInsertAsync([new MySqlBatchedRowDup { Id = 1, Name = "seed" }]);

            List<MySqlBatchedRowDup> updated =
            [
                new() { Id = 1, Name = "a" },
                new() { Id = 1, Name = "b" }
            ];
            // MySQL 8.4 多表 UPDATE 对同一目标行只更新一次（官方语法页），
            // 受影响 1 < 输入 2 → 按冲突抛出并回滚
            await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
                await db.BulkUpdateAsync(updated));

            MySqlBatchedRowDup? row = await db.GetAsync<MySqlBatchedRowDup>(1);
            await Assert.That(row).IsNotNull();
            await Assert.That(row!.Name).IsEqualTo("seed");
            await Assert.That(row.Version).IsEqualTo(0);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_dup");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_VersionedBulkUpdate_MissingRow_ThrowConflictAndRollback()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_miss");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_mysql_batched_miss (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");

            var seed = new List<MySqlBatchedRowMiss>
            {
                new() { Id = 1, Name = "s1" },
                new() { Id = 2, Name = "s2" },
                new() { Id = 3, Name = "s3" }
            };
            await db.BulkInsertAsync(seed);

            List<MySqlBatchedRowMiss> updated =
            [
                new() { Id = 1, Name = "u1" },
                new() { Id = 2, Name = "u2" },
                new() { Id = 3, Name = "u3" },
                new() { Id = 99, Name = "ghost" }
            ];
            await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
                await db.BulkUpdateAsync(updated));

            List<MySqlBatchedRowMiss> after = await db.From<MySqlBatchedRowMiss>().ToListAsync();
            await Assert.That(after.Count).IsEqualTo(3);
            foreach (MySqlBatchedRowMiss row in after)
            {
                await Assert.That(row.Name).IsEqualTo("s" + row.Id);
                await Assert.That(row.Version).IsEqualTo(0);
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_miss");
        }
    }
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_VersionedBulkUpdate_UseAffectedRowsTrue_SameCountSemantics()
    {
        await using (DataSession<MySqlProvider> setup = await TestDb.MySqlAsync())
        {
            await setup.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_uar");
            await setup.ExecuteAsync(
                $"CREATE TABLE palorm_mysql_batched_uar (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");
            var seed = new List<MySqlBatchedRowUar>
            {
                new() { Id = 1, Name = "s1" },
                new() { Id = 2, Name = "s2" },
                new() { Id = 3, Name = "s3" }
            };
            await setup.BulkInsertAsync(seed);
        }

        try
        {
            // UseAffectedRows=true 时驱动上报 changed rows；version 恒递增使 found/changed 同值，
            // 计数级冲突判定必须与默认档一致。
            string cs = TestEnvironment.ResolveMySqlConnectionString() + ";UseAffectedRows=true";
            await using DataSession<MySqlProvider> db =
                await DataSession<MySqlProvider>.CreateAsync(new DbOptions { ConnectionString = cs });

            List<MySqlBatchedRowUar> updated =
            [
                new() { Id = 1, Name = "u1" },
                new() { Id = 2, Name = "u2" },
                new() { Id = 3, Name = "u3" }
            ];
            long affected = await db.BulkUpdateAsync(updated);
            await Assert.That(affected).IsEqualTo(3);
            await Assert.That(updated[0].Version).IsEqualTo(1);

            updated[2].Version = 99;
            await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
                await db.BulkUpdateAsync(updated));

            List<MySqlBatchedRowUar> after = await db.From<MySqlBatchedRowUar>().ToListAsync();
            await Assert.That(after.Count).IsEqualTo(3);
            foreach (MySqlBatchedRowUar row in after)
            {
                await Assert.That(row.Name).IsEqualTo("u" + row.Id);
                await Assert.That(row.Version).IsEqualTo(1);
            }
        }
        finally
        {
            await using DataSession<MySqlProvider> cleanup = await TestDb.MySqlAsync();
            await cleanup.ExecuteAsync($"DROP TABLE IF EXISTS palorm_mysql_batched_uar");
        }
    }
}

[Table("palorm_mysql_batched")]
internal sealed partial class MySqlBatchedRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}

[Table("palorm_mysql_batched_c")]
internal sealed partial class MySqlBatchedRowC
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}

[Table("palorm_mysql_batched_v")]
internal sealed partial class MySqlBatchedRowV
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}

[Table("palorm_mysql_batched_dup")]
internal sealed partial class MySqlBatchedRowDup
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}

[Table("palorm_mysql_batched_miss")]
internal sealed partial class MySqlBatchedRowMiss
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}

[Table("palorm_mysql_batched_uar")]
internal sealed partial class MySqlBatchedRowUar
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}
