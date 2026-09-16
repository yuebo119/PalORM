using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace PalORM.SourceGen.Tests;

/// <summary>Registry 生成的**规模墙**守卫。
/// <para>背景：注册代码全部落在单个 <c>[ModuleInitializer] Initialize()</c> 方法里，
/// IL 随实体数线性增长（实测 ≈983 B/实体）——500 实体即单方法约 490 KB IL，
/// 且这段 IL 在模块加载期被一次性 JIT，与"只用到一两个实体"的消费者无关。
/// 拆分按实体块发射后，每个方法的上界与实体总数解耦，本测试即为该性质的守卫。</para>
/// <para>断言口径是**单方法 IL 字节数**而非文件大小：文件大小随实体数必然增长（每实体都要有注册项），
/// 真正要钉住的是「没有哪个生成方法会随规模无限膨胀」。</para></summary>
internal sealed class RegistryScaleTests
{
    /// <summary>单方法 IL 上限。取 64 KB：这是 IL 方法体的经典分界（fat header 之上 JIT
    /// 代价超线性），也是"仪器必须能失败"的边界——单体实现下 120 实体 = 130 KB 即失败。</summary>
    private const int MethodIlBudgetBytes = 64 * 1024;

    /// <summary>两维覆盖：实体数（决定总量）与列宽（决定单实体体量）。
    /// 只有实体数一维时，"固定每块 N 个实体"的切块法在宽表上撑破方法体也测不出来。</summary>
    [Test]
    [Arguments(120, 4)]
    [Arguments(60, 30)]
    public async Task Registry_NoGeneratedMethodExceedsIlBudget(int entityCount, int columnCount)
    {
        string source = BuildEntitySource(entityCount, columnCount);
        var result = GeneratorTestHost.RunGenerator(source, "RegistryScaleConsumer");

        string compileErrors = GeneratorTestHost.FormatErrors(result.OutputCompilation);
        await Assert.That(compileErrors).IsEmpty();

        var ilByMethod = ReadRegistryMethodIl(result.OutputCompilation);
        await Assert.That(ilByMethod).IsNotEmpty();

        (string largestName, int largestIl) = ilByMethod.MaxBy(static pair => pair.Value);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[规模量具] 实体数={entityCount}，列数={columnCount}，注册方法数={ilByMethod.Count}，"
            + $"最大单方法 IL={largestIl} B（{largestName}），上限={MethodIlBudgetBytes} B"));
        Console.WriteLine("[规模量具] 各方法 IL：" + string.Join(
            ", ",
            ilByMethod.OrderByDescending(static pair => pair.Value)
                .Take(8)
                .Select(static pair => $"{pair.Key}={pair.Value}B")));

        MeasureJitCost(result.OutputCompilation);
        await Assert.That(largestIl).IsLessThan(MethodIlBudgetBytes);
    }

    /// <summary>编译产物里 <c>PalORM_RegistryInitializer</c> 各方法的 IL 字节数。
    /// 直接读 PE 元数据而不是解析生成文本——文本行数不等于 IL 长度（字符串字面量在 IL 里只是一个 ldstr）。</summary>
    private static Dictionary<string, int> ReadRegistryMethodIl(Microsoft.CodeAnalysis.Compilation compilation)
    {
        using var stream = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "生成物编译失败：" + string.Join(Environment.NewLine, emit.Diagnostics
                    .Where(static d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    .Select(static d => d.ToString())));
        }

        stream.Position = 0;
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            string typeName = reader.GetString(type.Name);
            if (!string.Equals(typeName, "PalORM_RegistryInitializer", StringComparison.Ordinal))
                continue;

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue; // 无方法体（理论上的 abstract，此处不会出现）
                // GetMethodBody 的可空性来自 API 签名，RVA 非零即一定有方法体
                MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress)
                    ?? throw new InvalidOperationException($"RVA={method.RelativeVirtualAddress} 无可读方法体");
                sizes[reader.GetString(method.Name)] = body.GetILContent().Length;
            }
        }
        return sizes;
    }

    /// <summary>注册初始化器的**全部方法**的 JIT 编译耗时之和。
    /// 只量 Initialize 会随分块与否失去可比性（分块后它只剩调用语句，真正的方法体在块方法里），
    /// 因此口径定为"整类所有方法"——分块前后的差值才是分块自身的收益。
    /// 用 PrepareMethod 只编译不执行（执行会真的往全局注册表写 500 个假实体，污染同进程其他测试）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3011:AccessibilityBypass",
        Justification = "生成器产出的初始化器是 internal，只能经反射取到；这里只做 "
            + "PrepareMethod（纯 JIT 编译）不执行，不触碰任何状态，也不绕过任何安全边界。")]
    private static void MeasureJitCost(Microsoft.CodeAnalysis.Compilation compilation)
    {
        using var stream = new MemoryStream();
        compilation.Emit(stream);
        stream.Position = 0;
        System.Reflection.Assembly assembly = System.Reflection.Assembly.Load(stream.ToArray());
        Type? type = assembly.GetType("PalORM.Generated.PalORM_RegistryInitializer");
        System.Reflection.MethodInfo[] methods = type is null
            ? []
            : [.. type.GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)];
        if (methods.Length == 0)
        {
            Log("[规模量具] 未找到注册初始化器——JIT 耗时未测");
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (System.Reflection.MethodInfo method in methods)
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
        sw.Stop();
        Log(string.Create(CultureInfo.InvariantCulture,
            $"[规模量具] 注册初始化器全部 {methods.Length} 个方法 JIT 合计={sw.Elapsed.TotalMilliseconds:F1} ms"));
    }

    /// <summary>测试诊断输出——经参数而非字面量传给 Console（CA1303 只认字面量实参）。</summary>
    private static void Log(string message) => Console.WriteLine(message);

    /// <summary>合成 N 个同构实体——1 主键 + (columnCount - 1) 个值列。
    /// 列数是第二个维度：注册项体量随列数增长，切块必须对列宽也不敏感。</summary>
    private static string BuildEntitySource(int count, int columnCount)
    {
        const string usingDirectives = """
            using PalORM;

            """;
        var builder = new StringBuilder(usingDirectives);
        for (int i = 0; i < count; i++)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"[Table(\"scale_entity_{i}\")]");
            builder.AppendLine(CultureInfo.InvariantCulture, $"public sealed partial class ScaleEntity{i}");
            builder.AppendLine("{");
            builder.AppendLine("    [Key] public long Id { get; set; }");
            for (int c = 1; c < columnCount; c++)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"    [Column(\"col_{c}\")] public long Col{c} {{ get; set; }}");
            }
            builder.AppendLine("}");
            builder.AppendLine();
        }
        builder.AppendLine(CultureInfo.InvariantCulture, $"internal static class ScaleAnchor{{ public const int Count = {count}; }}");
        return builder.ToString();
    }
}
