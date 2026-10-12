using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>P0-2（2026-10-12 全量重读）：WhereIn/WhereNotIn 的 PG 数组形态真库行为锁定。
/// <para>本组在真 PG 上验证：① <c>= ANY(数组)</c> 与占位符 IN 的行集等价（含空结果与
/// 全命中边界）；② 超过占位符单批 500 上限的大列表在数组形态下整列表单参数正确执行
///（原路径需要 2 个 IN 批片 OR 组合）；③ NULL 语义：数组 NULL 元素在 = ANY 下永不匹配、
/// NOT IN 含 NULL 时全行过滤（经典语义，数组形态不得改变）。SQLite 走占位符回退路径
/// 作行为对照基线。</para>
/// <para><b>串行槽</b>：ExternalDatabase 用例统一挂 ExtBulkTable 守卫键
///（B63 家族——ExternalDbIsolationInvariantTests 门禁锁定）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class WhereInArrayFormIntegrationTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_WhereIn_ArrayForm_RowSetMatchesInSemantics()
        => await RunInSemanticsAsync(await TestDb.PostgreSqlAsync());

    [Test]
    public async Task Sqlite_WhereIn_PlaceholderFallback_SameRowSet()
        => await RunInSemanticsAsync(await TestDb.SqliteAsync());

    private static async Task RunInSemanticsAsync<TProvider>(DataSession<TProvider> db)
        where TProvider : IDbProvider
    {
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_wherein_form");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_wherein_form (id BIGINT PRIMARY KEY, name VARCHAR(32) NOT NULL, qty INT NOT NULL)");
            for (int i = 1; i <= 5; i++)
                await db.ExecuteAsync(
                    $"INSERT INTO palorm_wherein_form (id, name, qty) VALUES ({(long)i}, {"n" + i}, {i * 10})");

            // 命中子集 / 全命中 / 空交集三形态
            long[] subset = await IdsOf(db, [10, 30]);
            await Assert.That(string.Join(",", subset)).IsEqualTo("1,3");
            long[] all = await IdsOf(db, [10, 20, 30, 40, 50]);
            await Assert.That(string.Join(",", all)).IsEqualTo("1,2,3,4,5");
            long[] none = await IdsOf(db, [999]);
            await Assert.That(none.Length).IsEqualTo(0);

            // NOT IN：排除形态
            List<WhereInFormEntity> excluded = await db.From<WhereInFormEntity>()
                .WhereNotIn(x => x.Qty, [20])
                .OrderBy(x => x.Id).ToListAsync();
            await Assert.That(excluded.Count).IsEqualTo(4);

            // 空列表语义不变：IN 空 = 恒假；NOT IN 空 = no-op
            long[] inEmpty = await IdsOf(db, []);
            await Assert.That(inEmpty.Length).IsEqualTo(0);
            List<WhereInFormEntity> allRows = await db.From<WhereInFormEntity>()
                .WhereNotIn<int>(x => x.Qty, []).OrderBy(x => x.Id).ToListAsync();
            await Assert.That(allRows.Count).IsEqualTo(5);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_wherein_form");
            await db.DisposeAsync();
        }
    }

    private static async Task<long[]> IdsOf<TProvider>(DataSession<TProvider> db, int[] quantities)
        where TProvider : IDbProvider
    {
        List<WhereInFormEntity> rows = await db.From<WhereInFormEntity>()
            .WhereIn(x => x.Qty, quantities)
            .OrderBy(x => x.Id).ToListAsync();
        return [.. rows.Select(static r => r.Id)];
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_WhereIn_ArrayForm_LargeListBeyondPlaceholderBatch()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_wherein_big");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_wherein_big (id BIGINT PRIMARY KEY, qty INT NOT NULL)");
            // 600 行，qty = 100..699：600 值 > 占位符单批 500 上限——数组形态单参数整列表执行
            List<WhereInBigEntity> seed = [];
            for (int i = 0; i < 600; i++)
                seed.Add(new WhereInBigEntity { Id = i + 1, Qty = 100 + i });
            await db.BulkInsertAsync(seed);

            List<WhereInBigEntity> all = await db.From<WhereInBigEntity>()
                .WhereIn(x => x.Qty, [.. Enumerable.Range(100, 600)])
                .OrderBy(x => x.Id).ToListAsync();
            await Assert.That(all.Count).IsEqualTo(600);

            // 末段 550 个值（跨过原 500 上限的批片边界）
            List<WhereInBigEntity> tail = await db.From<WhereInBigEntity>()
                .WhereIn(x => x.Qty, [.. Enumerable.Range(150, 550)])
                .OrderBy(x => x.Id).ToListAsync();
            await Assert.That(tail.Count).IsEqualTo(550);
            await Assert.That(tail[0].Id).IsEqualTo(51L);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_wherein_big");
            await db.DisposeAsync();
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_WhereIn_NullElementSemantics()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_wherein_null");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_wherein_null (id BIGINT PRIMARY KEY, name VARCHAR(32))");
            await db.ExecuteAsync($"INSERT INTO palorm_wherein_null (id, name) VALUES ({1L}, {"a"})");
            await db.ExecuteAsync($"INSERT INTO palorm_wherein_null (id, name) VALUES ({2L}, {"b"})");

            // = ANY 含 NULL 元素：NULL 永不匹配，只有 "b" 行命中
            List<WhereInNullEntity> inList = await db.From<WhereInNullEntity>()
                .WhereIn(x => x.Name!, ["b", null!]).OrderBy(x => x.Id).ToListAsync();
            await Assert.That(inList.Count).IsEqualTo(1);
            await Assert.That(inList[0].Name).IsEqualTo("b");

            // NOT IN 含 NULL 元素：全行过滤（经典语义，数组形态不得"顺手修好"）
            List<WhereInNullEntity> notIn = await db.From<WhereInNullEntity>()
                .WhereNotIn(x => x.Name!, ["a", null!]).ToListAsync();
            await Assert.That(notIn.Count).IsEqualTo(0);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_wherein_null");
            await db.DisposeAsync();
        }
    }
}

[Table("palorm_wherein_form")]
internal sealed partial class WhereInFormEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("qty")]
    public int Qty { get; set; }
}

[Table("palorm_wherein_big")]
internal sealed partial class WhereInBigEntity
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("qty")]
    public int Qty { get; set; }
}

[Table("palorm_wherein_null")]
internal sealed partial class WhereInNullEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string? Name { get; set; }
}
