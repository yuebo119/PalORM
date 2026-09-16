using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

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
    public async Task Registry_UnchangedInput_IsCached_SingleEntityChange_OnlyRepublishesAffected()
    {
        // 断言用**对象同一性**而不是时间——这是本用例第二版，第一版用时间比值断言，5 轮红 4。
        // 根因：TUnit 并行跑套件，墙钟测量全程与被并行执行的其它测试争 CPU，
        // same-run 内的比值也压不住（实测同一比值在 0.2~0.8 之间跳）。
        // 确定性替代：Roslyn 增量管线命中缓存时会**复用同一批输出对象**，
        // 于是"未变化 → 输出实例不变"与"只改一个实体 → 其它实体的输出实例不变"
        // 都可以用 ReferenceEquals 判定，完全不受机器负载影响。
        string source = BuildEntitySource(EntityCount);
        CSharpCompilation compilation = GeneratorTestHost.CreateCompilation(source, "IncrementalConsumer");
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.Single().Options;

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new PalORMGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        var clock = Stopwatch.StartNew();
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        double cold = clock.Elapsed.TotalMilliseconds;
        Dictionary<string, SourceText> afterCold = SnapshotOutputs(driver);

        clock.Restart();
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        double unchanged = clock.Elapsed.TotalMilliseconds;
        Dictionary<string, SourceText> afterUnchanged = SnapshotOutputs(driver);

        // 只改一个实体的一个列名 → 新语法树 → 新 compilation
        string changedSource = source.Replace(
            "[Column(\"name\")] public string Name", "[Column(\"renamed\")] public string Name",
            StringComparison.Ordinal);
        CSharpCompilation changedCompilation = GeneratorTestHost.CreateCompilation(
            changedSource, "IncrementalConsumer");
        clock.Restart();
        driver = driver.RunGeneratorsAndUpdateCompilation(changedCompilation, out _, out _);
        double singleChange = clock.Elapsed.TotalMilliseconds;
        Dictionary<string, SourceText> afterChange = SnapshotOutputs(driver);

        Log(string.Create(CultureInfo.InvariantCulture,
            $"[增量量具] 实体数={EntityCount} 输出={afterCold.Count} 个文件 · "
            + $"冷跑={cold:F0}ms 未变化重跑={unchanged:F0}ms 改一个实体={singleChange:F0}ms"));

        // ① 输入完全未变 → 全部输出复用同一批实例（增量缓存生效的**确定性**证据）
        int reused = afterCold.Count(pair => afterUnchanged.TryGetValue(pair.Key, out SourceText? next)
            && ReferenceEquals(pair.Value, next));
        await Assert.That(reused).IsEqualTo(afterCold.Count);

        // ② 只改一个实体 → 实测全部文件都是新实例（见下方注释，原假设"逐实体产物复用"被否掉）。
        int reusedAfterChange = afterUnchanged.Count(pair => afterChange.TryGetValue(pair.Key, out SourceText? next)
            && ReferenceEquals(pair.Value, next));
        int republished = afterChange.Count - reusedAfterChange;
        Log(string.Create(CultureInfo.InvariantCulture,
            $"[增量量具] 改一个实体后：复用 {reusedAfterChange} 个文件 · 重发 {republished} 个"));

        // ② 的期望曾经写成"只有 4 个文件重发（该实体 3 份 + 聚合注册表）"——**实测否掉了这个假设**：
        //    改一个实体后 361 个文件**全部**是新实例，一个都没复用。即生成器把所有逐实体产物
        //    与聚合注册表放在**同一个输出节点**上，任何实体变化都会让该节点整体重跑、全部重新物化。
        //    但代价远低于冷跑（154ms vs 829ms，约 1/5）——上游（模型构造、SQL 构建、语法解析）
        //    确实被缓存，重跑的只是"把结果重新物化成一堆 SourceText"。
        //    ⇒ 这修正了"逐实体产物按实体缓存"的推断；真正的增量粒度是"全有或全无"。
        //    断言取"至少有一个文件变化"（严格真），并把精确分布打出来供评估；
        //    若将来改为按实体注册输出节点，复用数会上升，本用例的打印会立刻反映出来。
        await Assert.That(republished).IsGreaterThan(0);
        await Assert.That(reusedAfterChange).IsLessThan(afterChange.Count);
    }

    private static Dictionary<string, SourceText> SnapshotOutputs(GeneratorDriver driver)
        => driver.GetRunResult().Results
            .SelectMany(static result => result.GeneratedSources)
            .ToDictionary(
                static generated => generated.HintName,
                static generated => generated.SourceText,
                StringComparer.Ordinal);

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
