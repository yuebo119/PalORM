namespace PalORM.SourceGen.Tests;

/// <summary>PALORM041（P1-D / CONTRACT-1）：同命名空间内 [SqlTemplate] 重名必须报错；
/// 不同名或不同命名空间必须静默。三个方向各自一条用例，缺任一条都可能把"检测被删"
/// 或"检测过宽"洗成绿。</summary>
public sealed class Palorm041TemplateCollisionTests
{
    [Test]
    public async Task SameNamespaceSameName_ReportsPalorm041()
    {
        const string source = """
            using PalORM;
            namespace Models
            {
                public static partial class Templates
                {
                    [SqlTemplate("Q")]
                    public static System.FormattableString First() => $"SELECT 1";

                    [SqlTemplate("Q")]
                    public static System.FormattableString Second() => $"SELECT 2";
                }
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "Palorm041Trigger");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM041")).IsTrue();
    }

    [Test]
    public async Task SameNamespaceDifferentNames_DoesNotReport()
    {
        const string source = """
            using PalORM;
            namespace Models
            {
                public static partial class Templates
                {
                    [SqlTemplate("First")]
                    public static System.FormattableString First() => $"SELECT 1";

                    [SqlTemplate("Second")]
                    public static System.FormattableString Second() => $"SELECT 2";
                }
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "Palorm041Quiet");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM041")).IsFalse();
    }

    [Test]
    public async Task DifferentNamespacesSameName_DoesNotReport()
    {
        const string source = """
            using PalORM;
            namespace Models.One
            {
                public static partial class Templates
                {
                    [SqlTemplate("Q")]
                    public static System.FormattableString First() => $"SELECT 1";
                }
            }
            namespace Models.Two
            {
                public static partial class Templates
                {
                    [SqlTemplate("Q")]
                    public static System.FormattableString Second() => $"SELECT 2";
                }
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "Palorm041Namespaces");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM041")).IsFalse();
    }
}