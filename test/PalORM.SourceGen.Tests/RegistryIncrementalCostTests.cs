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
        // **一实体一语法树**：真实工程几乎不会把 120 个实体写在同一个文件里，而
        // ForAttributeWithMetadataName 的缓存粒度是语法树——单文件布局会让"改一个实体"
        // 变成"整棵树变了"，从而把所有实体都标记为变更。用单文件测出的代价是量具假象。
        string[] entitySources = BuildEntitySources(EntityCount);
        CSharpCompilation compilation = GeneratorTestHost.CreateCompilation(
            entitySources, "IncrementalConsumer");
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;

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

        // 只改一个实体的一个列名——只替换它那一棵树，其余 119 棵树原样重用
        string[] changedSources = (string[])entitySources.Clone();
        changedSources[0] = changedSources[0].Replace(
            "[Column(\"name\")] public string Name", "[Column(\"renamed\")] public string Name",
            StringComparison.Ordinal);
        CSharpCompilation changedCompilation = GeneratorTestHost.CreateCompilation(
            changedSources, "IncrementalConsumer");
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

        // ② 只改一个实体 → 只有受影响实体的 3 份产物 + 聚合注册表被重发，其余逐实体产物复用。
        //    这是生成器增量设计的**正向证据**：值比较器（TableModel 全字段 EquatableArray）
        //    与每实体输出节点确实按实体生效。
        int reusedAfterChange = afterUnchanged.Count(pair => afterChange.TryGetValue(pair.Key, out SourceText? next)
            && ReferenceEquals(pair.Value, next));
        int republished = afterChange.Count - reusedAfterChange;
        Log(string.Create(CultureInfo.InvariantCulture,
            $"[增量量具] 改一个实体后：复用 {reusedAfterChange} 个文件 · 重发 {republished} 个"));

        // 复现提示：把 BuildEntitySources 换回"全部实体拼成一个字符串"的单树布局，
        // 这里的复用数会掉到 0——那是**量具假象**而非生成器缺陷：被改的那棵树是全量实体，
        // 于是全部模型都判为变更。测增量务必用真实布局（一实体一文件）。
        // 一树一实体布局下的精确契约：重发面 = 该实体的 3 份产物（RowFactory/CommandFactory/
        // Migration）+ 1 份聚合注册表 = 4。这条断言依赖"每实体一个语法树"的布局——正是本用例
        // 特意构造的布局（见开头注释）；若未来生成器增加逐实体产物种类，此数会变，用例会红，
        // 届时同步该数与文档即可。
        await Assert.That(republished).IsEqualTo(4);
        await Assert.That(reusedAfterChange).IsLessThan(afterChange.Count);
    }

    private static Dictionary<string, SourceText> SnapshotOutputs(GeneratorDriver driver)
        => driver.GetRunResult().Results
            .SelectMany(static result => result.GeneratedSources)
            .ToDictionary(
                static generated => generated.HintName,
                static generated => generated.SourceText,
                StringComparer.Ordinal);

    /// <summary>每个实体一个源文件（一语法树）——贴近真实工程布局，也是增量缓存的有效粒度。</summary>
    private static string[] BuildEntitySources(int count)
    {
        var sources = new string[count];
        for (int i = 0; i < count; i++)
        {
            var builder = new StringBuilder(64);
            builder.AppendLine("using PalORM;");
            builder.AppendLine(CultureInfo.InvariantCulture, $"[Table(\"inc_entity_{i}\")]");
            builder.AppendLine(CultureInfo.InvariantCulture, $"public sealed partial class IncEntity{i}");
            builder.AppendLine("{");
            builder.AppendLine("    [Key] public long Id { get; set; }");
            builder.AppendLine("    [Column(\"name\")] public string Name { get; set; } = \"\";");
            builder.AppendLine("    [Column(\"qty\")] public int Qty { get; set; }");
            builder.AppendLine("}");
            sources[i] = builder.ToString();
        }
        return sources;
    }

    private static void Log(string message) => Console.WriteLine(message);
}
