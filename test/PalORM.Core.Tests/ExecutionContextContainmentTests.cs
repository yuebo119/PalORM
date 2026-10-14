using System.Diagnostics;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>G2 证伪回退（2026-10-14 并发塌方定位轮）的行为锁定测试——
/// <b>AsyncLocal 写的 EC 包含防线</b>。
/// <para><b>背景缺陷</b>：非 async 入口直接调用 EnterReadOnly 时，Enter 内的
/// <c>_currentOperationOwner.Value = this</c>（AsyncLocal 写）落在调用方活 ExecutionContext 上；
/// async 方法体内的写会被 async method builder 在同步完成路径还原（step16-P1 既有知识），
/// 非 async 入口没有这层包含。每操作新会话 = 每次写入一个<b>新的 AsyncLocal 实例键</b>
///（SessionOperationState 每会话持有独立 AsyncLocal 字段）→ 调用方 EC map 随操作数线性增长 →
/// AsyncLocal.Value setter 的 O(N) map 拷贝 → 长循环 O(N²)。PerfHub 并发臂（饱和循环）实测
/// P50 6.6→133.7µs（20×），短迭代批项不可见——只有饱和循环暴露。</para>
/// <para><b>检测器形态（二次型签名）</b>：同一异步流里连续跑 N 个每操作新会话，比较前段与
/// 后段的每 op 耗时——污染形态后段远慢于前段（map 随 N 增长），健康形态两段持平。比值断言
/// 机器无关（B100 判据稳定度：比例型 > 绝对耗时）；3× 阈值给 JIT/GC 噪声留双向余量
///（JIT 使前段偏慢、污染使后段偏慢，两者都把比值压向 1，只有真二次型会越线）。</para></summary>
public sealed class ExecutionContextContainmentTests
{
    // 量级依据：突变探针实测 800 op 的时延二次项未越过 3× 阈值；污染的显性可测面是
    // <b>分配</b>——EC map 每次写入都分配新 map 节点，后段 B/op 随 map 增长显著抬升
    //（G 形态实测：长会话臂 B/op 从基线抬升 ~26%，分配地板测试连带红）。分配基检测器
    // 确定性且机器无关（B100：比例型 > 绝对值），时延形状仅作旁证。
    private const int TotalOps = 6000;
    private const int HalfOps = TotalOps / 2;

    private static async Task<(double FirstHalfBPerOp, double SecondHalfBPerOp)> MeasurePerOpSessionsAsync(
        Func<DataSession<SqliteProvider>, long, CancellationToken, Task> operation)
    {
        string cs = $"Data Source=:memory:";
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE ec_probe (id INTEGER PRIMARY KEY, v TEXT NOT NULL)";
            await setup.ExecuteNonQueryAsync();
        }
        var opts = new DbOptions { ConnectionString = cs };

        // 在裸 Task.Run worker 内跑饱和循环（塌方实测的暴露形态：非 async 入口的
        // AsyncLocal 写逃逸进 worker 流动 EC，逐 op 累积）。测试框架自身的执行包装
        // 会隔离/还原 EC，污染在框架直跑的循环里不累积——必须复刻 worker 形态
        return await Task.Run(async () =>
        {
            long firstAlloc = 0, secondAlloc = 0;
            for (int i = 0; i < TotalOps; i++)
            {
                if (i == 0) GC.GetTotalAllocatedBytes();   // 触发内部计数同步（首轮忽略）
                long halfMark = i == HalfOps ? GC.GetTotalAllocatedBytes() : 0;
                long t0 = GC.GetTotalAllocatedBytes();
                // 会话即抛弃（连接归本测试持有，与 PerfHub 并发臂同形态），不 Dispose
                // 不关共享连接——正是缺陷的暴露形态（CA2000 按此形态豁免）
#pragma warning disable CA2000
                DataSession<SqliteProvider> session = new(connection, opts, [], null);
#pragma warning restore CA2000
                await operation(session, 1 + (i % 4), default);
                long delta = GC.GetTotalAllocatedBytes() - t0;
                if (i < HalfOps) firstAlloc += delta; else secondAlloc += delta;
                _ = halfMark;
            }
            return (firstAlloc / (double)HalfOps, secondAlloc / (double)HalfOps);
        }).ConfigureAwait(false);
    }

    private static async Task AssertNoQuadraticGrowthAsync(
        string operationName,
        Func<DataSession<SqliteProvider>, long, CancellationToken, Task> operation)
    {
        // 3 轮取最小比值——JIT 预热轮的前段偏重会压低比值，最小值最接近真值。
        // 诚实边界（突变探针实测）：本检测器在 G 形态下不必然直接红——污染在测试框架
        // 上下文中的累积弱于裸 worker，主要绊线是 SessionLifecycleCostTests 的分配地板
        // 测试（G 形态实测红，长会话臂 B/op 被 EC map 分配抬升）；本组提供次级的
        // 分配稳定性形态守卫（健康形态比值 ~1×，3× 阈值留足余量，无误报面）。
        double minRatio = double.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            (double first, double second) = await MeasurePerOpSessionsAsync(operation).ConfigureAwait(false);
            minRatio = Math.Min(minRatio, second / Math.Max(first, 1.0));
        }
        await Assert.That(minRatio).IsLessThan(3.0);
        _ = operationName;
    }

    [Test]
    public async Task GetAsync_PerOpSessions_NoExecutionContextMapGrowth()
        => await AssertNoQuadraticGrowthAsync("GetAsync",
            static (session, id, ct) => session.GetAsync<EcProbe>(id, ct).AsTask());

    [Test]
    public async Task GetAllAsync_PerOpSessions_NoExecutionContextMapGrowth()
        => await AssertNoQuadraticGrowthAsync("GetAllAsync",
            static (session, _, ct) => session.GetAllAsync<EcProbe>(ct).AsTask());

    [Test]
    public async Task CountAsync_PerOpSessions_NoExecutionContextMapGrowth()
        => await AssertNoQuadraticGrowthAsync("CountAsync",
            static (session, _, ct) => session.CountAsync<EcProbe>(null, ct).AsTask());
}

[Table("ec_probe")]
internal sealed partial class EcProbe
{
    [Key]
    [Column("id")]
    public long Id { get; set; }
    [Column("v")]
    public string V { get; set; } = "";
}
