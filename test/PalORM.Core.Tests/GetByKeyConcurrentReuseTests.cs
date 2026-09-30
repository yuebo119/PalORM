using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>T3 池化并发正确性（step8）：GetByKey 复用槽（PL-2 扩展）在并发读下的串扰面。
/// GetAsync 走 EnterOperation 读租约，ForParallelReads 作用域内读并发合法——两个并发
/// GetAsync 晋升后命中同一 _reusableGetByKey 命令对象，DbCommand 非线程安全，
/// 同命令并发 ExecuteReader 是未定义行为。本测试按"修复前红"纪律先实证。
/// （单行写路径 INSERT/UPDATE 槽不受此影响：写与事务串行不变式保证写路径独占主连接。）
/// PALORM005：本组观察点正是"同一会话并发单行读"——被测行为本身而非反模式。</summary>
#pragma warning disable PALORM005
[NotInParallel("GetByKeyConcurrentReuse")]
public sealed class GetByKeyConcurrentReuseTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSeededAsync()
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            // 命名内存库 + Shared 缓存（对齐 ParallelReadLeaseTests 的工作形态）：
            // 并行读池连接用同一连接串新建，裸 :memory: 每连接独立空库（读池连接会
            // 报 no such table）；Mode=Memory 命名源下池连接与会话主连接同库。
            // keeper 语义成立：会话释放先清并行池（ITM-797③）后关主连接。
            ConnectionString = $"Data Source=gkcr_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
        }).ConfigureAwait(false);
        await session.ExecuteAsync(
            $"CREATE TABLE gkcr_items (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)").ConfigureAwait(false);
        // 播种 8 行：并发读各自按 Id 命中预期行，串扰表现为拿错行或驱动异常
        for (int i = 1; i <= 8; i++)
            await session.InsertAsync(new GkcrItem { Id = i, Name = $"row-{i}" }).ConfigureAwait(false);
        // 越过晋升阈值（3）：确保复用槽已建立
        for (int i = 1; i <= 8; i++)
            _ = await session.GetAsync<GkcrItem>(i).ConfigureAwait(false);
        return session;
    }

    // 作用域外的读读并发被门禁串行化是 ARCH-001 既有设计（并发读只在 ForParallelReads
    // 作用域内合法），其拒绝语义由 SessionOperationState 既有测试覆盖——不在此重复构造
    // 时序竞争窗口（T24：时间窗竞态的测试必须在窗内真触发，竞争不可控的测试是假防线）。

    [Test]
    public async Task ConcurrentGetByKey_InsideParallelReadScope_NoCrossContamination()
    {
        // step8 连接治理：作用域内 GetAsync 走读连接池（每尝试独立获取/归还，弹性重试
        // 重新获取），8 路并发读各自命中预期行——复用槽仅服务作用域外主连接形态，
        // 读池路径无共享命令对象，无串扰面
        await using var session = await CreateSeededAsync().ConfigureAwait(false);

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using (session.ForParallelReads())
        {
            var tasks = new List<Task>(8);
            for (int worker = 1; worker <= 8; worker++)
            {
                long id = worker;
                tasks.Add(Task.Run(async () =>
                {
                    for (int round = 0; round < 25; round++)
                    {
                        GkcrItem? row = await session.GetAsync<GkcrItem>(id).ConfigureAwait(false);
                        if (row is null)
                            failures.Add($"scope id={id} round={round}: 未命中");
                        else if (row.Id != id)
                            failures.Add($"scope id={id} round={round}: 拿回 Id={row.Id}（串扰实锤）");
                    }
                }));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        await Assert.That(failures.IsEmpty).IsTrue();
    }

    [Test]
    public async Task SequentialGetByKey_AfterParallelScope_RestoresReuseAndCorrectness()
    {
        // 作用域退出后 GetAsync 回到主连接复用形态：结果正确且晋升槽继续服务
        await using var session = await CreateSeededAsync().ConfigureAwait(false);

        await using (session.ForParallelReads())
        {
            GkcrItem? inside = await session.GetAsync<GkcrItem>(1).ConfigureAwait(false);
            await Assert.That(inside!.Id).IsEqualTo(1);
        }

        for (int round = 1; round <= 4; round++)
        {
            GkcrItem? row = await session.GetAsync<GkcrItem>(round).ConfigureAwait(false);
            await Assert.That(row).IsNotNull();
            await Assert.That(row!.Id).IsEqualTo(round);
        }
    }
}

[Table("gkcr_items")]
internal sealed partial class GkcrItem
{
    [Key] [Column("Id")] public long Id { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
}
