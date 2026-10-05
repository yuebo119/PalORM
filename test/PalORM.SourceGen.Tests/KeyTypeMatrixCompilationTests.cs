using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PalORM.SourceGen.Tests;

/// <summary>主键类型矩阵的生成物编译防线（2026-10-05 审计：P1 构建中断缺陷的机械化拦截）。
///
/// <para><b>缺陷史</b>：UNNEST 阶段 A 新增的 <c>BuildDeleteKeyArray</c> 用共享助手
/// <c>BuildKeyCastExpression</c> 产出元素值。该助手的默认分支返回
/// <c>Convert.ChangeType(...)</c>，静态类型是 <c>object</c>；单值 binder 因把它包进
/// <c>(object)</c> 参数 Value 而不受影响，数组路径却是 <c>T[] values</c> 的直接赋值——
/// 于是 **decimal / DateTime / enum 等主键的实体整份生成物报 CS0266，构建中断**。
/// 当时 CI 全绿只因仓库内所有实体主键恰为 long/string/Guid，而这类"主键类型覆盖缺口"
/// 不会有人主动写用例。</para>
///
/// <para><b>本测试的形状</b>：对主键类型矩阵逐型生成实体，断言**输出编译无错误**
/// （实体被生成器拒绝时也无错误——拒绝是合法结果，产出坏代码不是）。判据是"无错误"而非
/// "已生成"，因为两种合法结局（生成 / 明确拒绝）都不该让使用者拿到不可编译的产物。</para></summary>
public sealed class KeyTypeMatrixCompilationTests
{
    /// <summary>生成器接受的 provider 值类型全集（含 <c>SourceGenerationValidation</c> 放行、
    /// 但不在 <c>BuildKeyCastExpression</c> 具名分支内的类型——缺陷正在这批上）。</summary>
    private static readonly (string TypeName, string Label)[] KeyTypes =
    [
        ("long", "long"),
        ("int", "int"),
        ("short", "short"),
        ("byte", "byte"),
        ("string", "string"),
        ("System.Guid", "guid"),
        ("bool", "bool"),
        ("decimal", "decimal"),
        ("double", "double"),
        ("float", "float"),
        ("char", "char"),
        ("System.DateTime", "datetime"),
        ("System.DateTimeOffset", "datetimeoffset"),
        ("System.DateOnly", "dateonly"),
        ("System.TimeOnly", "timeonly"),
        ("AuditKeyEnum", "enum"),
    ];

    private static string Source(string tableName, string entityName, string keyType)
        => $$"""
            using PalORM;

            public enum AuditKeyEnum : long { A = 1L, B = 2L }

            [Table("{{tableName}}")]
            public sealed partial class {{entityName}}
            {
                [Key(AutoIncrement = false)] [Column("id")] public {{keyType}} Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

    [Test]
    public async Task KeyTypeMatrix_GeneratedCodeCompiles_NoBuildBreak()
    {
        var failures = new List<string>();
        foreach ((string typeName, string label) in KeyTypes)
        {
            string entity = $"AuditKey_{label}";
            GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(
                Source($"audit_key_{label}", entity, typeName),
                $"KeyMatrix{label}");

            string errors = FormatErrors(result.OutputCompilation);
            if (errors.Length > 0)
            {
                failures.Add($"{label}（{typeName}）→ {errors}");
            }
        }

        // 判据是"零错误"：生成或明确拒绝都可接受，唯独不能产出不可编译代码
        await Assert.That(string.Join("\n\n", failures)).IsEmpty()
            .Because("主键类型矩阵内每个类型的生成物都必须可编译（CS0266 家族 = 构建中断）");
    }

    private static string FormatErrors(CSharpCompilation compilation)
        => string.Join("\n", compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));
}
