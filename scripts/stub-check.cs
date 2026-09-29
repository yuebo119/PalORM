// stub-check.cs（自 stub-check.sh 迁移，脚本 C# 化整改方案 T2-1）
// 扫描明确的占位实现。合法 fluent 方法必须改变状态，不能靠方法名排除。
// 契约（D6/D8）：0=干净，1=发现占位；stdout/stderr 格式与 .sh 版对拍兼容。
using System.Text;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var dir = args.Length > 0 ? args[0] : "src/";
var count = 0;

Console.WriteLine("=== PalORM 空壳扫描 ===");
Scan("直接返回 this 的表达式体", """=>\s*this\s*;""");
Scan("NotImplementedException 占位", """NotImplementedException""");
Scan("TODO 占位异常", """throw\s+new\s+NotSupportedException\("(?:TODO|Not implemented|Not yet implemented)""");

Console.WriteLine();
if (count > 0)
{
    Console.WriteLine($"FAIL 发现 {count} 个明确空壳或占位实现。");
    return 1;
}
Console.WriteLine("PASS 无明确空壳或占位实现。");
return 0;

void Scan(string title, string pattern)
{
    Console.WriteLine();
    Console.WriteLine($"--- {title} ---");
    var regex = new Regex(pattern, RegexOptions.Compiled);
    List<string> hits = [];
    if (Directory.Exists(dir))
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue; // 与 grep 行为对齐：读不了的文件静默跳过
            }
            for (var i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    hits.Add($"{file.Replace('\\', '/')}:{i + 1}:{lines[i]}");
                }
            }
        }
    }
    if (hits.Count > 0)
    {
        foreach (var hit in hits)
        {
            Console.WriteLine(hit);
        }
        count += hits.Count;
    }
    else
    {
        Console.WriteLine("PASS 零发现");
    }
}
