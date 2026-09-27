using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>MySQL-8（2026-09-27）：MySQL dialect 的逐条 UPDATE DbBatch 打包路径真库覆盖。
/// <para><b>背景</b>：乐观锁（[ConcurrencyCheck]）实体不能走单语句批量（无法表达"每行
/// version 匹配"），BulkUpdateAsync 路由到 ExecuteBulkUpdatePooledAsync 逐条路径——
/// N 行 = N 次协议往返。MySQL dialect 起该方法改用 DbBatch 打包（100 命令/批一次往返，
/// 探针十六实测 3.55×）。批内受影响行数按<b>总和</b>判定乐观锁（单行 UPDATE 每行恰 1 行）。</para>
/// <para><b>本组锁三件事</b>：① 批路径跨批边界（250 行 = 3 批）写值全部正确；
/// ② 批内 version 不匹配 → ConcurrencyConflictException（总和 &lt; 批大小）；
/// ③ ITM-556 延迟回填语义（仅批成功后内存 version +1）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class MySqlBatchedUpdatePathTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BatchedPooledUpdate_CrossesBatchBoundaries_WritesCorrectValues()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
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

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BatchedPooledUpdate_StaleVersionInsideBatch_ThrowsConcurrencyConflict()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
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

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BatchedPooledUpdate_InMemoryVersionIncrementOnlyOnSuccess()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
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
