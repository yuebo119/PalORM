using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// 审计 2026-09-19 GEN-010(M1)判定实验——基类列继承的增量失效行为。
/// 前提(已核实):列收集走基类链(TableModel.GetMappableProperties 沿 BaseType),基类列继承
/// 是受支持且有快照基线钉住的场景(SnapshotTests 实体 4,ITM-502)。管线谓词只监视挂 [Table]
/// 的声明节点(PalORMGenerator.cs)。
/// 实验问题:仅修改**基类文件**(派生实体的语法树不变)时,增量管线是否重跑派生实体的
/// transform、其生成物是否反映基类新列?
/// <para>断言用产物**内容**(而非时间):若第二轮产物含新列 → 增量语义正确(审计 M1 证伪,
/// GEN-010 撤销);若产物不含新列(复用旧实例)→ 陈旧缓存证实,GEN-010 需修管线。</para>
/// </summary>
internal sealed class BaseTypeIncrementalTests
{
    private const string BaseSource = """
        using PalORM;

        public abstract class ProbeBase
        {
            [Column("created_by")] public string CreatedBy { get; set; } = "";
            [Column("created_at")] public System.DateTimeOffset CreatedAt { get; set; }
        }
        """;

    private const string BaseSourceWithExtraColumn = """
        using PalORM;

        public abstract class ProbeBase
        {
            [Column("created_by")] public string CreatedBy { get; set; } = "";
            [Column("created_at")] public System.DateTimeOffset CreatedAt { get; set; }
            [Column("audit_note")] public string AuditNote { get; set; } = "";
        }
        """;

    private const string DerivedSource = """
        using PalORM;

        [Table("base_type_probe")]
        public sealed partial class InheritedProbe : ProbeBase
        {
            [Key] public long Id { get; set; }
            [Column("name")] public string Name { get; set; } = "";
        }
        """;

    [Test]
    public async Task EditingBaseClassFile_UpdatesDerivedEntityOutput_Incrementally()
    {
        // 一类一语法树:基类与派生类分属两棵树(真实工程布局),基类树变化不触碰派生树
        CSharpCompilation compilation = GeneratorTestHost.CreateCompilation(
            [BaseSource, DerivedSource], "BaseTypeIncrementalConsumer");
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new PalORMGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        string derivedAfterFirstRun = DerivedOutputs(driver);

        // 第二轮:只替换基类树(加 audit_note 列),派生树原样
        CSharpCompilation changed = GeneratorTestHost.CreateCompilation(
            [BaseSourceWithExtraColumn, DerivedSource], "BaseTypeIncrementalConsumer");
        driver = driver.RunGeneratorsAndUpdateCompilation(changed, out _, out _);
        string derivedAfterBaseEdit = DerivedOutputs(driver);

        // 判定断言:派生实体产物必须反映基类新列(增量语义正确性)
        await Assert.That(derivedAfterBaseEdit.Contains("audit_note", StringComparison.Ordinal))
            .IsTrue();
        // 顺带钉住第一轮不含(否则上断言无意义)
        await Assert.That(derivedAfterFirstRun.Contains("audit_note", StringComparison.Ordinal))
            .IsFalse();
    }

    private static string DerivedOutputs(GeneratorDriver driver)
        => string.Join("\n", driver.GetRunResult().Results
            .SelectMany(static result => result.GeneratedSources)
            .Where(static generated => generated.HintName.Contains("InheritedProbe", StringComparison.Ordinal))
            .Select(static generated => generated.SourceText.ToString()));
}
