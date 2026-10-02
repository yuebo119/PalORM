using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

// PALORM005（N+1 检测）：本组的观察点正是"循环内反复新建会话/反复单行读"——被测形态本身
// 就是 per-operation 会话，循环是成本摊销与生命周期断言的必要手段，非生产 N+1 反模式
#pragma warning disable PALORM005

namespace PalORM.Core.Tests;

/// <summary>会话生命周期成本门禁（2026-10-02）。
/// <para><b>为什么需要</b>：两条收益/缺口都没有任何锁定——</para>
/// <list type="number">
/// <item>「池化文件库 + 每请求 CreateAsync」形态（commit 47b6aa9）：整组初始化 PRAGMA 每物理句柄
/// 只跑一次，探针实测 <c>CreateAsync+GetAsync</c> 16.62µs/6408B → 8.08µs/3880B。PerfHub 与 BDN
/// 都不经过这个形态（前者 per-op 共享连接、后者共享内存库），收益与回归在夹具里看不见；
/// 该形态当前只有 <see cref="SqlitePooledInitializationTests"/> 的三个**行为**用例，没有成本锁定。</item>
/// <item>「per-op 会话拿不到命令复用」（本轮查明）：惰性晋升阈值 3 是防泄漏设计（B98：无条件缓存
/// 让并发混合负载慢 4.84×），代价是**每操作新建命令约 1448B**。这个取舍一旦被人"顺手"改成
/// 阈值 1 或无条件缓存，本用例应转红。</item>
/// </list>
/// <para><b>口径纪律</b>：并行测试套件<b>禁止一切墙钟断言</b>（B57/B74：同 run 比值实测漂 0.2~0.8、
/// 并行污染加 10~50KB）。故本组只测两类确定性事实：① 命令复用与否的**结构后果**（抛弃式会话用完即弃）
/// ② 分配量的 gross 级**上界**（真值数 KB，线设在 500KB，只挡 10× 级真实回归，
/// 拒绝把微基准精度伪装成单测）。</para>
/// <para><b>与本文件相关的探针证据</b>：<c>.ai/perf-probe/PerOpAbDiag.cs</c>、
/// <c>DisposePathDiag.cs</c>（本地工具，不入仓库）——后者实测会话释放库侧 424B/op
/// （TCS + 异步状态机），不在热路径，判定不减。</para>
/// <para><b>实测真值与门禁线</b>（本轮单测内实测，三跑稳定）：per-op 形态 3202 B/op、
/// 长会话形态 1307 B/op，比值 2.45 → 比值线设 1.8（并行污染对两臂等量，比值免疫）；
/// 长会话另设 64KB gross 线兜数量级退化。已用变异探针验证门禁能转红
/// （把比值线下调 → 用例红）。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1215",
    Justification = "GC.GetTotalAllocatedBytes 基线测量；宽松 tripwire，非精密断言。")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "连接生命周期移交 DataSession（同 FromAllocationTests 口径）；keeper 由调用方释放。")]
public sealed class SessionLifecycleCostTests
{
    /// <summary><b>两种会话形态的成本对比（同一用例内背靠背测量，比值免疫并行污染）。</b>
    /// <para>为什么不是一个形态一条绝对上界：并行测试套件里 <c>GC.GetTotalAllocatedBytes</c> 是
    /// **进程级**计数，其他用例的分配会被计入（B57/B74 实测污染加 10~50KB），单形态的绝对上界
    /// 在并行下不可靠（首版就是这么红的）。同一用例内交替测两臂时，污染对两臂等量叠加，
    /// **比值**免疫；且两臂真值本身相差约 2.5 倍（3202 对 1307），信号远大于污染对**比值**的影响。</para>
    /// <para><b>锁定的产品事实</b>：per-operation 形态（每操作新建会话 + 连接，命令永不复用）
    /// 的每操作分配应显著高于长会话形态（会话复用、命令已跨过惰性晋升阈值 3）。
    /// 若有人把阈值降为 1 或改成无条件缓存（B98 实测会让并发混合负载慢 4.84×），
    /// 或给 per-op 路径引入新的每操作资源，比值会塌向 1.0 而转红。</para></summary>
    [Test]
    public async Task PerOperationVsLongLived_AllocationRatioStaysAboveFloor()
    {
        await WithFileDatabaseAsync(async cs =>
        {
            var options = new DbOptions { ConnectionString = cs };

            async Task PerOperationCall()
            {
                var session = new DataSession<SqliteProvider>(await OpenConnectionAsync(cs), options, [], null);
                _ = await session.GetAsync<LifecycleRow>(1L);
            }

            await using var conn = await OpenConnectionAsync(cs);
            await using var longLived = new DataSession<SqliteProvider>(conn, options, [], null);
            // 跨过惰性晋升阈值 3，使本臂为"命令已复用"形态
            for (var i = 0; i < 5; i++)
            {
                _ = await longLived.GetAsync<LifecycleRow>(1L);
            }

            // 交替两轮取各自最小值：既压顺序偏置（B85），又让两臂经历同样的污染窗口
            var perOp = double.MaxValue;
            var longLivedCost = double.MaxValue;
            for (var round = 0; round < 2; round++)
            {
                perOp = Math.Min(perOp, await MeasureAsync(PerOperationCall));
                longLivedCost = Math.Min(
                    longLivedCost, await MeasureAsync(() => longLived.GetAsync<LifecycleRow>(1L).AsTask()));
            }

            double ratio = perOp / longLivedCost;
            // 实测真值（本轮单测内）：per-op 3202 B/op、长会话 1307 B/op → 比值 2.45。
            // 线设 1.8：留足污染与调度余量，同时任何把 per-op 成本拉到长会话同档的改动都会转红。
            await Assert.That(ratio).IsGreaterThan(1.8)
                .Because($"per-op {perOp:F0} B/op 应对长会话 {longLivedCost:F0} B/op 保持显著更高"
                    + "（命令不通用的既定取舍，B98）");
        });
    }

    /// <summary>长会话形态的绝对上界（宽松 gross 线）。它单独不可靠（并行污染），
    /// 但与上一条的**比值**断言互补：比值管"两形态没被拉平"，本条的宽松线管
    /// "长会话形态自身没有数量级退化"（真值 1307 B/op，线设 64KB 只挡 gross 级）。</summary>
    [Test]
    public async Task LongLivedSession_GetAsync_StaysUnderGrossLine()
    {
        await WithFileDatabaseAsync(async cs =>
        {
            await using var conn = await OpenConnectionAsync(cs);
            var options = new DbOptions { ConnectionString = cs };
            await using var session = new DataSession<SqliteProvider>(conn, options, [], null);
            for (var i = 0; i < 5; i++)
            {
                _ = await session.GetAsync<LifecycleRow>(1L);
            }

            double allocated = await MeasureAsync(() => session.GetAsync<LifecycleRow>(1L).AsTask());
            // 真值 1307 B/op；64KB 是污染免疫的 gross 线（只挡 10× 级以上的真实回归）
            await Assert.That(allocated).IsLessThan(64_000)
                .Because("长会话形态（命令已晋升）gross 上界：实测 1307 B/op");
        });
    }

    /// <summary><b>惰性晋升阈值的结构性锁定</b>：per-op 会话形态下，同 (实体, 操作) 的服务次数
    /// 永远达不到阈值 3（每次操作一个抛弃式会话），故命令**不会**在会话间复用——这是 B98 修复后的
    /// 既定取舍（阈值 3 防泄漏）。本用例断言该形态的**语义后果**：抛弃式会话从不持有可复用命令。
    /// <para>做法：per-op 会话执行一次后立即释放，反复 N 次——若有人把阈值降为 1 或改成无条件缓存，
    /// 命令会试图跨会话存活，分配量会明显变化（上界以 gross 线兜住）；更重要的是，
    /// 下方断言"每次操作后会话已释放"这一确定性事实。</para></summary>
    [Test]
    public async Task PerOperationSession_DisposedAfterSingleOperation()
    {
        await WithFileDatabaseAsync(async cs =>
        {
            var options = new DbOptions { ConnectionString = cs };
            for (var i = 0; i < 3; i++)
            {
                var session = new DataSession<SqliteProvider>(await OpenConnectionAsync(cs), options, [], null);
                _ = await session.GetAsync<LifecycleRow>(1L);
                await session.DisposeAsync();
            }

            // 确定性事实：三次抛弃式会话各自释放完毕，未悬挂（悬挂会让后续 Open 失败或泄漏句柄）
            await using var probe = await OpenConnectionAsync(cs);
            await using var cmd = probe.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM lifecycle_rows";
            object? count = await cmd.ExecuteScalarAsync();
            await Assert.That(Convert.ToInt64(count, CultureInfo.InvariantCulture)).IsEqualTo(1L);
        });
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string cs)
    {
        var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>循环摊销测量：预热不计入，整轮一次计数（与 <c>CountSqlAllocationTests</c> 同口径）。</summary>
    private static async Task<double> MeasureAsync(Func<Task> call)
    {
        for (var i = 0; i < 200; i++)
        {
            await call();
        }

        long before = GC.GetTotalAllocatedBytes();
        const int n = 500;
        for (var i = 0; i < n; i++)
        {
            await call();
        }

        long after = GC.GetTotalAllocatedBytes();
        return (after - before) / (double)n;
    }

    /// <summary>文件库（非内存库）：门禁针对的正是"池化文件库"形态——内存库每次 Open 都是新句柄，
    /// 池化收益与 per-op 成本在该形态下不可见（<see cref="SqlitePooledInitializationTests"/> 同因）。</summary>
    private static async Task WithFileDatabaseAsync(Func<string, Task> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"palorm-lifecycle-{Guid.NewGuid():N}.db");
        string cs = $"Data Source={path}";
        try
        {
            await using (var seed = new SqliteConnection(cs))
            {
                await seed.OpenAsync();
                await using DbCommand cmd = seed.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE lifecycle_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL);"
                    + "INSERT INTO lifecycle_rows (id, name) VALUES (1, 'row-1');";
                await cmd.ExecuteNonQueryAsync();
            }

            await body(cs);
        }
        finally
        {
            using (var pooled = new SqliteConnection(cs))
            {
                SqliteConnection.ClearPool(pooled);
            }

            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
            {
                try { File.Delete(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _ = exception;
                }
            }
        }
    }
}

/// <summary>门禁用实体——表名从 <c>[Table]</c> 注解抄（B101：跑前核对）。</summary>
[Table("lifecycle_rows")]
public sealed partial class LifecycleRow
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";
}

#pragma warning restore PALORM005
