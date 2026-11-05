using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace ReadManyProbe;

/// <summary>多键点读 A/B 探针（2026-11-05 性能方案 PERF-1.3/1.4）。
/// <para><b>要回答的问题</b>：新 API <c>GetByKeyManyAsync</c>（SQLite <c>json_each</c> 单参数）
/// 相对现行推荐形态（单会话内逐键 <c>GetAsync</c>，PL-2 复用槽生效）在同一数据、同一物化
/// 路径上到底快多少。</para>
/// <para><b>两臂</b>：
/// <list type="number">
/// <item><c>PerKey-Reuse</c>：单会话循环 <c>GetAsync</c>——PL-2 晋升后的稳态形态（现行最优读写法）；</item>
/// <item><c>Many-JsonEach</c>：单次 <c>GetByKeyManyAsync</c>——本方案新增形态。</item>
/// </list>
/// 两臂都经 PalORM 源生成 RowFactory 物化到同一实体类，物化路径逐位相同，差值即命令/往返侧开销。</para>
/// <para><b>口径</b>：同轮交替 A/B，≥3 批，取各批中位数的中位；双指标（时延 + 分配）。
/// 数据源为临时 SQLite 库，不依赖容器。</para></summary>
internal static class Program
{
    private const int Warmup = 2;
    private const int Timed = 5;
    private const int Batches = 3;
    private static readonly int[] Scales = [100, 1000, 10000];

    private static async Task<int> Main()
    {
        string db = Path.Combine(Path.GetTempPath(), $"readmany-probe-{Environment.ProcessId}.db");
        Delete(db);
        string cs = $"Data Source={db}";

        await using (DataSession<SqliteProvider> setup = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = cs }))
        {
            await setup.ExecuteAsync($"PRAGMA journal_mode=WAL");
            await setup.ExecuteAsync(
                $"CREATE TABLE read_many_probe (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Qty INTEGER NOT NULL)");
        }

        Console.WriteLine();
        Console.WriteLine($"批次={Batches}，每批 预热{Warmup}+计时{Timed}（中位）；同轮交替 A/B；物化路径两臂相同");
        Console.WriteLine("| 档 | 形态 | 中位 ms | µs/键 | 分配 B/键 |");
        Console.WriteLine("|---|---|---:|---:|---:|");

        foreach (int scale in Scales)
        {
            await SeedAsync(cs, scale);

            var results = new List<(string Arm, double Ms, double Alloc)>();
            for (int batch = 0; batch < Batches; batch++)
            {
                // 同轮交替：每批先 A 后 B（顺序固定，两臂都在同批环境态下测量）
                results.Add(await MeasureAsync(cs, "PerKey-Reuse", scale));
                results.Add(await MeasureAsync(cs, "Many-JsonEach", scale));
            }

            foreach (string arm in new[] { "PerKey-Reuse", "Many-JsonEach" })
            {
                double ms = Median(results.Where(r => r.Arm == arm).Select(r => r.Ms).ToList());
                double alloc = Median(results.Where(r => r.Arm == arm).Select(r => r.Alloc).ToList());
                Console.WriteLine($"| {scale,6:N0} | {arm} | {ms:F3} | {ms * 1000.0 / scale:F2} | {alloc / scale:F0} |");
            }

            double ratio = Median(results.Where(r => r.Arm == "Many-JsonEach").Select(r => r.Ms).ToList())
                / Median(results.Where(r => r.Arm == "PerKey-Reuse").Select(r => r.Ms).ToList());
            Console.WriteLine($"| {scale,6:N0} | **Many/PerKey 时延比** | {ratio:F2}x | — | — |");
        }

        SqliteConnection.ClearAllPools();
        Delete(db);
        return 0;
    }

    private static async Task SeedAsync(string cs, int scale)
    {
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = cs });
        await session.ExecuteAsync($"DELETE FROM read_many_probe");
        // 递归 CTE 单语句播种：20000 行不经逐行往返
        await session.ExecuteAsync(
            $"WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < {scale}) INSERT INTO read_many_probe (Id, Name, Qty) SELECT i, 'name-' || i, i % 100 FROM seq");
    }

    /// <summary>测一形态：预热 Warmup 轮，计时 Timed 轮取中位。两臂同构（每轮重建结果列表）。</summary>
    private static async Task<(string Arm, double Ms, double Alloc)> MeasureAsync(
        string cs, string arm, int scale)
    {
        var samples = new List<(double Ms, double Alloc)>(Timed);
        long observed = 0;
        for (int round = 0; round < Warmup + Timed; round++)
        {
            long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            List<ReadManyProbeRow> rows = arm == "PerKey-Reuse"
                ? await ReadPerKeyAsync(cs, scale)
                : await ReadManyAsync(cs, scale);
            sw.Stop();
            long alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
            observed = rows.Count;
            if (round >= Warmup) samples.Add((sw.Elapsed.TotalMilliseconds, alloc));
        }

        if (observed != scale)
        {
            throw new InvalidOperationException($"{arm}/{scale} 行数守卫失败：读到 {observed}");
        }
        return (arm, Median(samples.Select(s => s.Ms).ToList()), Median(samples.Select(s => s.Alloc).ToList()));
    }

    /// <summary>A 臂：单会话内逐键直查（PL-2 复用槽在第 3 次操作后生效）。</summary>
    private static async Task<List<ReadManyProbeRow>> ReadPerKeyAsync(string cs, int scale)
    {
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = cs });
        var rows = new List<ReadManyProbeRow>(scale);
        for (long i = 1; i <= scale; i++)
        {
            ReadManyProbeRow? row = await session.GetAsync<ReadManyProbeRow>(i);
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    /// <summary>B 臂：单次多键读。</summary>
    private static async Task<List<ReadManyProbeRow>> ReadManyAsync(string cs, int scale)
    {
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = cs });
        var keys = new List<object>(scale);
        for (long i = 1; i <= scale; i++) keys.Add(i);
        return await session.GetByKeyManyAsync<ReadManyProbeRow>(keys);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static void Delete(string db)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string f = db + suffix;
            if (File.Exists(f)) File.Delete(f);
        }
    }
}

[Table("read_many_probe")]
internal sealed partial class ReadManyProbeRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
    [Column("Qty")] public int Qty { get; set; }
}