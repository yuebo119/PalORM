using System.Runtime.CompilerServices;

namespace PalORM.Core.Tests;

/// <summary>
/// 外部库测试隔离不变式（B63 家族的机械防线，2026-10-04）。
/// 不变式：Integration 套件里每个标记 <c>[Property("Category", "ExternalDatabase")]</c> 的
/// 测试（类或方法），其自身或所属类的属性行组必须带 <c>[NotInParallel("ExtBulkTable")]</c>。
/// <para><b>为什么键必须是 ExtBulkTable（且只有一个键）</b>：PG/MySQL 是共享库，全部外部库
/// 用例争同一组系统目录与同一批表——<c>[NotInParallel]</c> 只对<b>同键</b>测试互斥，出现第二个
/// 键就等于放行了跨键并行，竞态原样回来。因此本测试断言的是字面量键，不是"有守卫就行"。</para>
/// <para><b>背景</b>：B63（pg_type/pg_class 23505 竞态）登记后仍复发——2026-10-04 实测 20 个
/// 引用该标记的类里 5 个缺守卫（其中一个仅注释提及属误报源），表现为失败项每轮不同
/// （42P07 relation "pg_test" already exists / 42710 type "merge_entities" already exists），
/// 3 跑 2 失败。人工补齐（f99e1ae）后连跑 4 次全绿；本测试把"新增外部库用例必须带守卫"
/// 从纪律变成编译产物级的机械检查。</para>
/// <para><b>形态依据</b>：Integration 夹具的属性是单行形态，类级与方法级两种；守卫与标记
/// 不一定相邻（中间可隔 [Test] 等），故按"属性行组 + 其后第一条声明行"判定归属，
/// 与 <see cref="ArchitectureInvariantTests"/> 同为源码文本扫描（T2 白盒豁免同口径）。</para>
/// </summary>
public sealed class ExternalDbIsolationInvariantTests
{
    [Test]
    public async Task ExternalDatabaseTests_CarrySharedSerialGuard()
    {
        string dir = IntegrationTestsDirectory();
        string[] files = Directory.GetFiles(dir, "*.cs");
        // 路径导航失败必须响亮失败，不能静默扫到 0 个文件然后空过（B123 零匹配假绿族）
        await Assert.That(files.Length).IsGreaterThanOrEqualTo(15)
            .Because($"Integration 测试源应 ≥15 个文件，实际 {files.Length}——目录定位可疑：{dir}");

        var violations = new List<string>();
        int markers = 0;
        foreach (string file in files.OrderBy(static f => f, StringComparer.Ordinal))
        {
            var scanner = new Scanner(Path.GetFileName(file));
            foreach (string raw in await File.ReadAllLinesAsync(file).ConfigureAwait(false))
            {
                scanner.Line(raw.Trim());
            }
            markers += scanner.Markers;
            violations.AddRange(scanner.Violations);
        }

        await Assert.That(markers).IsGreaterThanOrEqualTo(40)
            .Because($"外部库标记应 ≥40 处，实际 {markers}——扫描判据可能失效");
        await Assert.That(string.Join("\n", violations)).IsEmpty()
            .Because("带 ExternalDatabase 标记的测试必须与同键守卫同块出现（类或方法级），"
                + "否则共享库并行 DDL 竞态复发（B63 家族）");
    }

    private static string IntegrationTestsDirectory([CallerFilePath] string testPath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!, "..", "PalORM.Integration.Tests"));

    /// <summary>单文件扫描器：累积连续属性行，遇声明行结算；类块记录"本类是否带守卫"，
    /// 方法块可由自身或所属类满足守卫。外部库标记命中时计数（供测试侧做覆盖下限断言）。</summary>
    private sealed class Scanner
    {
        private const string ExternalMarker = """Property("Category", "ExternalDatabase")""";
        private const string RequiredGuard = """[NotInParallel("ExtBulkTable")]""";

        private readonly string _name;
        private readonly List<string> _pending = [];
        private bool _classHasGuard;

        internal int Markers { get; private set; }
        internal List<string> Violations { get; } = [];

        internal Scanner(string name)
        {
            _name = name;
        }

        internal void Line(string line)
        {
            if (line.Length == 0
                || line.StartsWith('/', StringComparison.Ordinal))
            {
                return;
            }

            if (line[0] == '[' && line.EndsWith(']', StringComparison.Ordinal))
            {
                _pending.Add(line);
                return;
            }

            // 非属性、非空、非注释 = 声明行（class/方法/字段/语句）。语句行无属性前导，
            // 结算成空块无害——属性只挂在声明前。
            if (_pending.Count > 0)
            {
                Settle(line);
            }
        }

        private void Settle(string declaration)
        {
            bool blockGuard = _pending.Exists(HasGuard);
            bool blockExternal = _pending.Exists(IsExternal);
            if (blockExternal)
            {
                Markers++;
            }

            if (declaration.Contains(" class ", StringComparison.Ordinal))
            {
                _classHasGuard = blockGuard;
                if (blockExternal && !blockGuard)
                {
                    Violations.Add($"{_name}: 类级 ExternalDatabase 缺 {RequiredGuard}");
                }
            }
            else if (blockExternal && !blockGuard && !_classHasGuard)
            {
                Violations.Add(
                    $"{_name}: 方法级 ExternalDatabase（{Truncate(declaration)}）缺 {RequiredGuard}");
            }

            _pending.Clear();
        }

        private static bool HasGuard(string attribute)
            => attribute.Contains(RequiredGuard, StringComparison.Ordinal);

        private static bool IsExternal(string attribute)
            => attribute.Contains(ExternalMarker, StringComparison.Ordinal);

        private static string Truncate(string line)
            => line.Length <= 60 ? line : line[..60] + "...";
    }
}
