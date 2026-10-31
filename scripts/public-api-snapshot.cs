// public-api-snapshot.cs（P1-D / CONTRACT-3）：公开 API 基线快照。
// 用法：
//   dotnet run --file scripts/public-api-snapshot.cs -- --check [--configuration Release]
//   dotnet run --file scripts/public-api-snapshot.cs -- --update [--configuration Release]
// --check 与 bench/baselines/public-api/<assembly>.txt 逐行比对；任何新增/删除/签名变化
// 都以非零退出码报红（PR 上即"破坏性变更报红"）。基线只由 --update 显式重写。
//
// 实现说明：API 快照天然需要读取程序集元数据，这里的反射只作用于本仓库构建产物，
// 不进产品运行时路径。加载用可回收 AssemblyLoadContext + 同目录依赖解析，
// 避免把 provider 依赖（Npgsql / MySqlConnector）装进脚本进程的默认上下文。

using System.Reflection;
using System.Runtime.Loader;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);

string mode = args.Contains("--update", StringComparer.Ordinal) ? "--update" : "--check";
string configuration = ReadOption("--configuration") ?? "Debug";
string repoRoot = FindRepoRoot();
string baselineDir = Path.Combine(repoRoot, "bench", "baselines", "public-api");
Directory.CreateDirectory(baselineDir);

string[] assemblies = ["PalORM.Core", "PalORM.MySql", "PalORM.PostgreSql", "PalORM.Sqlite"];
List<string> failures = [];
int changedFiles = 0;

foreach (string assemblyName in assemblies)
{
    string dll = Path.Combine(repoRoot, "src", assemblyName, "bin", configuration, "net11.0", $"{assemblyName}.dll");
    if (!File.Exists(dll))
    {
        Console.Error.WriteLine($"::error::缺少程序集 {dll}——先构建（-c {configuration}）");
        return 1;
    }

    string[] current = Snapshot(dll);
    string baselinePath = Path.Combine(baselineDir, $"{assemblyName}.txt");

    if (mode == "--update")
    {
        File.WriteAllLines(baselinePath, current, new UTF8Encoding(false));
        Console.WriteLine($"{assemblyName}: 基线已更新（{current.Length} 行）");
        continue;
    }

    if (!File.Exists(baselinePath))
    {
        failures.Add($"{assemblyName}: 基线缺失 {baselinePath}（首次冻结请运行 --update 并提交）");
        continue;
    }

    string[] baseline = File.ReadAllLines(baselinePath);
    (List<string> added, List<string> removed) = Diff(baseline, current);
    if (added.Count == 0 && removed.Count == 0) continue;

    changedFiles++;
    failures.Add($"{assemblyName}: 公开 API 相对基线变化 +{added.Count}/-{removed.Count}");
    foreach (string line in removed.Take(20)) Console.WriteLine($"- {line}");
    foreach (string line in added.Take(20)) Console.WriteLine($"+ {line}");
}

if (failures.Count > 0)
{
    foreach (string failure in failures) Console.Error.WriteLine($"::error::{failure}");
    Console.Error.WriteLine(
        $"公开 API 兼容门禁失败（{changedFiles} 个程序集变化）。确认变更是有意破坏后，"
        + "运行 --update 重写基线并在同一提交说明升级类型（SemVer）。");
    return 1;
}

Console.WriteLine($"公开 API 兼容门禁通过（{assemblies.Length} 个程序集，-c {configuration}）");
return 0;

string? ReadOption(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
    }
    return null;
}

static (List<string> Added, List<string> Removed) Diff(string[] baseline, string[] current)
{
    HashSet<string> baselineSet = new(baseline, StringComparer.Ordinal);
    HashSet<string> currentSet = new(current, StringComparer.Ordinal);
    List<string> added = [.. current.Where(line => !baselineSet.Contains(line))];
    List<string> removed = [.. baseline.Where(line => !currentSet.Contains(line))];
    return (added, removed);
}

static string[] Snapshot(string assemblyPath)
{
    string directory = Path.GetDirectoryName(assemblyPath)!;
    var context = new AssemblyLoadContext("public-api-snapshot", isCollectible: true);
    context.Resolving += (loadContext, name) =>
    {
        string candidate = Path.Combine(directory, $"{name.Name}.dll");
        return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
    };

    try
    {
        Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
        List<string> lines = [];
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (type.FullName is null) continue;
            lines.Add($"T:{type.FullName}|base={type.BaseType?.FullName ?? "-"}");
            foreach (Type implemented in type.GetInterfaces().OrderBy(static i => i.FullName, StringComparer.Ordinal))
                lines.Add($"I:{type.FullName}|{implemented.FullName}");

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (ConstructorInfo constructor in type.GetConstructors(Flags)
                .Where(IsVisible).OrderBy(static c => c.ToString(), StringComparer.Ordinal))
            {
                lines.Add($"M:{type.FullName}|.ctor({ParameterList(constructor.GetParameters())})");
            }
            foreach (MethodInfo method in type.GetMethods(Flags)
                .Where(IsVisible).Where(static m => !m.IsSpecialName)
                .OrderBy(static m => m.ToString(), StringComparer.Ordinal))
            {
                string generic = method.IsGenericMethodDefinition
                    ? $"<{method.GetGenericArguments().Length}>"
                    : "";
                lines.Add(
                    $"M:{type.FullName}|{method.Name}{generic}({ParameterList(method.GetParameters())})"
                    + $"|{method.ReturnType.FullName ?? method.ReturnType.Name}"
                    + $"|{(method.IsStatic ? "static" : "instance")}");
            }
            foreach (PropertyInfo property in type.GetProperties(Flags)
                .Where(static p => p.GetMethod is not null || p.SetMethod is not null)
                .OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                bool visible = (property.GetMethod is { } getter && IsVisible(getter))
                    || (property.SetMethod is { } setter && IsVisible(setter));
                if (!visible) continue;
                lines.Add($"P:{type.FullName}|{property.Name}|{property.PropertyType.FullName ?? property.PropertyType.Name}");
            }
            foreach (FieldInfo field in type.GetFields(Flags)
                .Where(static f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly)
                .OrderBy(static f => f.Name, StringComparer.Ordinal))
            {
                lines.Add($"F:{type.FullName}|{field.Name}|{field.FieldType.FullName ?? field.FieldType.Name}");
            }
            foreach (EventInfo @event in type.GetEvents(Flags).OrderBy(static e => e.Name, StringComparer.Ordinal))
                lines.Add($"E:{type.FullName}|{@event.Name}");
        }
        lines.Sort(StringComparer.Ordinal);
        return [.. lines];
    }
    finally
    {
        context.Unload();
    }
}

static bool IsVisible(MethodBase method)
    => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

static string ParameterList(ParameterInfo[] parameters)
    => string.Join(", ", parameters.Select(static p => p.ParameterType.FullName ?? p.ParameterType.Name));

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PalORM.slnx"))) return directory.FullName;
        directory = directory.Parent;
    }
    // 脚本经 dotnet run --file 运行，BaseDirectory 不在仓库内时退回当前目录
    string current = Directory.GetCurrentDirectory();
    if (File.Exists(Path.Combine(current, "PalORM.slnx"))) return current;
    Console.Error.WriteLine("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}