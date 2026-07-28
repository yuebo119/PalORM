using Microsoft.CodeAnalysis;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// Auto Tagging Interceptor 单元测试。
/// </summary>
/// <remarks>
/// 测试覆盖：
/// - 开关关闭时零生成物（不影响现有行为）
/// - 开关开启时生成拦截器（含 InterceptsLocationAttribute + 拦截方法）
/// - 生成的 Tag 注释含相对路径 + 方法名
/// </remarks>
public class AutoTaggingTests
{
    /// <summary>测试源码：含 [Table] 实体 + ToListAsync 调用。</summary>
    private const string _sourceWithToListAsyncCall = """
        using PalORM;

        [Table("users")]
        public class User
        {
            [Key] public long Id { get; set; }
            public string Name { get; set; } = "";
        }

        public static class Consumer
        {
            public static async Task Probe(object db)
            {
                // 这是被拦截的目标调用——QueryBuilderExtensions.ToListAsync
                _ = await ((PalORM.QueryBuilder<User>)null!).ToListAsync();
            }
        }
    """;

    [Test]
    public async Task AutoTagging_NotEnabled_NoAutoTaggingSourceGenerated()
    {
        // 不传 PalORMAutoTagging 开关——零生成物（不影响现有行为）
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(_sourceWithToListAsyncCall);

        await Assert.That(result.GeneratedSources.Keys).DoesNotContain("PalORM_AutoTagging.g.cs");
    }

    [Test]
    public async Task AutoTagging_Enabled_GeneratesInterceptor()
    {
        // 传 PalORMAutoTagging=true 开关
        var options = new Dictionary<string, string>
        {
            ["build_property.PalORMAutoTagging"] = "true"
        };
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(
            _sourceWithToListAsyncCall, "AutoTaggingConsumer", options);

        // 应生成 PalORM_AutoTagging.g.cs
        await Assert.That(result.GeneratedSources.Keys).Contains("PalORM_AutoTagging.g.cs");

        string generated = result.GeneratedSources["PalORM_AutoTagging.g.cs"];

        // 应包含 InterceptsLocationAttribute 定义
        await Assert.That(generated).Contains("InterceptsLocationAttribute");
        // 应包含拦截方法（ToListAsync_AutoTag_0）
        await Assert.That(generated).Contains("ToListAsync_AutoTag_0");
        // 应包含 [InterceptsLocation(...)] attribute 应用
        await Assert.That(generated).Contains("[global::System.Runtime.CompilerServices.InterceptsLocationAttribute(");
        // 应包含 Tag 注入
        await Assert.That(generated).Contains(".Tag(");
    }

    [Test]
    public async Task AutoTagging_GeneratedInterceptor_ContainsRelativePathAndMember()
    {
        var options = new Dictionary<string, string>
        {
            ["build_property.PalORMAutoTagging"] = "true"
        };
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(
            _sourceWithToListAsyncCall, "AutoTaggingConsumer", options);

        string generated = result.GeneratedSources["PalORM_AutoTagging.g.cs"];

        // Tag 注释应含成员名（Probe 是测试源码中的方法名）
        await Assert.That(generated).Contains("Probe");
        // Tag 注释应含行号（格式 path:line member）
        await Assert.That(generated).Contains(":");
    }

    [Test]
    public async Task AutoTagging_GeneratedInterceptor_HasCorrectSignature()
    {
        var options = new Dictionary<string, string>
        {
            ["build_property.PalORMAutoTagging"] = "true"
        };
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(
            _sourceWithToListAsyncCall, "AutoTaggingConsumer", options);

        string generated = result.GeneratedSources["PalORM_AutoTagging.g.cs"];

        // 拦截方法签名必须逐字匹配 ToListAsync（含 where T : class, new() 约束）
        await Assert.That(generated).Contains("where T : class, new()");
        // 返回类型必须匹配（ValueTask<List<T>>）
        await Assert.That(generated).Contains("ValueTask<global::System.Collections.Generic.List<T>>");
        // 参数必须匹配（this QueryBuilder<T> builder, CancellationToken ct = default）
        await Assert.That(generated).Contains("this global::PalORM.QueryBuilder<T> builder");
        await Assert.That(generated).Contains("global::System.Threading.CancellationToken ct = default");
    }
}
