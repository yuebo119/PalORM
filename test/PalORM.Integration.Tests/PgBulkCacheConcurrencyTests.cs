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
/// MigrateAsync）同时绕开 B63 的系统目录并发竞态面。</para>
/// <para>2026-10-03：手建表同样与并行建表用例争系统目录（实测 42710 type already exists），
/// 与 <c>ExtBulkTable</c> 组串行。</para></summary>
[NotInParallel("ExtBulkTable")]
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
            // 2026-09-28 二次修复：建表改走 MigrateAsync——此前手建表（裸 CREATE TABLE）
            // 与并行用例的全表迁移建同名表（r49_probe 是注册实体）撞 pg_type 并发竞态
            //（CI 实测 23505，同提交 dev 绿 main 红坐实概率性）。产品侧 ApplyTableDdlAsync
            // 已带竞态兜底（回退逐条），复用该路径后本用例零裸 DDL。原注释"绕开 B63 竞态面"
            // 的顾虑自产品兜底落地起消除。列名引号形态由 SourceGen 的方言 DDL 保证（B78）。
            await db.MigrateAsync();
            await db.ExecuteAsync($"DELETE FROM r49_probe");
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
