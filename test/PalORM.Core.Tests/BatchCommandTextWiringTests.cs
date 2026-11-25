using System.Runtime.CompilerServices;

namespace PalORM.Core.Tests;

/// <summary>P0-1 接线守卫：<c>src/PalORM.Core</c> 内每处写入 <c>DbBatchCommand.CommandText</c>
/// 的赋值都必须经过 <see cref="BatchCommandText"/>。<b>漏接一处</b>即该入口在 MySQL 上仍会
/// 退化为逐条发送（实测 100 条批 3.39 ms → 26.85 ms、产品管道比不用管道慢 21%），
/// 而运行期没有任何信号——因此用静态扫描把"漏点"变成编译后必跑的失败。
/// <para><b>扫描手法</b>：定位每个 <c>CreateBatchCommand()</c> 调用行，在其后
/// <see cref="AssignmentWindowLines"/> 行窗口内找 <c>.CommandText =</c> 赋值，断言同行含
/// <c>BatchCommandText.For(</c>。窗口取 12 行以容忍夹在中间的说明性注释。
/// 匹配前剥掉行注释——否则注释里的示例文本会把"文档提到"判成"代码在用"（B13 教训）。</para>
/// <para><b>为什么是静态扫描而非行为测试</b>：真 <c>DbBatch</c> 只存在于 MySQL/PG 驱动下，
/// Core.Tests 没有能 <c>CreateBatch</c> 的离线连接（Microsoft.Data.Sqlite 不覆写该成员）。
/// 行为级证据在探针（修复前后字节数与耗时对比）与 Integration 真库档；本用例只负责机械防漏点。</para></summary>
internal sealed class BatchCommandTextWiringTests
{
    /// <summary>赋值搜索窗口（行）：容纳 <c>CreateBatchCommand()</c> 与赋值之间的注释行。</summary>
    private const int AssignmentWindowLines = 12;

    /// <summary>已知批命令文本写入点的数量地板：QueryPipeline、SessionBatch、
    /// 批分页 COUNT 与页查询、批量更新 DbBatch 逐命令。低于此数说明扫描本身失效
    /// （例如源码目录定位错了），不是"接线更干净"。</summary>
    private const int ExpectedAssignmentFloor = 5;

    /// <summary>源码文件数地板：低于此值视为源码目录定位失效（避免"扫不到文件 = 零违规"的假绿）。</summary>
    private const int MinimumSourceFiles = 20;

    [Test]
    public async Task EveryBatchCommandTextAssignment_GoesThroughBatchCommandText()
    {
        (int assignments, List<string> violations) = ScanBatchCommandTextAssignments(FindCoreDir());

        await Assert.That(assignments).IsGreaterThanOrEqualTo(ExpectedAssignmentFloor);
        await Assert.That(string.Join("\n", violations)).IsEmpty();
    }

    /// <summary>定位 src/PalORM.Core（[CallerFilePath] 与 ArchitectureInvariantTests 同源手法：
    /// 放在私有方法上，避免 TUnit 把带参的 [Test] 当作数据驱动用例）。</summary>
    private static string FindCoreDir([CallerFilePath] string testPath = "")
        => Path.Combine(RepoPaths.Root(testPath), "src", "PalORM.Core");

    /// <summary>同步扫描（文件读取留在非 async 方法内，避免 CA1849/S6966）。
    /// 源文件数少于此值即判定位失效——扫描不到文件时"零违规"是假绿，必须响亮失败。</summary>
    private static (int Assignments, List<string> Violations) ScanBatchCommandTextAssignments(
        string coreDir)
    {
        string[] files = Directory.GetFiles(coreDir, "*.cs", SearchOption.TopDirectoryOnly);
        if (files.Length <= MinimumSourceFiles)
        {
            throw new InvalidOperationException(
                $"Core 源码定位异常：'{coreDir}' 只找到 {files.Length} 个 .cs 文件（预期超过 {MinimumSourceFiles}）。");
        }

        var violations = new List<string>();
        int assignments = 0;
        foreach (string file in files)
            ScanFile(file, violations, ref assignments);
        return (assignments, violations);
    }

    /// <summary>单文件扫描：每个 <c>CreateBatchCommand()</c> 之后的赋值窗口内，
    /// <c>.CommandText =</c> 必须经过 <see cref="BatchCommandText"/>。</summary>
    private static void ScanFile(string file, List<string> violations, ref int assignments)
    {
        string[] lines = File.ReadAllLines(file);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!StripLineComment(lines[i]).Contains("CreateBatchCommand()", StringComparison.Ordinal))
                continue;

            int last = Math.Min(lines.Length - 1, i + AssignmentWindowLines);
            for (int j = i + 1; j <= last; j++)
            {
                string code = StripLineComment(lines[j]);
                if (!code.Contains(".CommandText =", StringComparison.Ordinal))
                    continue;
                assignments++;
                if (!code.Contains("BatchCommandText.For(", StringComparison.Ordinal))
                    violations.Add($"{Path.GetFileName(file)}:{j + 1}: {code.Trim()}");
            }
        }
    }

    /// <summary>剥行注释后再参与匹配（B13）：整行注释与代码行内 <c>//</c> 之后都不算代码。
    /// 字符串字面量里的 <c>//</c> 会被误截，但本扫描的两个模式（<c>CreateBatchCommand()</c>、
    /// <c>.CommandText =</c>）不会出现在 URL 之类的字面量里，无影响。</summary>
    private static string StripLineComment(string line)
    {
        int index = line.IndexOf("//", StringComparison.Ordinal);
        return index >= 0 ? line[..index] : line;
    }
}
