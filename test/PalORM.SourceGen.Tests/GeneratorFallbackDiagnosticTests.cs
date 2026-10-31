namespace PalORM.SourceGen.Tests;

/// <summary>PALORM045（CONTRACT-2 逐码守卫需要的触发/不触发对）：
/// 生成器把无法建模的 [Table] 实体跳过时，必须发兜底 Warning——分析器规则被
/// .editorconfig/ruleset 抑制时，它是唯一的编译期线索。</summary>
public sealed class GeneratorFallbackDiagnosticTests
{
    [Test]
    public async Task InvalidTableEntity_ReportsPalorm045()
    {
        // 缺 [Key]：分析器报 PALORM001；生成器跳过该实体并给 PALORM045 兜底
        const string source = """
            using PalORM;
            namespace Models
            {
                [Table("no_key")]
                public partial class NoKey
                {
                    [Column("name")] public string Name { get; set; } = "";
                }
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "Palorm045Trigger");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM045")).IsTrue();
    }

    [Test]
    public async Task ValidTableEntity_DoesNotReportPalorm045()
    {
        const string source = """
            using PalORM;
            namespace Models
            {
                [Table("valid_entity")]
                public partial class ValidEntity
                {
                    [Key] public long Id { get; set; }
                    [Column("name")] public string Name { get; set; } = "";
                }
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "Palorm045Quiet");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM045")).IsFalse();
    }
}