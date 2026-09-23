using System.Text.Json;

namespace PalORM.PerfGate;

/// <summary>BDN 结果目录的读取——把多个 <c>*-report*.json</c> 合并为一张基准表。
/// <para>同一基准可能在多个导出文件里出现（如同时导出 json 与 json-brief），
/// 后读到的覆盖先读到的；出现一次即可。</para></summary>
internal static class ResultReader
{
    /// <summary>读取结果目录。任何无法解读的输入都以异常终止——判定不猜。</summary>
    /// <exception cref="InvalidOperationException">目录无 JSON、JSON 无法解析、或条目缺少必要字段。</exception>
    public static ResultSet Read(string resultsDirectory)
    {
        if (!Directory.Exists(resultsDirectory))
        {
            throw new InvalidOperationException(
                $"结果目录不存在: {resultsDirectory}。基准是否真的跑过？");
        }

        string[] files = [.. Directory.EnumerateFiles(
            resultsDirectory, "*-report*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"结果目录 {resultsDirectory} 下未找到 BDN 结果 JSON（*-report*.json）。");
        }

        Dictionary<string, BenchmarkMeasurement> benchmarks = new(StringComparer.Ordinal);
        BdnHostEnvironment? environment = null;

        foreach (string file in files)
        {
            BdnRoot root = ReadFile(file);
            environment ??= root.HostEnvironmentInfo;
            Collect(root, file, benchmarks);
        }

        return new ResultSet(benchmarks, environment);
    }

    private static BdnRoot ReadFile(string file)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            return JsonSerializer.Deserialize(stream, BdnJsonContext.Default.BdnRoot)
                ?? throw new InvalidOperationException($"{file} 反序列化为 null");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"无法解析 {file}: {exception.Message}", exception);
        }
    }

    private static void Collect(
        BdnRoot root, string file, Dictionary<string, BenchmarkMeasurement> benchmarks)
    {
        foreach (BdnBenchmark benchmark in root.Benchmarks)
        {
            if (benchmark.FullName is null || benchmark.Statistics?.Median is null)
            {
                throw new InvalidOperationException(
                    $"{file} 中的条目缺少 FullName/Statistics.Median: {benchmark.FullName ?? "(null)"}");
            }

            // 无 [MemoryDiagnoser] 的类不产出分配数据。这类条目跳过而非失败：覆盖度由
            // "基线列了什么就必须出现什么"兜底——基线里有的基准本轮缺失会被判 FAIL，
            // 不会静默缩水。（当前基准套件的类都带 diagnoser；此分支为"临时摘掉 diagnoser
            // 做纯速度交叉验证"的场景保留。）
            if (benchmark.Memory?.BytesAllocatedPerOperation is not { } allocated) continue;

            benchmarks[benchmark.FullName] = new BenchmarkMeasurement(
                benchmark.FullName, allocated, benchmark.Statistics.Median.Value);
        }
    }
}

/// <summary>一次运行的全部测量值。</summary>
/// <param name="Benchmarks">基准全名 → 测量值。</param>
/// <param name="Environment">宿主环境（可能为 null）。</param>
internal sealed record ResultSet(
    Dictionary<string, BenchmarkMeasurement> Benchmarks,
    BdnHostEnvironment? Environment);

/// <summary>单条基准的测量值。</summary>
/// <param name="Name">基准全名。</param>
/// <param name="AllocatedBytes">每次操作分配字节数。</param>
/// <param name="MedianNanoseconds">中位耗时（纳秒）。</param>
internal sealed record BenchmarkMeasurement(string Name, double AllocatedBytes, double MedianNanoseconds);
