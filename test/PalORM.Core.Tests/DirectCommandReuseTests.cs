using PalORM.Sqlite;

// PALORM005（N+1 检测）：本组观察点正是"同一会话内同文本直查命令的跨阈值复用"——
// 晋升阈值 3 的跨阈值行为必须逐次调用才能触发，循环内 DB 调用是被测行为本身而非反模式
#pragma warning disable PALORM005

namespace PalORM.Core.Tests;

/// <summary>P1-1（2026-10-12 全量重读）：直查族（QueryAsync/QueryFirstAsync/Scalar/Count/
/// ExecuteAsync）命令复用槽的行为契约——A9（From&lt;T&gt; 查询管线）的惰性晋升推广。
/// <para>晋升阈值 3（C15 按形状计数）：同 SQL 文本第 3 次起复用会话级命令，参数值就地写
/// （不 Clear 不 Add）。本组全部用行为断言：参数错位/类型陈旧/值残留都会在结果值上暴露。</para>
/// <para>覆盖面：① 跨阈值值一致性（Query/Scalar/Count/Execute 四入口）；② 流式枚举
/// （QueryFirstAsync 经 QueryAsyncEnumerable）；③ 同文本异类型值就地写失败回退新建；
/// ④ 与 From&lt;T&gt; 管线共用单槽的交替形态（C15：互不淘汰）；⑤ 并行读作用域内不复用
/// 仍正确；⑥ 事务内直查正确（Transaction 每次重设）。</para></summary>
public sealed class DirectCommandReuseTests
{
    [Test]
    public async Task QueryAsync_AcrossPromotionThreshold_ReturnsRowsForLatestParameterValues()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await InsertRowsAsync(session, (1, "a", 10), (2, "b", 20), (3, "c", 30));

        // 第 1、2 次：新建路径；第 3 次起：复用路径——就地写值必须每次生效（残留上一轮
        // Value 会立刻在返回行上暴露）
        int[] qtyThresholds = [10, 20, 30];
        for (int round = 0; round < 6; round++)
        {
            int thresholdIndex = round % 3;
            int qtyThreshold = qtyThresholds[thresholdIndex];
            List<DirectReuseEntity> rows = await session.QueryAsync<DirectReuseEntity>(
                $"SELECT Id, Name, Qty FROM direct_reuse_rows WHERE Qty >= {qtyThreshold}");
            await Assert.That(rows.Count).IsEqualTo(3 - thresholdIndex);
            foreach (DirectReuseEntity row in rows)
                await Assert.That(row.Qty).IsGreaterThanOrEqualTo(qtyThreshold);
        }
    }

    [Test]
    public async Task ScalarAsync_AcrossPromotionThreshold_ReflectsLatestValue()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await InsertRowsAsync(session, (1, "a", 10), (2, "b", 20));

        for (int round = 0; round < 6; round++)
        {
            int id = round % 2 == 0 ? 1 : 2;
            int expectedQty = round % 2 == 0 ? 10 : 20;
            long qty = await session.ScalarAsync<long>(
                $"SELECT Qty FROM direct_reuse_rows WHERE Id = {id}");
            await Assert.That(qty).IsEqualTo(expectedQty);
        }
    }

    [Test]
    public async Task CountAsync_WithWhere_AcrossPromotionThreshold_CountsCorrectly()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await InsertRowsAsync(session, (1, "a", 10), (2, "b", 20), (3, "c", 30));

        for (int threshold = 0; threshold < 6; threshold++)
        {
            long count = await session.CountAsync<DirectReuseEntity>($"Qty > {threshold * 5}");
            // Qty ∈ {10,20,30}：阈值 0/5/10/15/20/25 → 3/3/2/2/1/1 行
            int expectedCount = threshold switch { < 2 => 3, < 4 => 2, _ => 1 };
            await Assert.That(count).IsEqualTo(expectedCount);
        }
    }

    [Test]
    public async Task ExecuteAsync_AcrossPromotionThreshold_AppliesLatestValues()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await InsertRowsAsync(session, (1, "a", 10), (2, "b", 20));

        // 同文本 UPDATE 跨阈值：每次的 SET 值与 WHERE 键都必须生效（复用命令的就地写值）
        for (int round = 1; round <= 6; round++)
        {
            int affected = await session.ExecuteAsync(
                $"UPDATE direct_reuse_rows SET Qty = {round * 100} WHERE Id = {(round % 2) + 1}");
            await Assert.That(affected).IsEqualTo(1);
        }

        List<DirectReuseEntity> all = await session.QueryAsync<DirectReuseEntity>(
            $"SELECT Id, Name, Qty FROM direct_reuse_rows ORDER BY Id");
        await Assert.That(all[0].Qty).IsEqualTo(600);   // round 6 → Id=1
        await Assert.That(all[1].Qty).IsEqualTo(500);   // round 5 → Id=2
    }

    [Test]
    public async Task QueryFirstAsync_ViaStreamingEnumerator_CrossesThresholdCorrectly()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await InsertRowsAsync(session, (1, "a", 10), (2, "b", 20));

        // QueryFirstAsync 经 QueryAsyncEnumerable 迭代器（流式路径的复用分支）
        for (int round = 0; round < 6; round++)
        {
            int minQty = round % 2 == 0 ? 10 : 20;
            DirectReuseEntity first = await session.QueryFirstAsync<DirectReuseEntity>(
                $"SELECT Id, Name, Qty FROM direct_reuse_rows WHERE Qty >= {minQty} ORDER BY Id");
            await Assert.That(first.Name).IsEqualTo(round % 2 == 0 ? "a" : "b");
        }
    }

    [Test]
    public async Task SameText_DifferentValueTypes_FallsBackToFreshCommandSafely()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await InsertRowsAsync(session, (1, "a", 10), (2, "b", 20));

        // 先用 string 参数触发晋升（3 次同文本），再传 int——就地写的类型守卫拒绝
        //（PG DbType 陈旧错绑防线），回退新建命令；SQLite 下 TEXT 列对 int 值无匹配行，
        // 两形态都不得抛异常且结果正确
        for (int i = 0; i < 3; i++)
        {
            List<DirectReuseEntity> rows = await session.QueryAsync<DirectReuseEntity>(
                $"SELECT Id, Name, Qty FROM direct_reuse_rows WHERE Name = {"a"}");
            await Assert.That(rows.Count).IsEqualTo(1);
        }
        List<DirectReuseEntity> intMatch = await session.QueryAsync<DirectReuseEntity>(
            $"SELECT Id, Name, Qty FROM direct_reuse_rows WHERE Name = {7}");
        await Assert.That(intMatch.Count).IsEqualTo(0);
        // 再回 string：槽内命令仍是 string 形态（未被淘汰），继续复用且正确
        List<DirectReuseEntity> backToString = await session.QueryAsync<DirectReuseEntity>(
            $"SELECT Id, Name, Qty FROM direct_reuse_rows WHERE Name = {"b"}");
        await Assert.That(backToString.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AlternatingWithBuilderQueries_SingleSlotNeverCrossContaminates()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await InsertRowsAsync(session, (1, "a", 10), (2, "b", 20));

        // From<T>().ToListAsync（A9）与直查 QueryAsync 共用同一槽：交替形态经 C15
        // 按形状计数互不晋升淘汰；直查连续 3 次晋升后，builder 查询照常走各自路径
        for (int round = 0; round < 4; round++)
        {
            List<DirectReuseEntity> builderRows = await session.From<DirectReuseEntity>()
                .Where($"Qty > {5}").ToListAsync();
            List<DirectReuseEntity> directRows = await session.QueryAsync<DirectReuseEntity>(
                $"SELECT Id, Name, Qty FROM direct_reuse_rows WHERE Qty > {5}");
            await Assert.That(builderRows.Count).IsEqualTo(2);
            await Assert.That(directRows.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ParallelReadScope_DirectQueriesStillCorrect()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await InsertRowsAsync(session, (1, "a", 10), (2, "b", 20));

        // 并行读作用域内直查不复用（守卫返回 null），走池连接 + 新建命令——正确性不受影响
        for (int round = 0; round < 4; round++)
        {
            await using (session.ForParallelReads())
            {
                List<DirectReuseEntity> rows = await session.QueryAsync<DirectReuseEntity>(
                    $"SELECT Id, Name, Qty FROM direct_reuse_rows WHERE Qty > {5}");
                await Assert.That(rows.Count).IsEqualTo(2);
            }
        }
    }

    [Test]
    public async Task InsideTransaction_DirectUpdateAndQueryHonorTransaction()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await InsertRowsAsync(session, (1, "a", 10), (2, "b", 20));

        // 事务内直查/直写：复用命令的 Transaction 每次重设（CRUD 槽同口径），
        // 提交后可见、回滚后不可见——复用路径不得把语句带出事务
        await session.WithTransaction(async ct =>
        {
            int affected = await session.ExecuteAsync(
                $"UPDATE direct_reuse_rows SET Qty = {999} WHERE Id = {1}", ct);
            await Assert.That(affected).IsEqualTo(1);
            long qty = await session.ScalarAsync<long>(
                $"SELECT Qty FROM direct_reuse_rows WHERE Id = {1}", ct: ct);
            await Assert.That(qty).IsEqualTo(999L);
        });
        await Assert.That((await session.GetAsync<DirectReuseEntity>(1))!.Qty).IsEqualTo(999);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await session.WithTransaction(async ct =>
            {
                await session.ExecuteAsync($"UPDATE direct_reuse_rows SET Qty = {777} WHERE Id = {1}", ct);
                throw new InvalidOperationException("rollback");
            });
        });
        await Assert.That((await session.GetAsync<DirectReuseEntity>(1))!.Qty).IsEqualTo(999);
    }

    private static async Task InsertRowsAsync(
        DataSession<SqliteProvider> session, params (int Id, string Name, int Qty)[] rows)
    {
        foreach ((int id, string name, int qty) in rows)
            await session.ExecuteAsync(
                $"INSERT INTO direct_reuse_rows (Id, Name, Qty) VALUES ({id}, {name}, {qty})");
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=direct_reuse_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE direct_reuse_rows (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Qty INTEGER NOT NULL)");
        return session;
    }
}

[Table("direct_reuse_rows")]
internal sealed partial class DirectReuseEntity
{
    [Key] public long Id { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
    [Column("Qty")] public int Qty { get; set; }
}
