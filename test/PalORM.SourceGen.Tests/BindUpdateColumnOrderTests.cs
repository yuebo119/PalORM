using System.Text.RegularExpressions;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// 审计 2026-09-19 GEN-012 防线——BindUpdate 与 BindUpdateValues 的<b>参数列序一致性</b>。
/// 两个 binder 驱动同一条 UPDATE SQL 的占位符序（BatchUpdateSqlBuilder 按列序生成占位符），
/// 任何一侧列序漂移 = 参数绑到错误的列 = <b>错误数据写入而非编译失败</b>。发射侧已由
/// GetUpdateColumnOrder 单一真源保证；本测试从<b>生成产物</b>反提取两方法体内实体属性的
/// 出现顺序并断言一致——即使未来有人绕过单一真源手写发射，本防线仍独立拦截。
/// </summary>
public sealed class BindUpdateColumnOrderTests
{
    private const string Source = """
        using PalORM;

        [Table("bind_order_probe")]
        public sealed partial class BindOrderProbe
        {
            [Key] public long Id { get; set; }
            [Column("name")] public string Name { get; set; } = "";
            [Column("stock")] public long Stock { get; set; }
            [Column("price")] public decimal Price { get; set; }
            [ConcurrencyCheck] public int Version { get; set; }
        }
        """;

    [Test]
    public async Task BindUpdate_And_BindUpdateValues_ReferencePropertiesInSameOrder()
    {
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(Source, "BindOrderConsumer");

        string factory = result.GeneratedSources.Values.FirstOrDefault(
            static s => s.Contains("BindOrderProbe", StringComparison.Ordinal)
                && s.Contains("BindUpdateValues", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("CommandFactory 生成物未找到（含 BindUpdateValues 的文件）");

        var updateOrder = ExtractEntityReferences(factory, "BindUpdate(");
        var valuesOrder = ExtractEntityReferences(factory, "BindUpdateValues(");

        await Assert.That(updateOrder.Count).IsGreaterThan(1);
        await Assert.That(string.Join(",", updateOrder)).IsEqualTo(string.Join(",", valuesOrder));
    }

    /// <summary>提取指定方法体内 <c>entity.X</c> 属性引用的出现顺序（参数绑定序）。</summary>
    private static List<string> ExtractEntityReferences(string source, string methodSignature)
    {
        int methodStart = source.IndexOf(methodSignature, StringComparison.Ordinal);
        if (methodStart < 0) throw new InvalidOperationException($"生成物缺少方法 {methodSignature}");
        int bodyStart = source.IndexOf('{', methodStart, StringComparison.Ordinal);
        // 方法体到下一个 "internal static"（同文件下一方法）为止
        int next = source.IndexOf("internal static", bodyStart, StringComparison.Ordinal);
        string body = next < 0 ? source[bodyStart..] : source[bodyStart..next];

        return [.. Regex.Matches(body, @"entity\.(\w+)").Select(static m => m.Groups[1].Value)];
    }
}
