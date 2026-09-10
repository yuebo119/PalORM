namespace PalORM.SourceGen.Tests;

/// <summary>ITM-719/720(r20)：[SqlTemplate] 声明形状与插值串 trivia 锁定。
/// <para>背景：SqlTemplateEmitter 硬编码 <c>partial class</c> + 无参签名，且模板名只过
/// <c>SyntaxFacts.IsValidIdentifier</c>（对 C# 关键字返回 true，已探针实测）。此前这些形态
/// 会把编译错误抛进 .g.cs（ITM-573 家族），现由 PALORM046 定位报错。</para></summary>
public sealed class SqlTemplateDeclarationTests
{
    [Test]
    public async Task KeywordTemplateName_ReportsPalorm046_AndGeneratesNoSuchField()
    {
        const string source = """
            using PalORM;
            public static class C
            {
                [SqlTemplate("class")]
                public static System.FormattableString Q() => $"SELECT 1";
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplateKeyword");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM046")).IsTrue();
        // 不生成字段（否则生成物 `FormattableString class = ...` 编译错误落 .g.cs）
        await Assert.That(result.GeneratedSources.Any(pair =>
            pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task MethodWithParameters_ReportsPalorm046()
    {
        const string source = """
            using PalORM;
            public static class C
            {
                [SqlTemplate("ByArg")]
                public static System.FormattableString Q(int arg) => $"SELECT {arg}";
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplateParams");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM046")).IsTrue();
        await Assert.That(result.GeneratedSources.Any(pair =>
            pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ValidTemplate_GeneratesCompilableField()
    {
        // 对照组：合法声明正常生成、无 PALORM046、生成物可编译
        const string source = """
            using PalORM;
            public static class C
            {
                [SqlTemplate("ActiveUsers")]
                public static System.FormattableString Q() => $"SELECT * FROM users";
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplateValid");

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALORM046")).IsFalse();
        string generated = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal)).Value;
        await Assert.That(generated).Contains("FormattableString ActiveUsers");
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
    }

    [Test]
    public async Task TrailingLineComment_DoesNotLeakIntoGeneratedLiteral()
    {
        // ITM-720(r20)：`ToFullString().Trim()` 会把首尾行注释 trivia 带进初始值，
        // 生成的 `= $"..." // c;` 使分号被注释掉（语法错误落 .g.cs）。改用 ToString() 后
        // 生成物必须可编译且不含注释文本。
        const string source = """
            using PalORM;
            public static class C
            {
                [SqlTemplate("Commented")]
                public static System.FormattableString Q()
                    => // explain
                       $"SELECT * FROM users";
            }
            """;

        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source, "SqlTemplateComment");

        string generated = result.GeneratedSources.Single(pair =>
            pair.Key.StartsWith("SqlTemplate", StringComparison.Ordinal)).Value;
        await Assert.That(generated.Contains("// explain", StringComparison.Ordinal)).IsFalse();
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
    }
}
