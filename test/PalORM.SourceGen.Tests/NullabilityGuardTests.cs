namespace PalORM.SourceGen.Tests;

/// <summary>GEN-007（2026-09-23）：NRT 未启用（<c>#nullable disable</c>）的引用列读路径守卫。
/// <para>原实现只认 NRT 注解（IsNullable ← NullableAnnotation.Annotated）：未启用 NRT 的存量实体
/// 遇到 DB NULL 会落到驱动层 SqlNullValueException（无列名无实体名）。现在这类列发射命名化
/// 响亮失败；且只在"NRT 关闭"这一态生效——NRT 开启且显式声明非空的列保持直读（零额外
/// reader 访问，可空列双读的代价见 L44 登记，不扩大到非空列）。</para>
/// <para>两个用例各钉一态：误放宽（把 NotAnnotated 也当未知）或漏放宽（None 仍直读）都会让其一变红。</para></summary>
internal sealed class NullabilityGuardTests
{
    private const string NrtDisabledSource = """
        #nullable disable
        using PalORM;

        [Table("nrt_off")]
        internal sealed partial class NrtOffEntity
        {
            [Key] public long Id { get; set; }
            [Column("name")] public string Name { get; set; }
        }
        """;

    private const string NrtEnabledSource = """
        #nullable enable
        using PalORM;

        [Table("nrt_on")]
        internal sealed partial class NrtOnEntity
        {
            [Key] public long Id { get; set; }
            [Column("name")] public string Name { get; set; } = "";
        }
        """;

    private static string RowFactorySource(GeneratorTestHost.GeneratorResult result)
        => string.Concat(result.GeneratedSources
            .Where(pair => pair.Key.Contains("RowFactory", StringComparison.Ordinal))
            .Select(pair => pair.Value));

    [Test]
    public async Task NrtDisabled_ReferenceColumn_EmitsNamedNullGuard()
    {
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(NrtDisabledSource);

        string rowFactory = RowFactorySource(result);

        await Assert.That(rowFactory).Contains("IsDBNull");
        await Assert.That(rowFactory).Contains("nullability of the property is unknown");
        // 可归因上下文：列名与属性名都在消息里（原驱动异常的失败点没有这些信息）
        await Assert.That(rowFactory).Contains("Column 'name' (property 'Name')");

        // 生成物必须可编译：`? throw ... : read` 表达式要能赋给非空属性（否则消费方编译失败）
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task NrtEnabled_NonNullableReferenceColumn_KeepsDirectRead()
    {
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(NrtEnabledSource);

        string rowFactory = RowFactorySource(result);

        await Assert.That(rowFactory).DoesNotContain("nullability of the property is unknown");
        await Assert.That(rowFactory).DoesNotContain("IsDBNull");
    }
}
