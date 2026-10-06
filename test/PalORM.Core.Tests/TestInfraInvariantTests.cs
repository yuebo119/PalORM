using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PalORM.Core.Tests;

/// <summary>
/// 测试基建不变式（B74 家族机械防线，2026-10-06 release.yml 发布门禁两次拦截催生）。
/// 不变式：静态语句采集钩子（<c>ArrayAnySqliteCommand.SetStatementRecorder</c>，
/// last-write-wins）的每个捕获窗口（对采集列表 Clear）必须由本方法体自注册、或调用
/// "注册型方法"（方法体含 SetStatementRecorder 的工厂/包装）打开——同组 NotInParallel
/// 只保证互斥不保证执行顺序，依赖"上一例恰好注册过"的全局钩子状态即跨用例竞态。
/// <para>实证：BulkUpdateArrayFormTests 的 AllNull/MixedNulls 两用例 inline 建会话未自
/// 注册，本地全绿、CI 确定性红（钩子被 Delete 侧用例抢走，产品断言全过、仅计数断言
/// 抓空）。形态与 ArchitectureInvariantTests 同源：源码文本守卫（不变式在类型系统/
/// 运行时层面不可表达），方法体提取沿用其 ITM-413 花括号配对语义（含同款局限：
/// 字符串字面量内不平衡花括号会干扰配对，被扫描文件已核无此形态）。</para>
/// </summary>
internal sealed class TestInfraInvariantTests
{
    /// <summary>采集钩子使用文件登记表（新增使用方必须登记；反向守卫防漏登）。</summary>
    private static readonly string[] _recorderHookFiles =
    [
        "BulkUpdateArrayFormTests.cs",
        "BulkDeleteArrayFormTests.cs",
    ];

    private static readonly string SourceDirectory = GetSourceDirectory();

    [Test]
    public async Task CaptureWindows_OwnTheStatementRecorderHook()
    {
        var violations = new List<string>();
        foreach (string file in _recorderHookFiles)
        {
            string source = ReadStripped(file);
            // 采集列表名（形态锚点：private static readonly List<...> X = [];）
            string[] listNames = [.. Regex.Matches(
                    source,
                    @"private\s+static\s+readonly\s+List<[^>]*>\s+(\w+)\s*=\s*\[")
                .Select(static m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)];
            if (listNames.Length == 0)
            {
                violations.Add($"{file}: 未找到静态采集列表声明——登记表与实现脱同步，守卫失明");
                continue;
            }

            // 注册型方法：方法体直接含 SetStatementRecorder（工厂方法或自注册用例）
            List<(string Name, string Body)> methods = ExtractAllMethods(source);
            HashSet<string> owners = methods
                .Where(static m => m.Body.Contains("SetStatementRecorder", StringComparison.Ordinal))
                .Select(static m => m.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach ((string name, string body) in methods)
            {
                foreach (string list in listNames)
                {
                    if (!body.Contains($"{list}.Clear()", StringComparison.Ordinal))
                        continue;
                    bool owned =
                        body.Contains("SetStatementRecorder", StringComparison.Ordinal)
                        || owners.Any(owner => body.Contains($"{owner}(", StringComparison.Ordinal));
                    if (!owned)
                        violations.Add(
                            $"{file}/{name}: 打开 {list} 捕获窗口（Clear）但方法体既未自注册钩子也未调用注册型方法"
                            + "——静态 recorder 是 last-write-wins，捕获窗口依赖上一例的注册即跨用例竞态（B74 家族）");
                }
            }
        }

        await Assert.That(string.Join("\n", violations)).IsEmpty();
    }

    [Test]
    public async Task RecorderHookFiles_AreAllRegistered()
    {
        // 反向守卫：测试目录出现新的 SetStatementRecorder 使用文件而未登记 → 失败。
        // 本文件自身含该标识符文本（守卫代码），豁免。
        string[] unregistered =
        [
            .. Directory.GetFiles(SourceDirectory, "*.cs")
                .Select(static f => Path.GetFileName(f))
                .Where(static f => f != "TestInfraInvariantTests.cs")
                .Where(f => !_recorderHookFiles.Contains(f))
                .Where(f => ReadStripped(f).Contains("SetStatementRecorder", StringComparison.Ordinal))
                .OrderBy(static f => f, StringComparer.Ordinal),
        ];

        await Assert.That(string.Join(", ", unregistered)).IsEmpty()
            .Because("新增语句采集钩子使用文件必须登记进 TestInfraInvariantTests._recorderHookFiles"
                + "（否则捕获窗口守卫对该文件失明）");
    }

    private static string GetSourceDirectory([CallerFilePath] string path = "")
        => Path.GetDirectoryName(path)
           ?? throw new InvalidOperationException("无法定位测试源码目录（CallerFilePath 为空）");

    /// <summary>读被守卫文件源码（同步私有助手：与 ArchitectureInvariantTests.Read* 同形态，
    /// 读取点不落在 async 方法体直接调用面上）。</summary>
    private static string ReadSource(string file)
        => File.ReadAllText(Path.Combine(SourceDirectory, file));

    /// <summary>读源码并剥离注释（B13 纪律，见 StripComments）。</summary>
    private static string ReadStripped(string file)
        => StripComments(ReadSource(file));

    /// <summary>剥离块注释（保留其占用的换行数以维持行结构）与行注释。B13：统计类守卫
    /// 先剥注释，防"注释里写着守卫标识符"的假绿——注释掉的 SetStatementRecorder 不得
    /// 算作注册。已知局限：字符串字面量内的 // 或 /* 会被误剥（被扫描文件已核无该形态）。</summary>
    private static string StripComments(string source)
    {
        string withoutBlock = Regex.Replace(
            source,
            @"/\*.*?\*/",
            static m => new string('\n', m.Value.Count(static c => c == '\n')),
            RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//[^\n]*", string.Empty);
    }

    /// <summary>枚举源码内全部方法体：签名行锚定（行首缩进 + 修饰符 + 名字后紧跟左括号；
    /// 字段声明名字后无括号天然不匹配，主构造类头误中无害）+ 花括号配对取体（ITM-413
    /// 语义：表达式体取到首个顶层分号，字符串字面量内不平衡花括号属已接受局限）。</summary>
    private static List<(string Name, string Body)> ExtractAllMethods(string source)
    {
        var methods = new List<(string Name, string Body)>();
        foreach (Match signature in Regex.Matches(
                     source,
                     @"^[ \t]*(?:public|private|internal|protected)[^\n(]*\b(\w+)\s*\(",
                     RegexOptions.Multiline))
        {
            int end = FindBodyEnd(source, signature.Index + signature.Length - 1);
            if (end > 0)
                methods.Add((signature.Groups[1].Value, source[signature.Index..end]));
        }

        return methods;
    }

    /// <summary>从签名行的 '(' 起定位方法体结束下标：花括号配对（ITM-413 语义），
    /// 表达式体取到首个顶层分号；未找到闭合返回 -1。</summary>
    private static int FindBodyEnd(string source, int cursor)
    {
        int depth = 0;
        bool entered = false;
        for (; cursor < source.Length; cursor++)
        {
            char current = source[cursor];
            if (!entered && current == ';' && depth == 0)
                return cursor;
            if (current == '{')
            {
                depth++;
                entered = true;
            }
            else if (current == '}')
            {
                depth--;
                if (entered && depth == 0)
                    return cursor + 1;
            }
        }

        return -1;
    }
}
