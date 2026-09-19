namespace PalORM.SourceGen.Tests;

public sealed class SqlTemplateNamespaceCollisionTests
{
    [Test]
    public async Task ExistingNonPartialSqlTemplatesClass_ReportsPalorm046()
    {
        // 冲突成立条件：既有类型与生成类**同命名空间**（生成物落 model.Namespace）。
        // global namespace 下生成物落 PalORM.Generated，不冲突（另有用例覆盖）。
        const string source = """
            using PalORM;
            namespace Models
            {
                public class SqlTemplates { }
                public static class C
                {
                    [SqlTemplate("Q")]
                    public static System.FormattableString Q() => $"SELECT 1";
                }
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplatesClash");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM046")).IsTrue();
        await Assert.That(result.GeneratedSources.Any(pair =>
            pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ExistingPartialSqlTemplatesClass_DoesNotReport()
    {
        // 对照：既有同名类型是 partial（生成物将与之合并）——不应报
        const string source = """
            using PalORM;
            namespace Models
            {
                public static partial class SqlTemplates { }
                public static class C
                {
                    [SqlTemplate("Q")]
                    public static System.FormattableString Q() => $"SELECT 1";
                }
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplatesPartial");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM046")).IsFalse();
    }

    [Test]
    public async Task GlobalNamespaceSqlTemplatesClass_DoesNotConflict()
    {
        // 边界锁定：global namespace 时生成物落 PalORM.Generated，与 global 的 SqlTemplates
        // 不同命名空间——不报 PALORM046 且生成物可编译（澄清"global 也算冲突"的误判）
        const string source = """
            using PalORM;
            public class SqlTemplates { }
            public static class C
            {
                [SqlTemplate("Q")]
                public static System.FormattableString Q() => $"SELECT 1";
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplatesGlobal");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM046")).IsFalse();
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
    }
}
