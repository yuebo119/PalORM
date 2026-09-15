using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>测试环境读取器的纯函数单测——不触库、不需要外部凭据。
/// <para><b>为什么需要</b>：v5.6 起 <c>TestEnvironment</c> 会在解析连接串前自动从仓库根
/// <c>.env.test</c> 补入缺失的 <c>PALORM_*</c> 变量。该兜底路径此前只能靠手工
/// <c>source scripts/set-test-env.sh</c> 生效，漏做时失败信息是"环境变量未设置"，
/// 容易被误读为"没有可用数据库实例"——实际发生过一次误判。</para></summary>
public sealed class TestEnvironmentDotEnvTests
{
    /// <summary>仓库根一定有、且不在输出目录的标记文件（被复制到输出目录的
    /// <c>appsettings.test.json</c> 会掩盖回溯层数，不能用作基准）。</summary>
    private const string RootMarkerFile = "global.json";

    [Test]
    public async Task FindFileUpwards_DoesNotWasteAnIterationOnTrailingSeparator()
    {
        // 独立基准：搭一棵已知层数的临时目录树，标记文件放在第 4 层祖先上，
        // 且起点刻意以目录分隔符结尾——正是 AppContext.BaseDirectory 的形态。
        //   起点      = <tmp>/L3/L2/L1            （第 1 次迭代检查它自己）
        //   第 2 次   = <tmp>/L3/L2
        //   第 3 次   = <tmp>/L3
        //   第 4 次   = <tmp>   ← marker.txt 在这里
        // 于是"恰好 4 层"是可由目录结构直接读出的 ground truth，与实现无关。
        // 白耗一次迭代的实现会在第 4 次检查 <tmp>/L3 而落空，需要 5 次才命中。
        string root = Path.Combine(Path.GetTempPath(), "palorm-depth-" + Guid.NewGuid().ToString("N"));
        string start = Path.Combine(root, "L3", "L2", "L1");
        const string marker = "marker.txt";
        try
        {
            _ = Directory.CreateDirectory(start);
            await File.WriteAllTextAsync(Path.Combine(root, marker), "x");

            // 带尾部分隔符的起点（与 AppContext.BaseDirectory 同形）
            string startWithSeparator = start + Path.DirectorySeparatorChar;
            await Assert.That(startWithSeparator.EndsWith(Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsTrue();

            const int exactDepth = 4;
            await Assert.That(TestEnvironment.FindFileUpwards(marker, exactDepth, startWithSeparator))
                .IsNotNull()
                .Because("4 次迭代应恰好到达 <tmp>；返回 null 说明尾部分隔符白耗了一层");

            // 少一层必须落空——否则上面的"恰好"不成立，断言失去区分力
            await Assert.That(TestEnvironment.FindFileUpwards(marker, exactDepth - 1, startWithSeparator)).IsNull();
            // 多一层仍应命中，但返回的必须是同一个文件（不因余量而漂到别处）
            await Assert.That(TestEnvironment.FindFileUpwards(marker, exactDepth + 1, startWithSeparator))
                .IsEqualTo(TestEnvironment.FindFileUpwards(marker, exactDepth, startWithSeparator));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task FindFileUpwards_ReachesRepositoryRoot()
    {
        // 冒烟：真实起点（AppContext.BaseDirectory）能按配置的深度上限找到仓库根标记文件。
        // 不断言绝对层数——那会把测试绑死在 bin/<cfg>/<tfm> 的目录形状上。
        string? found = TestEnvironment.FindFileUpwards(RootMarkerFile);
        await Assert.That(found).IsNotNull();
        await Assert.That(Path.GetFileName(found!)).IsEqualTo(RootMarkerFile);
    }

    [Test]
    public async Task FindFileUpwards_MissingFile_ReturnsNull()
    {
        await Assert.That(TestEnvironment.FindFileUpwards("no-such-file-9f3a2b.env")).IsNull();
    }

    [Test]
    public async Task ParseDotEnv_SkipsCommentsBlankAndMalformedLines()
    {
        string[] lines =
        [
            "# 注释行",
            "",
            "   ",
            "NO_EQUALS_SIGN",
            "=VALUE_WITHOUT_KEY",
            "PALORM_KEEP=1",
        ];

        List<(string Key, string Value)> parsed = [.. TestEnvironment.ParseDotEnv(lines)];

        await Assert.That(parsed.Count).IsEqualTo(1);
        await Assert.That(parsed[0].Key).IsEqualTo("PALORM_KEEP");
        await Assert.That(parsed[0].Value).IsEqualTo("1");
    }

    [Test]
    public async Task ParseDotEnv_StripsSurroundingQuotes()
    {
        string[] lines =
        [
            "PALORM_A=\"Host=h;Port=1\"",
            "PALORM_B='single'",
            "PALORM_C=bare",
            "PALORM_D=\"unbalanced",
        ];

        List<(string Key, string Value)> parsed = [.. TestEnvironment.ParseDotEnv(lines)];

        await Assert.That(parsed.Count).IsEqualTo(4);
        await Assert.That(parsed[0].Value).IsEqualTo("Host=h;Port=1");
        await Assert.That(parsed[1].Value).IsEqualTo("single");
        await Assert.That(parsed[2].Value).IsEqualTo("bare");
        // 不成对引号原样保留——不猜测意图，避免把引号咽掉后拼出半截连接串
        await Assert.That(parsed[3].Value).IsEqualTo("\"unbalanced");
    }

    [Test]
    public async Task ParseDotEnv_IgnoresNonPalormKeys()
    {
        string[] lines =
        [
            "PATH=/usr/bin",
            "AWS_SECRET_ACCESS_KEY=redacted",   // 值刻意短于 20 字符，避免触发 secret-guard 的 API Key 规则
            "PALORM_OK=yes",
        ];

        List<(string Key, string Value)> parsed = [.. TestEnvironment.ParseDotEnv(lines)];

        // 只接受 PALORM_ 前缀——该文件是测试配置载体，不应成为注入任意进程环境变量的通道
        await Assert.That(parsed.Count).IsEqualTo(1);
        await Assert.That(parsed[0].Key).IsEqualTo("PALORM_OK");
    }
}
