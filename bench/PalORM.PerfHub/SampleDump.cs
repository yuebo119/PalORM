// 迭代级采样诊断落盘（2026-10-17）：默认关闭，环境变量 PALORM_PERFHUB_SAMPLE_DUMP=1 启用。
// 背景：当日多批全量出现远程臂瞬时劣化（EnumInserts 12×、TxRollback 3.5×、GetByKey +77% 等），
// filtered 复测即恢复，聚合指标（中位/均值/ErrorRatio）只能看到"这轮不稳"，分不清两种形态：
// 单次尖峰（个别迭代数百 ms、其余正常 = 环境瞬时）与全程偏慢（系统性劣化 = 产品或服务器问题）。
// 逐迭代原始采样一次定性；离群迭代带墙钟时间戳，可与批期间的双端负载采样日志对齐归因。
// 落盘与 history 同目录同 stamp，纯诊断产物，不进门禁（V20 只对 OperationNames），按需提交。
using System.Text;

namespace PalORM.PerfHub;

internal static class SampleDump
{
    private static StreamWriter? _writer;
    private static readonly string Stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

    /// <summary>诊断开关状态——热路径据此跳过原始采样拷贝。</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("PALORM_PERFHUB_SAMPLE_DUMP") == "1";

    /// <summary>落一条测量记录：含离群迭代（>3× 中位且 ≥1ms 绝对地板，至多 10 个，带原始序号与墙钟）。</summary>
    public static void Emit(string dialect, string impl, string operation, int rows,
        int iterations, double medianMs, double meanMs, double[] rawMs)
    {
        if (!Enabled) return;
        try
        {
            _writer ??= new StreamWriter(
                Path.Combine(RepoRoot(), "bench", "perfhub", "results", $"samples-{Stamp}.jsonl"),
                append: true, new UTF8Encoding(false)) { AutoFlush = true };

            var sb = new StringBuilder();
            sb.Append("{\"dialect\":\"").Append(dialect).Append("\",\"impl\":\"").Append(impl)
              .Append("\",\"op\":\"").Append(operation).Append("\",\"rows\":").Append(rows)
              .Append(",\"iters\":").Append(iterations)
              .Append(",\"median_ms\":").Append(Math.Round(medianMs, 4))
              .Append(",\"mean_ms\":").Append(Math.Round(meanMs, 4))
              .Append(",\"outliers\":[");
            bool first = true;
            int written = 0;
            // 降序扫描：>3× 中位且 ≥1ms 绝对地板（停顿级事件；亚毫秒抖动是快操作的正常噪声，
            // 全淹没会稀释信号）。整轮水平漂移（所有迭代一起慢）不进 outliers，由 median/mean 承载。
            // 首次不满足后续都更小，可断
            foreach (var (ms, idx) in rawMs.Select((ms, i) => (ms, i + 1)).OrderByDescending(t => t.Item1))
            {
                if (written >= 10 || ms < 1.0 || ms <= medianMs * 3) break;
                if (!first) sb.Append(',');
                sb.Append("{\"i\":").Append(idx)
                  .Append(",\"ms\":").Append(Math.Round(ms, 3))
                  .Append(",\"t\":\"").Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("\"}");
                first = false;
                written++;
            }
            sb.Append("]}");
            _writer.WriteLine(sb.ToString());
        }
        catch
        {
            // 诊断落盘失败不阻断跑测——诊断不能杀主流程（B104 家族反向原则）
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
