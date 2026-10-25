using System.Text.Json;

namespace PalORM.Bench.Shared;

/// <summary>CrossBench 四臂交叉批的读取层（<c>bench/crossbench/results/crossbench-*.json</c>）。
/// <para><b>为什么共享</b>：PerfCli（crossbench 子命令 / 统一报告 FreeSql 节）与 PerfGate
/// （四组表 FreeSql 列）都要"最近完整批"判别与测量装载——复制成两份会让完整批判据各自漂移。</para>
/// <para><b>完整批判别</b>：四臂 × 三方言 × 两档 = 24 个 (Dialect,Rows,Implementation) 三元组；
/// 子集批（--arms/--dialects/--fast）不作为统一报告与四组表的数据源。文件按名倒序（时间戳命名）
/// 取第一个满足覆盖面的批；解析失败（半写文件）按不存在跳过继续找更早的批。</para>
/// <para><b>解析形态</b>：crossbench JSON 是裸测量数组（无信封信封 schema），字段名与 PerfHub
/// history 同构（Dialect/Implementation/Operation/Rows/MedianNs/AllocatedBytesPerOp/ErrorRatio）。
/// 用 JsonDocument 手工取值而非反射反序列化——AOT 全链路纪律（P0 #3）。</para></summary>
internal static class CrossBenchBatch
{
    /// <summary>完整批最低三元组数（四臂 × 三方言 × 两档 = 24；批内每三元组含多操作）。</summary>
    private const int FullBatchMinTriples = 24;

    /// <summary>最近的完整 CrossBench 批文件路径；目录缺失或无完整批返回 null。</summary>
    public static string? LatestFullBatchPath(string crossBenchResultsDir)
    {
        if (!Directory.Exists(crossBenchResultsDir))
        {
            return null;
        }

        foreach (string file in Directory.EnumerateFiles(crossBenchResultsDir, "crossbench-*.json")
                     .OrderDescending(StringComparer.Ordinal))
        {
            List<CrossBenchMeasurement>? items = LoadOrNull(file);
            if (items is not null && CountTriples(items) >= FullBatchMinTriples)
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>装载一批测量并按 (Dialect,Operation,Rows,Implementation) 聚合：交叉批每操作
    /// 两遍反序（臂序相位消除协议，不可减为单遍），冻结模板的口径 = 两遍 Average（MedianNs 与
    /// 分配都是）；ErrorRatio 同取均值。半写/坏文件返回 null（调用方决定跳过或报错）。</summary>
    public static List<CrossBenchMeasurement>? LoadAggregated(string path)
    {
        List<CrossBenchMeasurement>? raw = LoadOrNull(path);
        if (raw is null)
        {
            return null;
        }

        return
        [
            .. raw
                .GroupBy(m => (m.Dialect, m.Implementation, m.Operation, m.Rows))
                .Select(g => new CrossBenchMeasurement(
                    Dialect: g.Key.Dialect,
                    Implementation: g.Key.Implementation,
                    Operation: g.Key.Operation,
                    Rows: g.Key.Rows,
                    MedianUs: g.Average(m => m.MedianUs),
                    AllocBytes: g.Average(m => m.AllocBytes),
                    ErrorRatio: g.Average(m => m.ErrorRatio))),
        ];
    }

    /// <summary>装载一批测量（原始两遍，未聚合）；半写/坏文件返回 null（调用方决定跳过或报错）。</summary>
    public static List<CrossBenchMeasurement>? LoadOrNull(string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            var items = new List<CrossBenchMeasurement>();
            foreach (JsonElement m in doc.RootElement.EnumerateArray())
            {
                items.Add(new CrossBenchMeasurement(
                    Dialect: m.GetProperty("Dialect").GetString() ?? "",
                    Implementation: m.GetProperty("Implementation").GetString() ?? "",
                    Operation: m.GetProperty("Operation").GetString() ?? "",
                    Rows: m.GetProperty("Rows").GetInt32(),
                    MedianUs: m.GetProperty("MedianNs").GetDouble() / 1000.0,
                    AllocBytes: m.TryGetProperty("AllocatedBytesPerOp", out JsonElement a) ? a.GetDouble() : 0,
                    ErrorRatio: m.TryGetProperty("ErrorRatio", out JsonElement e) ? e.GetDouble() : 0));
            }

            return items;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int CountTriples(List<CrossBenchMeasurement> items)
    {
        var triples = new HashSet<string>();
        foreach (CrossBenchMeasurement m in items)
        {
            triples.Add($"{m.Dialect}|{m.Rows}|{m.Implementation}");
        }

        return triples.Count;
    }

    /// <summary>单条交叉测量（单位已归一：MedianNs → MedianUs）。</summary>
    internal sealed record CrossBenchMeasurement(
        string Dialect, string Implementation, string Operation, int Rows,
        double MedianUs, double AllocBytes, double ErrorRatio);
}
