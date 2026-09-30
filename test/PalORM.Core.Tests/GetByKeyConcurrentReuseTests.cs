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
            ConnectionString = "Data Source=:memory:"
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
    public async Task ConcurrentGetByKey_InsideParallelReadScope_LoudlyRejected()
    {
        // step8-T3 实测链：作用域内并发 GetAsync 修 EnterReadOnly 放行后暴露双缺陷——
        // ① 复用槽同命令并发（SQLite "Must add values" 参数串扰，已禁用复用槽）
        // ② 直查族命令走主连接不走读池，并发挤同一物理连接（SqliteConnection.Close NRE）
        // ②的根治 = 读连接池扩覆盖（专门迭代），当前以响亮失败拒绝并指引用户改用
        // From<T>() 族（读池支撑）——响亮失败优于 NRE（项目哲学：静默兜底即假防线）
        await using var session = await CreateSeededAsync().ConfigureAwait(false);

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using (session.ForParallelReads())
        {
            var tasks = new List<Task>(8);
            for (int worker = 1; worker <= 8; worker++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await session.GetAsync<GkcrItem>(1).ConfigureAwait(false);
                        failures.Add("作用域内 GetAsync 未被拒绝（应响亮失败）");
                    }
                    catch (InvalidOperationException ex)
                    {
                        if (!ex.Message.Contains("ForParallelReads", StringComparison.Ordinal))
                            failures.Add($"异常形态不符: {ex.Message}");
                    }
                }));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        await Assert.That(failures.IsEmpty).IsTrue();
    }
}

[Table("gkcr_items")]
internal sealed partial class GkcrItem
{
    [Key] [Column("Id")] public long Id { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
}
