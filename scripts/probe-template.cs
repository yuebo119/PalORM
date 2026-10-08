// probe-template.cs（自 .ai/scripts/probe-template.sh 迁移，脚本全面 C# 化轮）
// 探针骨架生成器——review 系统 probe-first 的基建。
// 用法: dotnet run --file scripts/probe-template.cs -- <探针名> [sqlite|pg|mysql]
// 生成 %TEMP%/palorm-probe-<名>/ 最小工程（引用本仓库 Core+Provider+SourceGen），
// 写 Program.cs 后 dotnet run 即可。成本 ~30 秒，替代每次手搓（~5 分钟）。
// 契约：0=生成完成；1=未知 provider；2=缺参；csproj 引用为 Windows 原生路径（.sh 版 MSYS 路径的修正）。
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length < 1)
{
    Console.Error.WriteLine("用法: probe-template.cs <探针名> [sqlite|pg|mysql]");
    return 2;
}
var name = args[0];
var provider = args.Length > 1 ? args[1] : "sqlite";

string prj, usingNs, prov, cs;
switch (provider)
{
    case "sqlite":
        (prj, usingNs, prov, cs) = ("PalORM.Sqlite", "PalORM.Sqlite", "SqliteProvider", @"Data Source=:memory:");
        break;
    case "pg":
        (prj, usingNs, prov, cs) = ("PalORM.PostgreSql", "PalORM.PostgreSql", "PostgreSqlProvider", "$ENV:PALORM_PG_CONNECTION");
        break;
    case "mysql":
        (prj, usingNs, prov, cs) = ("PalORM.MySql", "PalORM.MySql", "MySqlProvider", "$ENV:PALORM_MYSQL_CONNECTION");
        break;
    default:
        Console.WriteLine($"未知 provider: {provider}（sqlite|pg|mysql）");
        return 1;
}

var repo = FindRepoRoot();
var dir = Path.Combine(Path.GetTempPath(), $"palorm-probe-{name}");
Directory.CreateDirectory(dir);

File.WriteAllText(Path.Combine(dir, "probe.csproj"), $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <AnalysisLevel>none</AnalysisLevel>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{repo}/src/PalORM.Core/PalORM.Core.csproj" />
    <ProjectReference Include="{repo}/src/{prj}/{prj}.csproj" />
    <ProjectReference Include="{repo}/src/PalORM.SourceGen/PalORM.SourceGen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
""", new UTF8Encoding(false));

var programPath = Path.Combine(dir, "Program.cs");
if (!File.Exists(programPath))
{
    // 模板含大量 C# 大括号，用占位符 + Replace 而非内插 raw string（避免 {{ 转义地雷）
    var template = """
        using PalORM;
        using __USING__;

        // 探针: __NAME__ —— 断言写在下方，结论打印到 stdout（证实/证伪一行说清）
        [Table("probe_entity")]
        public partial class ProbeEntity
        {
            [Key] public long Id { get; set; }
            [Column("name")] public string Name { get; set; } = "";
        }

        internal static class Program
        {
            private static async Task Main()
            {
                await using var db = await DataSession<__PROV__>.CreateAsync(
                    new DbOptions { ConnectionString = "__CS__" });
                await db.MigrateAsync();
                // TODO: 探针主体
                Console.WriteLine("probe __NAME__: TODO");
            }
        }
        """;
    File.WriteAllText(programPath, template
        .Replace("__USING__", usingNs)
        .Replace("__PROV__", prov)
        .Replace("__CS__", cs)
        .Replace("__NAME__", name) + "\n", new UTF8Encoding(false));
}

Console.WriteLine($"探针工程: {dir}");
Console.WriteLine($"编辑 {dir}/Program.cs 后: cd {dir} && dotnet run");
return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    Console.Error.WriteLine("错误: 未找到仓库根（PalORM.slnx 哨兵缺失）");
    Environment.Exit(2);
    return "";
}
