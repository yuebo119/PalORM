namespace PalORM.SourceGen.Tests;

/// <summary>GEN-008（2026-09-23）：方言选择性发射。属性 <c>PalORMTargetDialects</c> 声明目标方言后，
/// 未目标方言的 SQL 载荷不发射（该方言的 <c>CommandSqlSet</c> 为 <c>default</c>），运行时取到即
/// 响亮失败（Core 侧 <c>CommandSqlByDialect.Get</c> 守卫，另有 Core 用例锁定）。
/// <para>收益实测：注册文件里方言 SQL 字面量占 42.9%（16,405 / 38,209 字节），单方言应用可去约 2/3。</para>
/// <para>缺省属性 = 三方言全发射，由 <c>SnapshotTests</c> 逐字锁定；本文件只覆盖"声明子集"。</para></summary>
internal sealed class DialectSelectionTests
{
    private const string Source = """
        using PalORM;

        [Table("dialect_probe")]
        internal sealed partial class DialectProbeEntity
        {
            [Key] public long Id { get; set; }
            [Column("name")] public string Name { get; set; } = "";
        }
        """;

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, System.StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, System.StringComparison.Ordinal);
        }
        return count;
    }

    [Test]
    public async Task TargetDialects_PostgreSqlOnly_EmitsSingleSetAndDropsMySqlPayload()
    {
        var options = new Dictionary<string, string>
        {
            ["build_property.PalORMTargetDialects"] = "postgresql"
        };
        GeneratorTestHost.GeneratorResult result =
            GeneratorTestHost.RunGenerator(Source, "DialectProbePg", options);
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];

        // 三个方言位中只有一个发射了完整 SQL 集（另两个是 default）
        await Assert.That(CountOccurrences(registry, "new global::PalORM.CommandSqlSet(")).IsEqualTo(1);
        await Assert.That(CountOccurrences(registry, "default")).IsEqualTo(2);
        // 未目标方言（MySQL 反引号形态）的载荷确实没了；实体本体仍在（只有方言载荷被裁）
        await Assert.That(registry).DoesNotContain("UPDATE `dialect_probe`");
        await Assert.That(registry).Contains("dialect_probe");
        // 生成物必须可编译（default 是合法的 CommandSqlSet 实参）
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task TargetDialects_MySqlOnly_KeepsMySqlPayload()
    {
        var options = new Dictionary<string, string>
        {
            ["build_property.PalORMTargetDialects"] = "mysql"
        };
        GeneratorTestHost.GeneratorResult result =
            GeneratorTestHost.RunGenerator(Source, "DialectProbeMy", options);
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];

        await Assert.That(CountOccurrences(registry, "new global::PalORM.CommandSqlSet(")).IsEqualTo(1);
        await Assert.That(registry).Contains("UPDATE `dialect_probe`");
        await Assert.That(registry).DoesNotContain("UPDATE \"dialect_probe\"");
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task TargetDialects_UnknownToken_FallsBackToAllDialects()
    {
        // 拼错属性的代价是体积（全发射），不是运行期故障——与 DialectSelection.Parse 的文档一致
        var options = new Dictionary<string, string>
        {
            ["build_property.PalORMTargetDialects"] = "postgress"
        };
        GeneratorTestHost.GeneratorResult result =
            GeneratorTestHost.RunGenerator(Source, "DialectProbeTypo", options);
        string registry = result.GeneratedSources["PalORM_Registry.g.cs"];

        await Assert.That(CountOccurrences(registry, "new global::PalORM.CommandSqlSet(")).IsEqualTo(3);
    }
}
