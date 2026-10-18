// backup-ai-system.cs（2026-10-18 质量审计轮）
// .ai 知识层异地备份：把不可再生的制度记忆（lessons/四系统提示词/账本/台账/规则）复制到
// 指定目录并写 MANIFEST 校验清单。
// 动机：.ai 是 gitignored 本地工具、无 remote、主仓零跟踪——201 条 lessons、误判库、指标
// 账本、提交锚基线全部只有一份副本，单盘故障即全丢。601MB 体积中知识文件仅约 1.5MB，
// 其余是 aot-poc/scratch-*/perf-probe 实验产物（可再生，不备份）。
// 契约：0=备份成功；1=备份失败/校验不一致；2=用法或环境错。
// 用法：
//   dotnet run --file scripts/backup-ai-system.cs
//   dotnet run --file scripts/backup-ai-system.cs -- --out <目录>
//   dotnet run --file scripts/backup-ai-system.cs -- --verify [--out <目录>]
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var repoRoot = FindRepoRoot();
var aiRoot = Path.Combine(repoRoot, ".ai");
if (!Directory.Exists(aiRoot))
{
    Console.Error.WriteLine($"错误：.ai 目录不存在（{aiRoot}）——本脚本只适用于装有 .ai 工具链的检出。");
    return 2;
}

var outDir = ArgValue(args, "--out")
    ?? Path.Combine(Path.GetDirectoryName(repoRoot) ?? repoRoot, "palorm-ai-backup");

if (args.Contains("--verify", StringComparer.Ordinal))
{
    return VerifyLatest(outDir);
}

return CreateBackup(aiRoot, outDir, repoRoot);

int CreateBackup(string source, string outRoot, string root)
{
    string[] includeRoots =
    [
        "README.md", "lessons.md", "CHANGELOG-lessons.md", "perf-ledger.md",
        "review", "gate", "refine", "test", "scripts", "system-template",
    ];
    string[] excludeDirNames = ["aot-poc", "brain-data", "tmp", "bin", "obj"];

    var files = CollectKnowledgeFiles(source, includeRoots, excludeDirNames);
    var totalBytes = files.Sum(f => new FileInfo(f).Length);
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    var target = Path.Combine(outRoot, stamp);

    Console.WriteLine($"源：{source}");
    Console.WriteLine($"目标：{target}");
    Console.WriteLine($"知识文件 {files.Count} 个，合计 {FormatSize(totalBytes)}（aot-poc/scratch/perf-probe 等可再生产物已排除）");

    Directory.CreateDirectory(target);
    var entries = new List<ManifestEntry>(files.Count);
    foreach (var f in files)
    {
        var rel = Path.GetRelativePath(source, f).Replace('\\', '/');
        var dest = Path.Combine(target, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(f, dest, overwrite: true);
        entries.Add(new ManifestEntry { Path = rel, Bytes = new FileInfo(dest).Length, Sha256Prefix = ShortHash(dest) });
    }

    var manifest = new BackupManifest
    {
        CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
        SourceRoot = source,
        MainRepoHead = Git(root, "rev-parse HEAD"),
        AiRepoHead = Git(source, "rev-parse HEAD"),
        FileCount = entries.Count,
        TotalBytes = entries.Sum(e => e.Bytes),
        Files = entries,
    };
    var manifestPath = Path.Combine(target, "MANIFEST.json");
    File.WriteAllText(manifestPath,
        JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.BackupManifest) + "\n",
        new UTF8Encoding(false));

    // 回读校验：备份本身也要能失败——逐文件比对字节数与哈希
    var bad = new List<string>();
    foreach (var e in entries)
    {
        var dest = Path.Combine(target, e.Path);
        if (!File.Exists(dest)) { bad.Add($"{e.Path}(缺失)"); continue; }
        if (new FileInfo(dest).Length != e.Bytes) { bad.Add($"{e.Path}(字节数不符)"); continue; }
        if (!string.Equals(ShortHash(dest), e.Sha256Prefix, StringComparison.Ordinal)) bad.Add($"{e.Path}(哈希不符)");
    }
    if (bad.Count > 0)
    {
        Console.WriteLine($"FAIL 备份回读校验失败 {bad.Count} 处：{string.Join(' ', bad.Take(10))}");
        return 1;
    }
    Console.WriteLine($"PASS 备份完成并回读校验通过（{entries.Count} 文件 · {FormatSize(totalBytes)}）");
    Console.WriteLine($"     MANIFEST：{manifestPath}");
    Console.WriteLine("     校验命令：dotnet run --file scripts/backup-ai-system.cs -- --verify");
    return 0;
}

int VerifyLatest(string outRoot)
{
    if (!Directory.Exists(outRoot))
    {
        Console.Error.WriteLine($"错误：备份根目录不存在（{outRoot}）");
        return 2;
    }
    var latest = Directory.GetDirectories(outRoot).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
    if (latest is null)
    {
        Console.Error.WriteLine($"错误：{outRoot} 下无备份");
        return 2;
    }
    var manifestFile = Path.Combine(latest, "MANIFEST.json");
    if (!File.Exists(manifestFile))
    {
        Console.Error.WriteLine($"错误：最新备份缺 MANIFEST（{latest}）");
        return 1;
    }
    var m = JsonSerializer.Deserialize(File.ReadAllText(manifestFile), ManifestJsonContext.Default.BackupManifest);
    if (m?.Files is null) { Console.Error.WriteLine("错误：MANIFEST 解析失败"); return 1; }
    var bad = m.Files.Where(e =>
        !File.Exists(Path.Combine(latest, e.Path))
        || !string.Equals(ShortHash(Path.Combine(latest, e.Path)), e.Sha256Prefix, StringComparison.Ordinal)).ToList();
    if (bad.Count > 0)
    {
        Console.WriteLine($"FAIL 最新备份（{Path.GetFileName(latest)}）校验失败 {bad.Count}/{m.FileCount} 个文件");
        foreach (var e in bad.Take(10)) Console.WriteLine($"  - {e.Path}");
        return 1;
    }
    Console.WriteLine($"PASS 最新备份（{Path.GetFileName(latest)} · {m.CreatedAt}）校验通过：{m.FileCount} 文件 · {FormatSize(m.TotalBytes)}");
    Console.WriteLine($"     主仓 {Head(m.MainRepoHead)} · .ai {Head(m.AiRepoHead)}");
    return 0;
}

static List<string> CollectKnowledgeFiles(string source, string[] includeRoots, string[] excludeDirNames)
{
    var files = new List<string>();
    foreach (var rootName in includeRoots)
    {
        var full = Path.Combine(source, rootName);
        if (File.Exists(full)) { files.Add(full); continue; }
        if (!Directory.Exists(full)) continue;
        foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, f).Replace('\\', '/');
            if (rel.Split('/').Any(seg => excludeDirNames.Contains(seg, StringComparer.OrdinalIgnoreCase))) continue;
            var name = Path.GetFileName(f);
            // 运行产物：日志/整批结果 JSON 可再生，不入知识备份
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("full", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(f);
        }
    }
    return [.. files.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)];
}

static string? ArgValue(string[] argv, string flag)
{
    var i = Array.IndexOf(argv, flag);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

static string Head(string sha) => sha.Length > 8 ? sha[..8] : sha;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    Console.Error.WriteLine("错误：未找到仓库根（PalORM.slnx 哨兵缺失）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}

static string Git(string workingDir, string arguments)
{
    var psi = new ProcessStartInfo("git")
    {
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add(arguments.Split(' ')[0]);
    foreach (var a in arguments.Split(' ').Skip(1)) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd().Trim();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    return output;
}

static string ShortHash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream))[..16];
}

static string FormatSize(long bytes) => bytes >= 1024 * 1024
    ? $"{bytes / 1024.0 / 1024.0:F1} MB"
    : $"{bytes / 1024.0:F0} KB";

internal sealed class ManifestEntry
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256Prefix { get; set; } = "";
}

internal sealed class BackupManifest
{
    public string CreatedAt { get; set; } = "";
    public string SourceRoot { get; set; } = "";
    public string MainRepoHead { get; set; } = "";
    public string AiRepoHead { get; set; } = "";
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public List<ManifestEntry> Files { get; set; } = [];
}

[System.Text.Json.Serialization.JsonSerializable(typeof(BackupManifest))]
internal sealed partial class ManifestJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
