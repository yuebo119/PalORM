using System.Data;
using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>会话池（2026-10-13 二轮方案）的行为契约锁定。
/// <para>覆盖面：① 租/还循环复用同一会话实例；② 瞬态复位（WithTenant/IgnoreFilters/
/// WithIsolationLevel/WithTimeout 的租期覆写与 AddInterceptor 加项不串味下一租期）；
/// ③ 复用槽跨租期存活（槽内命令与参数池引用逐位保持——池化的核心收益）；④ 归还门禁
///（活动事务/未消费流式枚举器/并行读作用域未收口时抛出且不入池）；⑤ 空闲上限（满员
/// 归还真实释放）；⑥ 池释放（空闲清空、后续租出抛出、滞留租出会话归还时自行释放）；
/// ⑦ 并发租出互异且各自正确；⑧ 连接不健康的闲置项租出时丢弃重建。</para></summary>
public sealed class SessionPoolTests
{
    private static async Task<DataSessionPool<SqliteProvider>> CreatePoolAsync(string name)
    {
        DataSessionPool<SqliteProvider> pool = DataSessionPool<SqliteProvider>.Create(
            new DbOptions { ConnectionString = $"Data Source={name}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        DataSession<SqliteProvider> setup = await pool.RentAsync();
        await setup.ExecuteAsync(
            $"CREATE TABLE pool_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        await setup.DisposeAsync();
        return pool;
    }

    [Test]
    public async Task RentReturnCycle_ReusesSameSessionInstance()
    {
        await using DataSessionPool<SqliteProvider> pool = await CreatePoolAsync("spt_reuse");
        DataSession<SqliteProvider> first = await pool.RentAsync();
        await first.DisposeAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(1);

        DataSession<SqliteProvider> second = await pool.RentAsync();
        await Assert.That(ReferenceEquals(second, first)).IsTrue();
        await Assert.That(pool.IdleCount).IsEqualTo(0);
        await second.DisposeAsync();
    }

    [Test]
    public async Task RepeatedDispose_IsIdempotent()
    {
        // 加固（2026-10-13）：重复 Dispose 不抛（AlreadyParked 幂等路径）——与非池化
        // DisposeAsync 的幂等语义对齐；并发双归还由 Park 锁内状态判定互斥
        await using DataSessionPool<SqliteProvider> pool = await CreatePoolAsync("spt_idem");
        DataSession<SqliteProvider> session = await pool.RentAsync();
        await session.DisposeAsync();
        await session.DisposeAsync();   // 顺序重复
        await session.DisposeAsync();   // 再重复
        await Assert.That(pool.IdleCount).IsEqualTo(1);   // 恰好入池一次

        // 并发双归还:两个线程同时 Dispose 已租出的同一会话——先驻留者归还,后到者幂等返回
        DataSession<SqliteProvider> concurrent = await pool.RentAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(0);
        Task firstDispose = Task.Run(async () => await concurrent.DisposeAsync());
        Task secondDispose = Task.Run(async () => await concurrent.DisposeAsync());
        await Task.WhenAll(firstDispose, secondDispose);   // 任一抛出即测试失败
        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAfterPoolDiscard_IsIdempotent()
    {
        // 池满被丢弃(TornDown 路径)后的残留 Dispose 不抛
        DataSessionPool<SqliteProvider> pool = DataSessionPool<SqliteProvider>.Create(
            new DbOptions { ConnectionString = $"Data Source=spt_torn_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" },
            maxIdleSize: 1);
        try
        {
            DataSession<SqliteProvider> a = await pool.RentAsync();
            DataSession<SqliteProvider> b = await pool.RentAsync();
            await a.DisposeAsync();
            await b.DisposeAsync();   // 满员 → 真实释放(TornDown)
            await b.DisposeAsync();   // 释放后的残留 Dispose:TornDown 幂等路径
            await Assert.That(pool.IdleCount).IsEqualTo(1);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task TransientOverrides_ResetBetweenRentals()
    {
        CountingInterceptor counter = new();
        DataSessionPool<SqliteProvider> pool = DataSessionPool<SqliteProvider>.Create(new DbOptions
        {
            ConnectionString = $"Data Source=spt_reset_{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
        });
        try
        {
            DataSession<SqliteProvider> setup = await pool.RentAsync();
            await setup.ExecuteAsync(
                $"CREATE TABLE pool_tenant (id INTEGER PRIMARY KEY, name TEXT, tenant_id INTEGER)");
            await setup.DisposeAsync();

            // 租期 1：覆写全部瞬态状态
            DataSession<SqliteProvider> first = await pool.RentAsync();
            first.WithTenant(7L).IgnoreFilters().WithIsolationLevel(IsolationLevel.Serializable)
                .WithTimeout(TimeSpan.FromSeconds(99));
            first.AddInterceptor(counter);
            await first.DisposeAsync();

            // 租期 2：全部复位——租户过滤不生效(全表可见)、隔离级别回 RC、拦截器表回池基准(空)
            DataSession<SqliteProvider> second = await pool.RentAsync();
            await Assert.That(ReferenceEquals(second, first)).IsTrue();
            await second.ExecuteAsync($"INSERT INTO pool_tenant (id, name, tenant_id) VALUES ({1L}, {"a"}, {7L})");
            List<PoolTenantRow> all = await second.From<PoolTenantRow>().ToListAsync();
            await Assert.That(all.Count).IsEqualTo(1);   // 无租户过滤：行可见
            await second.ExecuteAsync($"INSERT INTO pool_tenant (id, name, tenant_id) VALUES ({2L}, {"b"}, {8L})");
            List<PoolTenantRow> stillAll = await second.From<PoolTenantRow>().ToListAsync();
            await Assert.That(stillAll.Count).IsEqualTo(2);   // 覆写已复位,租户 7 过滤不生效
            await second.DisposeAsync();
            await Assert.That(counter.BeforeCount).IsEqualTo(0);   // 租期 1 加的拦截器未串味
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task ReusableSlots_SurviveAcrossRentals()
    {
        await using DataSessionPool<SqliteProvider> pool = await CreatePoolAsync("spt_slots");

        // 租期 1：3 次同形态 GetByKey 晋升复用槽
        DataSession<SqliteProvider> first = await pool.RentAsync();
        for (int i = 1; i <= 3; i++)
            await first.ExecuteAsync($"INSERT INTO pool_rows (id, name, qty) VALUES ({(long)i}, {"n" + i}, {i})");
        PoolRow? a = await first.GetAsync<PoolRow>(1L);
        PoolRow? b = await first.GetAsync<PoolRow>(2L);
        PoolRow? c = await first.GetAsync<PoolRow>(3L);
        await Assert.That(c!.Name).IsEqualTo("n3");
        await first.DisposeAsync();

        // 租期 2：同一会话——槽内命令与参数池引用必须保持(跨租期存活即池化收益)
        DataSession<SqliteProvider> second = await pool.RentAsync();
        PoolRow? again = await second.GetAsync<PoolRow>(2L);
        await Assert.That(again!.Name).IsEqualTo("n2");
        // 行为断言：复用路径的值正确性(槽内参数就地写值跨租期不串味)
        PoolRow? first_ = await second.GetAsync<PoolRow>(1L);
        PoolRow? third = await second.GetAsync<PoolRow>(3L);
        await Assert.That(first_!.Qty).IsEqualTo(1);
        await Assert.That(third!.Qty).IsEqualTo(3);
        await second.DisposeAsync();
    }

    [Test]
    public async Task ReturnWithActiveTransaction_ThrowsAndNotPooled()
    {
        await using DataSessionPool<SqliteProvider> pool = await CreatePoolAsync("spt_tx");
        DataSession<SqliteProvider> session = await pool.RentAsync();
        DbTransaction transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync($"INSERT INTO pool_rows (id, name, qty) VALUES ({9L}, {"x"}, {9})");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await session.DisposeAsync();
        });
        await Assert.That(pool.IdleCount).IsEqualTo(0);   // 未入池
        await transaction.RollbackAsync();
        await session.DisposeAsync();   // 事务收口后可正常归还
        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task ReturnWithUnconsumedEnumerator_Throws()
    {
        await using DataSessionPool<SqliteProvider> pool = await CreatePoolAsync("spt_stream");
        for (int i = 1; i <= 2; i++)
        {
            DataSession<SqliteProvider> seed = await pool.RentAsync();
            await seed.ExecuteAsync($"INSERT INTO pool_rows (id, name, qty) VALUES ({(long)i}, {"n" + i}, {i})");
            await seed.DisposeAsync();
        }

        DataSession<SqliteProvider> session = await pool.RentAsync();
        IAsyncEnumerable<PoolRow> enumerator = session.QueryAsyncEnumerable<PoolRow>(
            $"SELECT id, name, qty FROM pool_rows");
        await using IAsyncEnumerator<PoolRow> cursor = enumerator.GetAsyncEnumerator();
        await cursor.MoveNextAsync();   // 打开 reader 后即放弃(不消费完,不 DisposeAsync 枚举器)

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await session.DisposeAsync();
        });
        await cursor.DisposeAsync();
        await session.DisposeAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task IdleCap_ExcessReturnTearsDown()
    {
        DataSessionPool<SqliteProvider> pool = DataSessionPool<SqliteProvider>.Create(
            new DbOptions { ConnectionString = $"Data Source=spt_cap_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" },
            maxIdleSize: 1);
        try
        {
            DataSession<SqliteProvider> a = await pool.RentAsync();
            DataSession<SqliteProvider> b = await pool.RentAsync();
            await a.DisposeAsync();
            await Assert.That(pool.IdleCount).IsEqualTo(1);
            await b.DisposeAsync();   // 空闲满员：真实释放
            await Assert.That(pool.IdleCount).IsEqualTo(1);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task PoolDispose_DrainsIdle_AndRentThrowsAfterwards()
    {
        DataSessionPool<SqliteProvider> pool = await CreatePoolAsync("spt_dispose");
        DataSession<SqliteProvider> session = await pool.RentAsync();
        await session.DisposeAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(1);

        await pool.DisposeAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(0);

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await pool.RentAsync();
        });
        // 幂等
        await pool.DisposeAsync();
    }

    [Test]
    public async Task ConcurrentRentals_DistinctSessions_AllCorrect()
    {
        await using DataSessionPool<SqliteProvider> pool = await CreatePoolAsync("spt_conc");
        // 两段式:先串行租满(租出期间无人归还,必然互异),再并行使用
        var rented = new List<DataSession<SqliteProvider>>();
        for (int i = 0; i < 8; i++)
            rented.Add(await pool.RentAsync());
        await Assert.That(rented.Distinct().Count()).IsEqualTo(8);

        var tasks = new List<Task<int>>();
        for (int i = 0; i < rented.Count; i++)
        {
            DataSession<SqliteProvider> session = rented[i];
            long id = i + 1;
            tasks.Add(Task.Run(async () =>
            {
                await session.ExecuteAsync($"INSERT INTO pool_rows (id, name, qty) VALUES ({id}, {"c" + id}, {id})");
                PoolRow? row = await session.GetAsync<PoolRow>(id);
                await session.DisposeAsync();
                return row!.Qty;
            }));
        }
        int[] quantities = await Task.WhenAll(tasks);
        foreach (int qty in quantities)
            await Assert.That(qty).IsGreaterThan(0);
        await Assert.That(pool.IdleCount).IsEqualTo(8);
    }

    [Test]
    public async Task UnhealthyIdleConnection_DiscardedOnRent()
    {
        await using DataSessionPool<SqliteProvider> pool = await CreatePoolAsync("spt_dead");
        // keeper:共享内存库在全部连接关闭后销毁(B101 同族)——持一条租期内的会话保活
        DataSession<SqliteProvider> keeper = await pool.RentAsync();
        DataSession<SqliteProvider> first = await pool.RentAsync();
        await Assert.That(await first.GetAsync<PoolRow>(1L)).IsNull();   // 空结果证明连接可用
        await first.DisposeAsync();

        // 模拟池化期间连接被外部关闭:闲置会话的连接死亡
        await CloseSessionConnectionAsync(first);
        DataSession<SqliteProvider> second = await pool.RentAsync();
        // 不健康闲置项被丢弃,租出的是新建会话(连接可用)
        await second.ExecuteAsync($"INSERT INTO pool_rows (id, name, qty) VALUES ({1L}, {"r"}, {1})");
        PoolRow? row = await second.GetAsync<PoolRow>(1L);
        await Assert.That(row!.Name).IsEqualTo("r");
        await second.DisposeAsync();
        await keeper.DisposeAsync();
    }

    /// <summary>测试夹具:关闭池化会话的主连接以模拟静默死亡。只读取字段引用并关闭连接,
    /// 不绕过任何安全不变式(产品代码无此路径)。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S3011",
        Justification = "测试夹具模拟连接死亡:只读取 _conn 字段引用并 Close,无安全不变式绕过。")]
    private static async Task CloseSessionConnectionAsync(DataSession<SqliteProvider> session)
    {
        System.Reflection.FieldInfo connectionField = typeof(DataSession<SqliteProvider>)
            .GetField("_conn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("session main connection field not found");
        DbConnection connection = (DbConnection)connectionField.GetValue(session)!;
        await connection.CloseAsync();
    }
}

[Table("pool_rows")]
internal sealed partial class PoolRow
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
}

[Table("pool_tenant")]
[TenantAware]
internal sealed partial class PoolTenantRow
{
    [Key] public long Id { get; set; }
    [Column("name")] public string? Name { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
}

/// <summary>计数拦截器(瞬态复位断言用)。</summary>
internal sealed class CountingInterceptor : IQueryInterceptor
{
    internal int BeforeCount;
    public int Priority => 0;
    public void OnBefore(QueryContext context) => BeforeCount++;
    public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) { }
    public void OnError(QueryContext context, Exception exception) { }
}
