// test-package-contract.cs（自 test-package-contract.sh 迁移，脚本 C# 化整改方案 T3-3）
// 验证最终 NuGet 包的依赖契约，而不是源码 ProjectReference 假象。
// 仅验证公开发布的包（PalORM.Testing 是内部测试库，IsPackable=false，不在验证范围）。
// 版本从 Directory.Build.props 动态读取（与包版本同源，避免硬编码漂移）。
using System.Text;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var repoRoot = FindRepoRoot();
Environment.CurrentDirectory = repoRoot;

// 从 Directory.Build.props 动态读取版本号（POSIX sed 等价：取首个 <Version> 匹配）
var propsText = File.ReadAllText("Directory.Build.props");
var versionMatch = new Regex("<Version>([^<]*)</Version>").Match(propsText);
if (!versionMatch.Success)
{
    Console.Error.WriteLine("FAIL 无法从 Directory.Build.props 读取 Version");
    return 1;
}
var version = versionMatch.Groups[1].Value;
Console.WriteLine($"INFO 检测到版本 {version}");

var tmp = Path.Combine(Path.GetTempPath(), "pk-contract-" + Guid.NewGuid().ToString("N")[..8]);
try
{
    var packages = Path.Combine(tmp, "packages");
    var consumerDir = Path.Combine(tmp, "analyzer-consumer");
    Directory.CreateDirectory(packages);
    Directory.CreateDirectory(consumerDir);

    // 打包公开发布的 5 个项目（Testing 是内部库不打包）；pack 输出与 .sh 同样静默（>/dev/null）
    foreach (var project in new[] { "Core", "SourceGen", "Sqlite", "PostgreSql", "MySql" })
    {
        var code = RunDotnet($"pack src/PalORM.{project}/PalORM.{project}.csproj -c Release -o \"{packages}\" --nologo",
            inheritStdout: false, inheritStderr: true);
        if (code != 0)
        {
            return code;
        }
    }

    File.WriteAllText(Path.Combine(consumerDir, "AnalyzerConsumer.csproj"), $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net11.0</TargetFramework>
            <OutputType>Exe</OutputType>
            <ImplicitUsings>disable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="PalORM.Core" Version="{version}" />
            <PackageReference Include="PalORM.SourceGen" Version="{version}"
                              OutputItemType="Analyzer"
                              ReferenceOutputAssembly="false" />
          </ItemGroup>
        </Project>
        """, new UTF8Encoding(false));

    // 评审 2026-09-02 第二批：SqlFile 包契约用例——包内 buildTransitive targets 必须自动把
    // **/*.sql 注入为 AdditionalFiles（否则生成器拿不到内容，partial 方法缺实现 → CS8795）。
    File.WriteAllText(Path.Combine(consumerDir, "Models.cs"), """
        [global::PalORM.Table("root_users")]
        public sealed partial class RootUser
        {
            [global::PalORM.Key]
            public long Id { get; set; }
            [global::PalORM.Column("name")] public string Name { get; set; } = "";
        }

        namespace Alpha
        {
            [global::PalORM.Table("alpha_users")]
            public sealed partial class User
            {
                [global::PalORM.Key]
                public long Id { get; set; }
                [global::PalORM.Column("name")] public string Name { get; set; } = "";
            }
        }

        namespace Beta
        {
            [global::PalORM.Table("beta_users")]
            public sealed partial class User
            {
                [global::PalORM.Key]
                public long Id { get; set; }
                [global::PalORM.Column("name")] public string Name { get; set; } = "";
            }
        }
        """, new UTF8Encoding(false));

    Directory.CreateDirectory(Path.Combine(consumerDir, "Queries"));
    File.WriteAllText(Path.Combine(consumerDir, "Queries", "Ping.sql"), "SELECT 1 AS ping;\n", new UTF8Encoding(false));

    File.WriteAllText(Path.Combine(consumerDir, "SqlFileQueries.cs"), """
        namespace FileQueries
        {
            public static partial class Ping
            {
                [global::PalORM.SqlFile("Queries/Ping.sql")]
                public static partial string Get();
            }
        }
        """, new UTF8Encoding(false));

    File.WriteAllText(Path.Combine(consumerDir, "Program.cs"), """
        internal static class Program
        {
            private static int Main()
            {
                global::System.Type[] expected =
                [
                    typeof(global::RootUser),
                    typeof(global::Alpha.User),
                    typeof(global::Beta.User)
                ];
                foreach (global::System.Type type in expected)
                {
                    if (!global::PalORM.PalORM_Runtime.TableNames.ContainsKey(type))
                        return 1;
                }

                // SqlFile 包契约：包 targets 注入 AdditionalFiles → 生成器嵌入 .sql 内容
                // （未注入时 partial 无实现，编译期 CS8795 直接失败，到不了这里）
                string ping = global::FileQueries.Ping.Get();
                if (!ping.Contains("SELECT 1 AS ping", global::System.StringComparison.Ordinal))
                    return 3;

                return expected[1] == expected[2] ? 2 : 0;
            }
        }
        """, new UTF8Encoding(false));

    // .sh 版经 cygpath 转 Windows 路径；C# 直接产出原生路径，无需转换
    var nugetConfig = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="{tmp}\global-packages" />
          </config>
          <packageSources>
            <clear />
            <add key="local" value="{packages}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="local">
              <package pattern="PalORM.*" />
            </packageSource>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """;
    File.WriteAllText(Path.Combine(consumerDir, "NuGet.config"), nugetConfig, new UTF8Encoding(false));

    var exitCode = RunDotnet(
        $"run --project \"{consumerDir}/AnalyzerConsumer.csproj\" -c Release --configfile \"{consumerDir}/NuGet.config\" --nologo",
        inheritStdout: true, inheritStderr: true);
    if (exitCode != 0)
    {
        return exitCode;
    }
    Console.WriteLine("PASS SourceGen 包加载、禁用隐式 using、实体身份契约与 SqlFile AdditionalFiles 注入");
    return 0;
}
finally
{
    // 等价 .sh 的 trap 'rm -rf "$TMP"' EXIT
    try
    {
        Directory.Delete(tmp, recursive: true);
    }
    catch (IOException)
    {
    }
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    Console.Error.WriteLine("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}

static int RunDotnet(string arguments, bool inheritStdout, bool inheritStderr)
{
    var psi = new System.Diagnostics.ProcessStartInfo("dotnet", arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = !inheritStdout,
        RedirectStandardError = !inheritStderr,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    // 重定向流必须异步排空再 Wait，否则缓冲写满互等死锁
    var stdoutTask = !inheritStdout ? p.StandardOutput.ReadToEndAsync() : null;
    var stderrTask = !inheritStderr ? p.StandardError.ReadToEndAsync() : null;
    p.WaitForExit();
    if (stdoutTask is not null && !inheritStdout)
    {
        // 场景约定：stdout 静默即丢弃（.sh 的 >/dev/null）；stderr 未重定向则已直通
        _ = stdoutTask.Result;
    }
    if (stderrTask is not null)
    {
        Console.Error.Write(stderrTask.Result);
    }
    return p.ExitCode;
}
