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
// --root：从另一份检出（如 v6.8.0 worktree）读取已构建程序集，用于补历史归档基线
string assemblyRoot = ReadOption("--root") is { Length: > 0 } rootArg
    ? Path.GetFullPath(rootArg)
    : FindRepoRoot();
// --archive <ver>：把当前快照写入 bench/baselines/public-api/v<ver>/，供 semver-bump-check 跨版本比对
string archiveVersion = ReadOption("--archive") ?? "";
string repoRoot = FindRepoRoot();
string baselineDir = Path.Combine(repoRoot, "bench", "baselines", "public-api");
if (archiveVersion.Length > 0)
{
    mode = "--update";
    baselineDir = Path.Combine(baselineDir, $"v{archiveVersion}");
}
Directory.CreateDirectory(baselineDir);

// PalORM.SourceGen 2026-11-05 纳入（G5）：它确有 4 个公开类型（PalORMGenerator /
// PalORMAnalyzer / SqlTemplates / InterceptionTarget），此前不在快照面内，破坏不可见。
string[] assemblies = ["PalORM.Core", "PalORM.MySql", "PalORM.PostgreSql", "PalORM.Sqlite", "PalORM.SourceGen"];
List<string> failures = [];
int changedFiles = 0;

foreach (string assemblyName in assemblies)
{
    string dll = ResolveAssemblyPath(assemblyRoot, assemblyName, configuration);
    if (dll.Length == 0)
    {
        Console.Error.WriteLine($"::error::缺少程序集 {assemblyName}（{assemblyRoot}/src/{assemblyName}/bin/{configuration}/**/*.dll）——先构建（-c {configuration}）");
        return 1;
    }

    string[] current = Snapshot(dll);
    string baselinePath = Path.Combine(baselineDir, $"{assemblyName}.txt");

    if (mode == "--update")
    {
        File.WriteAllLines(baselinePath, current, new UTF8Encoding(false));
        Console.WriteLine(archiveVersion.Length > 0
            ? $"{assemblyName}: 归档 v{archiveVersion} 写入（{current.Length} 行）→ {baselinePath}"
            : $"{assemblyName}: 基线已更新（{current.Length} 行）");
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

// TFM 自适应：SourceGen 是 netstandard2.0，其余是 net11.0。多个命中时优先 net11.0。
static string ResolveAssemblyPath(string root, string assemblyName, string configuration)
{
    string binDir = Path.Combine(root, "src", assemblyName, "bin", configuration);
    if (!Directory.Exists(binDir))
    {
        return "";
    }
    var candidates = Directory.GetFiles(binDir, $"{assemblyName}.dll", SearchOption.AllDirectories)
        .OrderByDescending(p => p.Contains($"{Path.DirectorySeparatorChar}net11.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .ToList();
    return candidates.Count > 0 ? candidates[0] : "";
}

// 依赖解析：先同目录（provider 的 Npgsql / MySqlConnector），再 SDK 的 Roslyn/bincore。
// SourceGen 的公开类型继承 DiagnosticAnalyzer / IIncrementalGenerator，这些基类来自
// Roslyn，而分析器包按惯例不把 Microsoft.CodeAnalysis 复制进构建输出（PrivateAssets），
// 直接反射会 FileNotFoundException。SDK 自带的 Roslyn 版本与被引用版本同源，可直接加载。
static string? ResolveDependencyPath(string directory, string name)
{
    string local = Path.Combine(directory, $"{name}.dll");
    if (File.Exists(local))
    {
        return local;
    }

    if (!name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
        && !name.StartsWith("System.Collections.Immutable", StringComparison.Ordinal))
    {
        return null;
    }

    // dotnet 根：<root>/shared/Microsoft.NETCore.App/<ver>/ → 上三级
    string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string? dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
    if (dotnetRoot is not null)
    {
        string sdkDir = Path.Combine(dotnetRoot, "sdk");
        if (Directory.Exists(sdkDir))
        {
            foreach (string sdk in Directory.GetDirectories(sdkDir).OrderByDescending(p => p, StringComparer.Ordinal))
            {
                string candidate = Path.Combine(sdk, "Roslyn", "bincore", $"{name}.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
    }

    // 兜底：NuGet 缓存里的 Roslyn（离线或自建 SDK 时）
    string nugetRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    string packageDir = name switch
    {
        "Microsoft.CodeAnalysis" => "microsoft.codeanalysis.common",
        "Microsoft.CodeAnalysis.CSharp" => "microsoft.codeanalysis.csharp",
        "System.Collections.Immutable" => "system.collections.immutable",
        _ => "",
    };
    if (packageDir.Length > 0)
    {
        string dir = Path.Combine(nugetRoot, packageDir);
        if (Directory.Exists(dir))
        {
            var hits = Directory.GetFiles(dir, $"{name}.dll", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Contains($"{Path.DirectorySeparatorChar}netstandard2.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ThenByDescending(p => p, StringComparer.Ordinal)
                .ToList();
            if (hits.Count > 0)
            {
                return hits[0];
            }
        }
    }

    return null;
}

static string[] Snapshot(string assemblyPath)
{
    string directory = Path.GetDirectoryName(assemblyPath)!;
    var context = new AssemblyLoadContext("public-api-snapshot", isCollectible: true);
    context.Resolving += (loadContext, name) =>
    {
        string? candidate = ResolveDependencyPath(directory, name.Name ?? "");
        return candidate is not null ? loadContext.LoadFromAssemblyPath(candidate) : null;
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
        // 剥掉泛型实参里的程序集标识（Version / Culture / PublicKeyToken）：那是依赖的
        // 发行版信息，不是 PalORM 的契约。实测同一份源码在开发者机与 CI 上解析出的
        // Microsoft.CodeAnalysis 版本不同（5.12 vs 4.14），保留了它基线就会随环境漂移。
        // 依赖版本变化由 Package Validation 在 pack 时按包依赖检查，不靠本快照。
        return [.. lines.Select(Normalize)];
    }
    finally
    {
        context.Unload();
    }
}

static bool IsVisible(MethodBase method)
    => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

// 形如 ", Version=11.0.0.0, Culture=neutral, PublicKeyToken=null" 一律删除（可出现在任意嵌套层）
static string Normalize(string line)
{
    const string Pattern = @", Version=\d+\.\d+\.\d+\.\d+(?:, Culture=[^,\]]+)?(?:, PublicKeyToken=[^,\]]+)?";
    return System.Text.RegularExpressions.Regex.Replace(line, Pattern, "");
}

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