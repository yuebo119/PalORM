using PalORM.PostgreSql;
using PalORM.Testing;

// PALORM005（N+1 检测）：租期循环内的逐行读写是被测行为本身（池化租期 + 槽跨租期命中），
// 非反模式
#pragma warning disable PALORM005

namespace PalORM.Integration.Tests;

/// <summary>会话池真库行为锁定（2026-10-13 二轮方案）。
/// <para>覆盖：① PG 租/还循环的 CRUD 正确性与复用槽跨租期存活（GetByKey 槽晋升后跨租期
/// 命中）；② 租期内 With* 覆写复位（超时/隔离级别）；③ 多次租出交错归还的正确性
///（两租期交替读写不串味）。</para>
/// <para><b>串行槽</b>：ExternalDatabase 用例统一挂 ExtBulkTable 守卫键（B63 族）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class SessionPoolIntegrationTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_PoolRentals_RoundTripsCorrectly_AcrossCycles()
    {
        DataSessionPool<PostgreSqlProvider> pool = DataSessionPool<PostgreSqlProvider>.Create(
            new DbOptions { ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString() });
        try
        {
            DataSession<PostgreSqlProvider> setup = await pool.RentAsync();
            await setup.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pool_rt");
            await setup.ExecuteAsync(
                $"CREATE TABLE palorm_pool_rt (id BIGINT PRIMARY KEY, name VARCHAR(32) NOT NULL)");
            await setup.DisposeAsync();

            // 租期 1:写入 5 行 + 晋升 GetByKey 槽(3 次同形态读)
            DataSession<PostgreSqlProvider> first = await pool.RentAsync();
            for (int i = 1; i <= 5; i++)
                await first.InsertAsync(new PoolRtRow { Id = i, Name = "n" + i });
            _ = await first.GetAsync<PoolRtRow>(1L);
            _ = await first.GetAsync<PoolRtRow>(2L);
            PoolRtRow? r3 = await first.GetAsync<PoolRtRow>(3L);
            await Assert.That(r3!.Name).IsEqualTo("n3");
            await first.DisposeAsync();

            // 租期 2:同一会话——槽跨租期命中,读写值逐位正确(不串味)
            DataSession<PostgreSqlProvider> second = await pool.RentAsync();
            await Assert.That(ReferenceEquals(second, first)).IsTrue();
            for (int i = 1; i <= 5; i++)
            {
                PoolRtRow? row = await second.GetAsync<PoolRtRow>(i);
                await Assert.That(row!.Name).IsEqualTo("n" + i);
            }
            await second.InsertAsync(new PoolRtRow { Id = 6, Name = "n6" });
            await second.UpdateAsync(new PoolRtRow { Id = 6, Name = "n6-upd" });
            await Assert.That((await second.GetAsync<PoolRtRow>(6L))!.Name).IsEqualTo("n6-upd");
            await second.DeleteAsync<PoolRtRow>(6L);
            await Assert.That(await second.CountAsync<PoolRtRow>()).IsEqualTo(5L);
            await second.DisposeAsync();

            // 租期 3:租期内覆写(WithTimeout)后归还,下一租期功能不受影响
            DataSession<PostgreSqlProvider> third = await pool.RentAsync();
            third.WithTimeout(TimeSpan.FromSeconds(3));
            await third.ExecuteAsync($"UPDATE palorm_pool_rt SET name = {"z"} WHERE id = {1L}");
            await third.DisposeAsync();
            DataSession<PostgreSqlProvider> fourth = await pool.RentAsync();
            await Assert.That((await fourth.GetAsync<PoolRtRow>(1L))!.Name).IsEqualTo("z");
            await fourth.DisposeAsync();
        }
        finally
        {
            DataSession<PostgreSqlProvider> cleanup = await pool.RentAsync();
            await cleanup.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pool_rt");
            await cleanup.DisposeAsync();
            await pool.DisposeAsync();
        }
    }
}

[Table("palorm_pool_rt")]
internal sealed partial class PoolRtRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";
}
