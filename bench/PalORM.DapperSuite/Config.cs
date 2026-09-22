using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;

namespace PalORM.DapperSuite;

/// <summary>官方 Config.cs 的移植：Job.ShortRun + Launch 1 + Warmup 2 + Iteration 10 +
/// UnrollFactor 500 + MemoryDiagnoser + ORM/Return/Method 列 + Mean/StdDev/Error +
/// Baseline 比值 + 最快到最慢排序。
/// <para>口径差登记：官方 <c>ReturnColum</c>（工作负载方法返回类型名）未移植——本套件
/// PalORM 臂是 async-only，该列对三臂会一律塌成 <c>Task</c>，丧失区分度且与方法名重复。</para>
/// <para>官方 <c>AddColumn(ORMColum)</c> 与 <c>AddColumn(TargetMethodColumn.Method)</c> 是并列
/// 两个 AddColumn 调用，缺前者报告将无"哪一臂"列，缺后者将无方法名列（本套件初版即漏后者，
/// 表现为 joined 表整列丢失、9 行无法归因）。</para></summary>
public class Config : ManualConfig
{
    public const int Iterations = 500;   // 官方常量：unroll 因子

    public Config()
    {
        AddLogger(ConsoleLogger.Default);

        AddExporter(CsvExporter.Default);
        AddExporter(MarkdownExporter.GitHub);
        AddExporter(HtmlExporter.Default);

        MemoryDiagnoser md = MemoryDiagnoser.Default;
        AddDiagnoser(md);
        AddColumn(new OrmColumn());
        AddColumn(TargetMethodColumn.Method);
        AddColumn(StatisticColumn.Mean);
        AddColumn(StatisticColumn.StdDev);
        AddColumn(StatisticColumn.Error);
        AddColumn(BaselineRatioColumn.RatioMean);
        AddColumnProvider(DefaultColumnProviders.Metrics);

        AddJob(Job.ShortRun
            .WithLaunchCount(1)
            .WithWarmupCount(2)
            .WithUnrollFactor(Iterations)
            .WithIterationCount(10)
        );
        Orderer = new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest);
        Options |= ConfigOptions.JoinSummary;
    }
}
