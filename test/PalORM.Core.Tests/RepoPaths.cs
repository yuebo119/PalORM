namespace PalORM.Core.Tests;

/// <summary>源码扫描类测试的仓库根定位。
/// <para><b>为什么需要回退</b>：这些测试用 <c>[CallerFilePath]</c> 拿到编译期源码路径，
/// 再上溯到仓库根。常规构建下有效；但变异测试（Stryker）会把被测项目源码复制到临时目录
/// 重新编译，编译期路径不存在，测试首跑即失败，Stryker 判定"初始测试运行失败"后整轮中止。
/// 回退顺序：编译期路径（存在则用）→ 从 <see cref="AppContext.BaseDirectory"/> 上溯找
/// <c>PalORM.slnx</c>。两处都找不到时响亮失败，不静默扫 0 个文件。</para></summary>
internal static class RepoPaths
{
    /// <summary>仓库根。传调用方 <c>[CallerFilePath]</c> 的值。</summary>
    public static string Root(string compileTimeFilePath)
    {
        // 变异测试（Stryker）把项目复制到仓库外的临时目录重编译：编译期路径与 BaseDirectory
        // 上溯都可能失效。runner 以 PALORM_REPO_ROOT 显式传入真实仓库根。
        string? explicitRoot = Environment.GetEnvironmentVariable("PALORM_REPO_ROOT");
        if (!string.IsNullOrEmpty(explicitRoot) && File.Exists(Path.Combine(explicitRoot, "PalORM.slnx")))
            return explicitRoot;

        string? fromCaller = TryFromCompileTimePath(compileTimeFilePath);
        if (fromCaller is not null) return fromCaller;

        string dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "PalORM.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
        throw new InvalidOperationException(
            $"无法定位仓库根：编译期路径 '{compileTimeFilePath}' 与 BaseDirectory 上溯均未找到 PalORM.slnx");
    }

    /// <summary>测试源码目录（test/&lt;项目&gt;）。</summary>
    public static string CoreTestsDirectory(string compileTimeFilePath)
    {
        if (!string.IsNullOrEmpty(compileTimeFilePath) && Directory.Exists(Path.GetDirectoryName(compileTimeFilePath)))
            return Path.GetDirectoryName(compileTimeFilePath)!;
        return Path.Combine(Root(compileTimeFilePath), "test", "PalORM.Core.Tests");
    }

    private static string? TryFromCompileTimePath(string compileTimeFilePath)
    {
        if (string.IsNullOrEmpty(compileTimeFilePath)) return null;
        string? directory = Path.GetDirectoryName(compileTimeFilePath);
        if (directory is null || !Directory.Exists(directory)) return null;
        // test/<proj>/<file> → 上两级是仓库根
        string root = Path.GetFullPath(Path.Combine(directory, "..", ".."));
        return File.Exists(Path.Combine(root, "PalORM.slnx")) ? root : null;
    }
}