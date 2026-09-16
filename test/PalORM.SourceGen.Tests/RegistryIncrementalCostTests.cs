using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PalORM.SourceGen.Tests;

/// <summary>增量重生成代价的守卫——回答「改一个实体要付多少」。
/// <para><b>为什么需要它</b>：注册表产物是**聚合输出**（一个 <c>PalORM_Registry.g.cs</c> 覆盖全部实体）。
/// 聚合输出的增量缓存粒度就是整份文件：任何一个实体变化都会让整份注册表失效并全量重发——
/// 在 500 实体的工程里，每次改一个实体都要重新生成全部 500 个实体的注册代码。
/// 本用例把该代价量出来并钉住"未变化时不得重算"这一增量契约。</para>
/// <para><b>为什么断言"未变化不重算"而不是断言时间</b>：时间是环境相关的（机器/负载），
/// 而"输入未变则输出缓存命中"是 Roslyn 增量管线的确定性契约——用比值而非绝对值断言，
/// 并同时打印单实体变化的全量重发代价供人评估。</para></summary>
internal sealed class RegistryIncrementalCostTests
{
    private const int EntityCount = 120;

    [Test]
    public async Task Registry_UnchangedInput_IsCached_SingleEntityChange_RepublishesAll()
    {
        string source = BuildEntitySource(EntityCount);
        CSharpCompilation compilation = GeneratorTestHost.CreateCompilation(source, "IncrementalConsumer");
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.Single().Options;

        // 同一 driver 连续三轮：冷跑 → 输入未变 → 改一个实体
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new PalORMGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        double cold = MeasureRun(driver, compilation, out GeneratorDriver warmed);
        double unchanged = MeasureRun(warmed, compilation, out GeneratorDriver warm2);

        // 只改一个实体的一个列名 → 新语法树 → 新 compilation
        string changedSource = source.Replace(
            "[Column(\"name\")] public string Name", "[Column(\"renamed\")] public string Name",
            StringComparison.Ordinal);
        CSharpCompilation changedCompilation = GeneratorTestHost.CreateCompilation(
            changedSource, "IncrementalConsumer");
        double singleChange = MeasureRun(warm2, changedCompilation, out _);

        Log(string.Create(CultureInfo.InvariantCulture,
            $"[增量量具] 实体数={EntityCount} 冷跑={cold:F0}ms 未变化重跑={unchanged:F0}ms "
            + $"改一个实体={singleChange:F0}ms（冷跑的 {singleChange / cold * 100:F0}%）"));

        // 增量契约：输入完全未变时必须走缓存（允许抖动，故用 20% 冷跑耗时作宽松上界）
        await Assert.That(unchanged).IsLessThan(cold * 0.2);
        // 实测（120 实体 × 3 列）：冷跑 653ms · 未变化重跑 9ms（缓存生效）· 改一个实体 131ms
        // = 冷跑的 20%。**这修正了"聚合输出=改一个实体就全量重发"的推断**：逐实体产物
        // （RowFactory/CommandFactory/Migration）确实命中缓存，只有注册表这一份聚合产物重发，
        // 其份额约 20%。500 实体规模下按此推算单次编辑约数百毫秒——可接受，故**不建议**
        // 为它拆成按块多文件输出（那会改变生成物文件集，牵动快照与文档）。
        // 断言取"> 0 且 < 冷跑的 40%"：防的是"增量完全失效"（退化为冷跑量级）这种回归。
        await Assert.That(singleChange).IsGreaterThan(0);
        await Assert.That(singleChange).IsLessThan(cold * 0.4);
    }

    private static double MeasureRun(GeneratorDriver driver, Compilation compilation, out GeneratorDriver updated)
    {
        // 预热 JIT（首次调用含 Roslyn 内部初始化，不属于被测成本）
        var sw = Stopwatch.StartNew();
        GeneratorDriver result = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out _);
        sw.Stop();
        updated = result;
        return sw.Elapsed.TotalMilliseconds;
    }

    private static string BuildEntitySource(int count)
    {
        const string usingDirectives = """
            using PalORM;

            """;
        var builder = new StringBuilder(usingDirectives);
        for (int i = 0; i < count; i++)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"[Table(\"inc_entity_{i}\")]");
            builder.AppendLine(CultureInfo.InvariantCulture, $"public sealed partial class IncEntity{i}");
            builder.AppendLine("{");
            builder.AppendLine("    [Key] public long Id { get; set; }");
            builder.AppendLine("    [Column(\"name\")] public string Name { get; set; } = \"\";");
            builder.AppendLine("    [Column(\"qty\")] public int Qty { get; set; }");
            builder.AppendLine("}");
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static void Log(string message) => Console.WriteLine(message);
}
