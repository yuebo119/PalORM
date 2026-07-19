using System.Text;
using Microsoft.CodeAnalysis;

namespace PalORM.SourceGen;

/// <summary>[SqlFile("path.sql")] 特性源生成器——编译时读取 .sql 文件并嵌入为 const string。
/// <para><b>为什么不用 AdditionalTexts</b>: RS1041 强制 netstandard2.0, AdditionalTexts 仅在 net8.0+ 暴露。
/// 使用 ForAttributeWithMetadataName 从特性参数获取文件路径——任何 Roslyn 版本通用。</para>
/// <para>Provider 条件分支: .sql 文件中 -- @pg/@mysql/@sqlite/@all 指令→根据 [SqlFile(Provider="xx")]
/// 编译时只提取匹配段。</para>
/// <para>安全: 拒绝绝对路径和 .. 遍历, Path.GetFullPath 前缀校验防越界。</para></summary>
internal static class SqlFileEmitter
{
    internal static string? Generate(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method)
            return null;

        var attr = method.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name is "SqlFileAttribute" or "SqlFile"
            && a.AttributeClass?.ContainingNamespace?.ToDisplayString() is "PalORM");
        if (attr?.ConstructorArguments.Length != 1)
            return null;

        string relativePath = attr.ConstructorArguments[0].Value?.ToString() ?? "";
        if (string.IsNullOrEmpty(relativePath))
            return null;

        // 读取可选 Provider 参数
        string? targetProvider = null;
        foreach (var namedArg in attr.NamedArguments)
        {
            if (namedArg.Key is "Provider" && namedArg.Value.Value is string p)
                targetProvider = p;
        }

        // 安全: 拒绝绝对路径和路径遍历
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(".."))
            return GenerateError(method, $"SqlFile 路径必须为相对路径，不允许 '..' 或绝对路径: {EscapeForCSharp(relativePath)}");

        ct.ThrowIfCancellationRequested();

        // ITM-530：源生成器读取磁盘 .sql 文件是本特性的核心设计（见类型注释——RS1041 下
        // 无法用 AdditionalTexts），此处按需读盘属刻意为之，故局部抑制 RS1035（分析器禁用 IO API）
        // 而非全局 NoWarn，保证其它意外 IO 仍被诊断。
#pragma warning disable RS1035
        // 编译时读取文件: 相对于项目根目录
        string? projectDir = Path.GetDirectoryName(
            ctx.TargetSymbol.Locations.FirstOrDefault()?.SourceTree?.FilePath);
        if (projectDir is null) return null;

        ct.ThrowIfCancellationRequested();

        // 向上查找 .csproj, 最多 10 层 (避免无限循环)
        string? currentDir = projectDir;
        for (int depth = 0; depth < 10 && currentDir is not null; depth++)
        {
            if (Directory.GetFiles(currentDir, "*.csproj").Length > 0) break;
            string? parent = Path.GetDirectoryName(currentDir);
            if (parent == currentDir) { currentDir = null; break; }
            currentDir = parent;
        }
        string rootDir = currentDir ?? projectDir;
        string fullPath = Path.GetFullPath(Path.Combine(rootDir, relativePath));

        // 确保解析后路径仍在项目目录内。前缀比较带尾分隔符（ITM-545 纵深防御）：
        // 否则 rootDir="/proj/app" 时 "/proj/app-evil/x" 会误判为在内（虽当前被 .. 拒绝挡住）。
        string rootWithSep = rootDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? rootDir : rootDir + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            return GenerateError(method, $"SqlFile 路径越界: {EscapeForCSharp(relativePath)}");

        string sqlContent;
        try
        {
            sqlContent = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return GenerateError(method, $"SQL file not found: {EscapeForCSharp(fullPath)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件被占用/无权限：友好诊断而非源生成器崩溃
            return GenerateError(method,
                $"SQL file could not be read ({ex.GetType().Name}): {EscapeForCSharp(fullPath)}");
        }
#pragma warning restore RS1035

        // ── V_SQL: 条件分支解析 ──
        // ITM-564：段全不匹配时此前静默回退整份原文（含全部异方言语句），运行期才炸；
        // 未识别的 provider 别名（如 "postgres"）也静默落入同路径——两者都转为编译期明确失败。
        SqlSectionResolution resolution = ResolveProviderSections(sqlContent, targetProvider);
        if (resolution.UnrecognizedProvider is not null)
            return GenerateError(method,
                $"SqlFile Provider '{EscapeForCSharp(resolution.UnrecognizedProvider)}' 不是有效的 provider 名；" +
                "支持: postgresql/pg, mysql/my, sqlite/sq");
        if (resolution.HasDirectives && string.IsNullOrWhiteSpace(resolution.Resolved))
            return GenerateError(method,
                $"SqlFile '{EscapeForCSharp(relativePath)}' 声明了 provider 段但没有任何段匹配 " +
                $"'{EscapeForCSharp(targetProvider ?? "(未指定)")}'（也无 @all 段）；" +
                "嵌入整份原文会在运行期执行异方言 SQL，已拒绝");
        sqlContent = resolution.Resolved;

        INamedTypeSymbol? containingType = method.ContainingType;
        string typeName = containingType?.Name ?? "Unknown";
        string? ns = containingType?.ContainingNamespace?.IsGlobalNamespace == true
            ? null : containingType?.ContainingNamespace?.ToDisplayString();

        return GenerateMethod(ns, typeName, method.Name, sqlContent, fullPath);
    }

    private static string GenerateMethod(string? ns, string typeName, string methodName, string sqlContent, string sourcePath)
    {
        // 使用 C#11 raw string literal 处理含引号和反斜杠的 SQL；SQL 内容含 """ 序列时
        // 加长定界符（比内容中最长引号连串多 1），保证生成物永远合法（ITM-410 同类加固）
        int maxQuoteRun = 0, run = 0;
        foreach (char c in sqlContent)
        {
            run = c == '"' ? run + 1 : 0;
            if (run > maxQuoteRun) maxQuoteRun = run;
        }
        string delimiter = new('"', Math.Max(3, maxQuoteRun + 1));
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (ns is not null) sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"partial class {typeName}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>编译时嵌入 SQL (来源: {EscapeForCSharp(sourcePath)})</summary>");
        sb.Append("    public static partial string ").Append(methodName).Append("() => ").AppendLine(delimiter);
        sb.AppendLine(sqlContent);
        sb.Append(delimiter).AppendLine(";");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateError(IMethodSymbol method, string error)
    {
        string typeName = method.ContainingType?.Name ?? "Unknown";
        string? ns = method.ContainingType?.ContainingNamespace?.IsGlobalNamespace == true
            ? null : method.ContainingType?.ContainingNamespace?.ToDisplayString();

        // FormatLiteral 统一转义——错误消息含引号/换行时生成物仍是合法 C#（ITM-410：
        // 此前 AppendLine 在字符串字面量中间断行，生成物本身 CS1010，Obsolete 诊断被架空）
        string errorLiteral = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(error, quote: true);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (ns is not null) sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"partial class {typeName}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>SqlFile 源生成失败。</summary>");
        sb.AppendLine($"    [global::System.Obsolete({errorLiteral}, error: true)]");
        sb.Append("    public static partial string ").Append(method.Name)
            .Append("() => throw new global::System.IO.FileNotFoundException(")
            .Append(errorLiteral).AppendLine(");");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private readonly record struct SqlSectionResolution(
        string Resolved, bool HasDirectives, string? UnrecognizedProvider);

    private static SqlSectionResolution ResolveProviderSections(string sql, string? targetProvider)
    {
        // 将 provider 名标准化为短前缀；不在白名单的显式 Provider 视为拼写错误（ITM-564）
        string? unrecognized = null;
        string? target = targetProvider?.ToLowerInvariant() switch
        {
            null => null,
            "postgresql" or "pg" => "pg",
            "mysql" or "my" => "mysql",
            "sqlite" or "sq" => "sqlite",
            var other => Unrecognized(other, ref unrecognized)
        };

        var lines = sql.Split('\n');
        var result = new StringBuilder(sql.Length);
        bool inSection = true; // 默认包含 @all 段
        bool hasDirectives = false;
        string? currentSection = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            // 检测 -- @provider 指令
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("-- @", StringComparison.Ordinal))
            {
                hasDirectives = true;
                string directive = trimmed.Substring(4).Trim().ToLowerInvariant();
                // 提取 provider 名（去掉 -- @ 前缀后的单词）
                int space = directive.IndexOf(' ');
                string sectionName = space > 0 ? directive.Substring(0, space) : directive;

                currentSection = sectionName;
                // 匹配当前 provider 或 @all 段
                inSection = sectionName == "all"
                    || (target is not null && sectionName == target);
                continue;
            }

            if (inSection)
            {
                if (result.Length > 0 || !string.IsNullOrWhiteSpace(line))
                    result.AppendLine(line);
            }
        }

        string resolved = result.ToString().TrimEnd('\r', '\n');
        // 无任何指令的普通文件：原样返回（ITM-564 只拦"有段但全不匹配"）
        if (!hasDirectives)
            return new SqlSectionResolution(sql, false, unrecognized);
        return new SqlSectionResolution(resolved, true, unrecognized);

        static string? Unrecognized(string value, ref string? slot)
        {
            slot = value;
            return value;
        }
    }

    /// <summary>将路径/错误信息转义为合法的 C# 字符串字面量。</summary>
    private static string EscapeForCSharp(string text)
    {
        return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
