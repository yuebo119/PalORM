using System.Threading;
using PalORM;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>T8/step7（R49/R50）：PG 批路径首次并发的单实例化。
/// <para><b>R49</b>：COPY 目标引用形态缓存（<c>QuotedInsertTargetCache</c>）的首次并发只构建一次
/// ——以构建计数器（<c>QuotedTargetBuildCount</c>）断言；旧 TryGetValue→TryAdd 形态会让并发
/// 首触各建一份（TryAdd 的败者也返回自己那份）。</para>
/// <para><b>为什么用专用实体 + 手建表</b>：缓存键含 (Type, Dialect)，已注册实体可能被先前用例
/// 预热过（计数器已 >0），读数失去判别力；专用实体保证本用例就是它的首次触发。手建表（不调
/// MigrateAsync）同时绕开 B63 的系统目录并发竞态面。</para></summary>
[Property("Category", "ExternalDatabase")]
public sealed class PgBulkCacheConcurrencyTests
{
    private static DbOptions PgOptions() => new()
    {
        ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString(),
    };

    [Test]
    public async Task FirstConcurrentBulkInsert_BuildsQuotedTargetOnce()
    {
        await using (var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOptions()))
        {
            // 专用实体手建表（列集合与实体的 InsertColumns 一致由 SourceGen 保证）。
            // 2026-09-28 修复：删去裸 DELETE FROM——它在 DROP IF EXISTS 之前，全新空库
            //（CI 每次新容器）且本用例先于任何 MigrateAsync 执行时撞 42P01（既有顺序
            // 依赖被 v6.0 新用例的调度变化暴露；本地绿因库有历史表）。DROP IF EXISTS
            // + CREATE 已覆盖 DELETE 的全部意图（每次全新建表）。
            // 列名与生成物一致用引号形态（B78：PG 折叠未加引号的混合大小写）。
            await db.ExecuteAsync($"DROP TABLE IF EXISTS r49_probe");
            await db.ExecuteAsync(
                $"CREATE TABLE r49_probe (\"Id\" INTEGER PRIMARY KEY, \"label\" TEXT)");
        }

        int before = PostgreSqlProvider.QuotedTargetBuildCount;
        const int threads = 8;
        using var start = new Barrier(threads);
        var tasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            int id = t + 1;  // for 循环变量被闭包共享（C#5 起仅 foreach 每迭代新实例）——拷本地
            tasks[t] = Task.Run(async () =>
            {
                // 每线程独立会话——单活动操作门禁是每会话契约（8 并发 BulkInsert 打同一会话
                // 本就该被拒）；缓存是 Provider 静态面，争用点与单会话时相同。
                await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOptions());
                start.SignalAndWait();
                await db.BulkInsertAsync([new R49ProbeEntity { Id = id, Label = "x" }]);
            });
        }
        await Task.WhenAll(tasks);
        int after = PostgreSqlProvider.QuotedTargetBuildCount;

        // 8 线程同时首触：构建次数恰为 1（GetOrAdd 桶锁内单次执行工厂）
        await Assert.That(after - before).IsEqualTo(1);
        await using (var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOptions()))
        {
            await Assert.That(await db.CountAsync<R49ProbeEntity>()).IsEqualTo(threads);
        }
    }
}

[Table("r49_probe")]
internal sealed partial class R49ProbeEntity
{
    [Key(AutoIncrement = false)] public int Id { get; set; }
    [Column("label")] public string? Label { get; set; }
}
