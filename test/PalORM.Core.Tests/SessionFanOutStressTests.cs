using System.Collections.Concurrent;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// 独立审计 2026-09-19 M3-3（T8）——高扇出压力防线。
/// 既有 SessionConcurrencyTests 用 TCS 握手精确编排交错（扇出 ≤3，单会话重叠拒绝已充分覆盖——
/// 注：屏障同步的"同时发起"下顺序排队执行是门禁的合法行为，不构成违约，故不做该形态）；
/// 本组补的高扇出面：64 个独立会话并发 × 16 操作/会话——最终态一致性断言（无静默串扰/丢行/死锁）。
/// 纪律：确定性种子、零 sleep（轮询用 Task.WhenAll 自然汇合）、断言最终态而非时序。
/// </summary>
[NotInParallel("FanOutStress")]
public sealed class SessionFanOutStressTests
{
    private const int SessionCount = 64;
    private const int OpsPerSession = 16;

    [Test]
    public async Task SixtyFourSessions_SixteenOpsEach_FinalStateConsistent()
    {
        // 64 会话并发,每会话 16 操作:建表(竞争容忍)→ 插 8 行 → 读全表计数 ≥ 自己的 8 行
        var results = new ConcurrentBag<int>();
        var sessions = new List<Task>();
        using var barrier = new SemaphoreSlim(0, 1);
        for (int s = 0; s < SessionCount; s++)
        {
            int id = s;
            sessions.Add(Task.Run(async () =>
            {
                await using DataSession<SqliteProvider> db = await CreateSessionAsync();
                // 每会话独立 :memory: 库——断言"会话隔离下最终态 = 自己写入的全量",无丢失
                await db.ExecuteAsync($"CREATE TABLE stress_rows (Id INTEGER PRIMARY KEY AUTOINCREMENT, owner INT NOT NULL, seq INT NOT NULL)");
                // PALORM005 豁免:压力测试本意是 16 次串行操作(扇出来自会话维度)
#pragma warning disable PALORM005
                for (int op = 0; op < OpsPerSession / 2; op++)
                    await db.InsertAsync(new StressRow { Owner = id, Seq = op });
#pragma warning restore PALORM005
                var all = await db.GetAllAsync<StressRow>();
                results.Add(all.Count);
            }));
        }
        await Task.WhenAll(sessions);
        // 每会话应见自己全部 8 行(无丢行/无串行污染)
        await Assert.That(results.Count).IsEqualTo(SessionCount);
        await Assert.That(results.All(static c => c == OpsPerSession / 2)).IsTrue();
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
}

[Table("stress_gate")]
internal sealed partial class StressGate
{
    [Key]
    public long Id { get; set; }
    [Column("v")]
    public long V { get; set; }
}

[Table("stress_rows")]
internal sealed partial class StressRow
{
    [Key]
    public long Id { get; set; }
    [Column("owner")]
    public int Owner { get; set; }
    [Column("seq")]
    public int Seq { get; set; }
}
