using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// 审计 2026-09-19 GEN-011（M6）防线——并发令牌的生成器自守卫（CanGenerateEntity）。
/// [Key] 一直是双层防线（分析器 + 自守卫，见 SourceGenerationValidation 注释），令牌族此前
/// 只有分析器一层；本测试驱动纯生成器（不加载分析器，等价于 PALORM012/013 被降级），
/// 断言坏令牌形状被 Skipped 而非穿透到 GenerateIncrementVersionBody 的 <c>entity.X++</c>
/// （Guid/string → CS0019、init-only → CS8852、多令牌 → FirstOrDefault 静默只递增其一）。
/// </summary>
public sealed class ConcurrencyTokenGuardTests
{
    private static string Source(string tableName, string entityName, string tokenLine)
        => $$"""
            using PalORM;

            [Table("{{tableName}}")]
            public sealed partial class {{entityName}}
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            {{tokenLine}}
            }
            """;

    [Test]
    public async Task GuidToken_EntitySkipped_NotBrokenGeneratedCode()
    {
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(
            Source("token_guard_guid", "BadGuidToken",
                "[ConcurrencyCheck] public System.Guid Version { get; set; }"),
            "ConcGuidToken");

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(result.GeneratedSources.Values.Any(
            static s => s.Contains("BadGuidToken", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task InitOnlyToken_EntitySkipped_NotBrokenGeneratedCode()
    {
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(
            Source("token_guard_init", "BadInitToken",
                "[ConcurrencyCheck] public int Version { get; init; }"),
            "ConcInitToken");

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(result.GeneratedSources.Values.Any(
            static s => s.Contains("BadInitToken", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task MultipleTokens_EntitySkipped_NotSilentlyIncrementingOne()
    {
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(
            Source("token_guard_dual", "BadDualToken",
                "[ConcurrencyCheck] public int VersionA { get; set; }\n            [ConcurrencyCheck] public int VersionB { get; set; }"),
            "ConcDualToken");

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(result.GeneratedSources.Values.Any(
            static s => s.Contains("BadDualToken", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ValidLongToken_EntityStillGenerated()
    {
        // 对照组合法形状（long、可写、单个）：正常注册，守卫不误伤
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(
            Source("token_guard_good", "GoodLongToken",
                "[ConcurrencyCheck] public long Version { get; set; }"),
            "ConcLongToken");

        await Assert.That(FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(result.GeneratedSources.Values.Any(
            static s => s.Contains("GoodLongToken", StringComparison.Ordinal))).IsTrue();
    }

    private static string FormatErrors(CSharpCompilation compilation)
        => string.Join("\n", compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => d.ToString()));
}
