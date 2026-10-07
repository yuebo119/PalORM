using Microsoft.CodeAnalysis;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// Auto Tagging Interceptor 单元测试。
/// </summary>
/// <remarks>
/// 测试覆盖：
/// - 开关关闭时零生成物（不影响现有行为）
/// - 开关开启时生成拦截器（含 InterceptsLocationAttribute + 拦截方法）
/// - 生成的 Tag 注释含文件名/哨兵路径 + 方法名（ITM-697 后恒为文件名，确定性）
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
    public async Task AutoTagging_GeneratedInterceptor_ContainsFileNameAndMember()
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
        // r19/T-P3-11：原"含冒号"弱断言改为路径哨兵断言——内存语法树无文件路径时
        // NormalizePath 恒返回 "unknown"，格式 path:line member 必须完整出现
        await Assert.That(generated).Contains("unknown:");
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

    /// <summary>测试源码：含全部 9 个终态方法调用（ITM-909 哨兵——s_terminals 是 Core
    /// QueryBuilderExtensions 签名的第二真源，本测试锁覆盖面；Core 新增终态时扩本清单）。
    /// 调用形态按各终态真实签名（ForEachAsync 需 Func&lt;T,ct,ValueTask&gt;、ToPageAsync 需
    /// orderBy 表达式、QueryMultipleAsync 需 FormattableString）。</summary>
    private const string _sourceWithAllNineTerminals = """
        using PalORM;
        using System.Linq.Expressions;

        [Table("users")]
        public class User
        {
            [Key] public long Id { get; set; }
            public string Name { get; set; } = "";
        }

        public static class Consumer
        {
            public static async Task Probe(PalORM.QueryBuilder<User> b)
            {
                _ = await b.ToListAsync();
                _ = await b.FirstAsync();
                _ = await b.FirstOrDefaultAsync();
                _ = await b.SingleAsync();
                _ = await b.SingleOrDefaultAsync();
                _ = await b.ExecuteNonQueryAsync();
                _ = await b.ForEachAsync(static (u, ct) => System.Threading.Tasks.ValueTask.CompletedTask);
                _ = await b.ToPageAsync(10, static u => u.Id);
                _ = await b.QueryMultipleAsync($"SELECT 1");
            }
        }
    """;

    [Test]
    public async Task AutoTagging_AllNineTerminals_Intercepted()
    {
        // ITM-909：九终态全覆盖哨兵——9 个调用点各生成一个拦截方法。
        // Emitter 漏登某个终态（s_terminals 与 Core 漂移）时该测试红。
        var options = new Dictionary<string, string>
        {
            ["build_property.PalORMAutoTagging"] = "true"
        };
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(
            _sourceWithAllNineTerminals, "AutoTaggingNine", options);

        string generated = result.GeneratedSources["PalORM_AutoTagging.g.cs"];
        // 已知限制：GeneratorTestHost 未启用 InterceptorsNamespaces（CS9137），OutputCompilation
        // 零错断言不可用——生成物可编译性由真实消费工程（AotTest.* 开 PalORMAutoTagging +
        // InterceptorsNamespaces）验证；本测试锁拦截覆盖面与签名文本。
        int interceptCount = System.Text.RegularExpressions.Regex.Count(
            generated, @"\[global::System\.Runtime\.CompilerServices\.InterceptsLocationAttribute\(");
        string missing = string.Join(",", s_allNineTerminalNames
            .Where(t => !generated.Contains($"{t}_AutoTag_", StringComparison.Ordinal)));
        await Assert.That($"count={interceptCount} missing=[{missing}]").IsEqualTo("count=9 missing=[]");

        // ITM-914 签名正确性锁（文本级）：三个多参/双泛型终态的拦截签名必须逐字含类型参数与
        // 全部参数。旧硬编码 <T> 两参模板（撤模板化）下生成的是错误签名——计数断言无判别力
        // （attribute 文本同在），此三断言是模板化修复的咬合面。
        await Assert.That(generated.Contains(
            "ForEachAsync_AutoTag_", StringComparison.Ordinal)
            && generated.Contains("global::System.Func<T, global::System.Threading.CancellationToken, global::System.Threading.Tasks.ValueTask> action", StringComparison.Ordinal)
            && generated.Contains(".ForEachAsync(action, ct)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(generated.Contains(
            "<T, TKey>(this global::PalORM.QueryBuilder<T> builder, int pageSize, global::System.Linq.Expressions.Expression<global::System.Func<T, TKey>> orderBy", StringComparison.Ordinal)
            && generated.Contains(".ToPageAsync(pageSize, orderBy, lastValue, descending, ct)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(generated.Contains(
            "QueryMultipleAsync_AutoTag_", StringComparison.Ordinal)
            && generated.Contains("global::System.FormattableString sql", StringComparison.Ordinal)
            && generated.Contains(".QueryMultipleAsync(sql, ct)", StringComparison.Ordinal)).IsTrue();
    }

    private static readonly string[] s_allNineTerminalNames =
    [
        "ToListAsync", "FirstAsync", "FirstOrDefaultAsync", "SingleAsync",
        "SingleOrDefaultAsync", "ExecuteNonQueryAsync", "ForEachAsync",
        "ToPageAsync", "QueryMultipleAsync",
    ];
}
