using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace PalORM.SourceGen;

/// <summary>[SqlFile("path.sql")] 特性源生成器——编译时把 .sql 文件嵌入为常量方法。
/// <para><b>评审 2026-09-02 管线重构</b>：文件内容经 AdditionalFiles（targets 自动注入
/// **/*.sql）进入增量管线，内容成为缓存键——仅编辑 .sql 也触发重新生成（消除 ITM-585
/// "改 .sql 不重读"的陈旧缓存限制）；生成器零磁盘 IO，RS1035 文件级抑制删除。
/// 此前注释声称"RS1041 强制 netstandard2.0 故不能用 AdditionalTexts"系误注——该规则只
/// 约束 TFM，AdditionalTextsProvider 在 netstandard2.0 可用；真实收益是文件内容以值相等
/// 进入缓存键（即本重构后的实际形态）。项目根改读 build_property.ProjectDir，不再从源
/// 文件路径向上找 *.csproj（多项目/linked file 场景可能解析到错误根）。</para>
/// <para>Provider 条件分支: .sql 文件中 -- @pg/@mysql/@sqlite/@all 指令→根据 [SqlFile(Provider="xx")]
/// 编译时只提取匹配段。</para>
/// <para>安全: 拒绝绝对路径和 .. 遍历, 解析后前缀校验防越界。</para></summary>
internal static class SqlFileEmitter
{
    /// <summary>[SqlFile] 方法的编译期模型——路径校验与内容查找在 <see cref="Render"/>
    /// 阶段（RegisterSourceOutput）执行；本模型只含缓存键友好（纯字符串）成员。</summary>
    internal sealed record SqlFileMethodModel(
        string HintName,
        string? Namespace,
        string TypeName,
        string MethodName,
        string RelativePath,
        string? TargetProvider);

    /// <summary>AdditionalText 的内容快照——值相等进入增量缓存键（内容变更即管线失效）。</summary>
    internal readonly record struct SqlFileContent(string Path, string Content)
    {
        public static SqlFileContent FromAdditionalText(AdditionalText text, CancellationToken ct)
            => new(text.Path, text.GetText(ct)?.ToString() ?? "");
    }

    /// <summary>提取阶段（transform，缓存键=语法+符号）：只取方法形状与特性参数，零 IO。
    /// 泛型/嵌套类不支持——跳过生成 → 宿主侧得到 CS8795（partial 方法缺实现）直接指向
    /// 原方法，明确可定位（r19/ITM-686）。</summary>
    internal static SqlFileMethodModel? ExtractMethodModel(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method)
            return null;

        if (method.ContainingType is { IsGenericType: true }
            || method.ContainingType?.ContainingType is not null)
            return null;

        var attr = method.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name is "SqlFileAttribute" or "SqlFile"
            && a.AttributeClass?.ContainingNamespace?.ToDisplayString() is "PalORM");
        if (attr?.ConstructorArguments.Length != 1)
            return null;

        string relativePath = attr.ConstructorArguments[0].Value?.ToString() ?? "";

        // 读取可选 Provider 参数
        string? targetProvider = null;
        foreach (var namedArg in attr.NamedArguments)
        {
            if (namedArg.Key is "Provider" && namedArg.Value.Value is string p)
                targetProvider = p;
        }

        INamedTypeSymbol? containingType = method.ContainingType;
        string typeName = containingType?.Name ?? "Unknown";
        string? ns = containingType?.ContainingNamespace?.IsGlobalNamespace == true
            ? null : containingType?.ContainingNamespace?.ToDisplayString();

        return new SqlFileMethodModel(
            PalORMGenerator.CreateStableHintName(
                "SqlFile", method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            ns, typeName, method.Name, relativePath, targetProvider);
    }

    /// <summary>渲染阶段（RegisterSourceOutput）：路径安全校验 + AdditionalFiles 内容查找 +
    /// Provider 段解析。找不到内容时发射 Obsolete(error) 错误占位（调用点 CS0619 指向原方法）。</summary>
    internal static string? Render(
        SqlFileMethodModel model,
        ImmutableArray<SqlFileContent> sqlFiles,
        string? projectDir)
    {
        if (string.IsNullOrEmpty(model.RelativePath))
            return GenerateError(model, "SqlFile 路径不能为空。");

        // 安全: 拒绝绝对路径和路径遍历
        // ITM-584: '..' 按路径段判定——子串判定误拒 `my..queries.sql` 等合法文件名；
        // 真正的遍历（`../x` / `a/../b`）仍被拒绝，且 ITM-545 的解析后前缀校验仍在下游兜底。
        if (Path.IsPathRooted(model.RelativePath) || HasTraversalSegment(model.RelativePath))
            return GenerateError(model, $"SqlFile 路径必须为相对路径，不允许 '..' 或绝对路径: {model.RelativePath}");

        // 无 ProjectDir（非 MSBuild 宿主）时无法解析相对路径——跳过（与原实现同口径）。
        // 模式判空而非 IsNullOrEmpty：netstandard2.0 引用程序集无 [NotNullWhen] 注解，
        // 流分析依赖模式匹配（评审 2026-09-02）。
        // ITM-789(r21)：缺 ProjectDir（非 MSBuild 宿主）原 return null——partial 方法无实现
        // 报 CS8795（缺实现），可定位性差于设计的 CS0619 占位（Obsolete error）。改为发
        // 可检索错误占位，与 not-found 同型。
        if (projectDir is null || projectDir.Length == 0)
            return GenerateError(model,
                $"SqlFile 需要 MSBuild 属性 build_property.ProjectDir（经 PalORM.SourceGen targets " +
                "或 CompilerVisibleProperty 注入）。自定义 Roslyn 宿主请注入该属性后重试。");

        string fullPath = Path.GetFullPath(Path.Combine(projectDir, model.RelativePath));

        // 确保解析后路径仍在项目目录内。前缀比较带尾分隔符（ITM-545 纵深防御）：
        // 否则 rootDir="/proj/app" 时 "/proj/app-evil/x" 会误判为在内（虽当前被 .. 拒绝挡住）。
        // ITM-632 登记：OrdinalIgnoreCase 在 Linux 大小写敏感 FS 上可放行大小写异形越界路径，
        // 内容查找同为不区分大小写（实害低）；GetFullPath 不解析 symlink——指向项目外的 .sql
        // 符号链接可越界读。两者属受信任项目文件模型的接受面（.csproj 同级威胁）。
        string rootWithSep = projectDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? projectDir : projectDir + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            return GenerateError(model, $"SqlFile 路径越界: {model.RelativePath}");

        string? sqlContent = FindContent(sqlFiles, fullPath);
        if (sqlContent is null)
            return GenerateError(model,
                $"SQL file not found: {fullPath}. Ensure the file exists and is supplied to the compiler as "
                + "AdditionalFiles (automatic via the PalORM.SourceGen targets; otherwise add "
                + "<AdditionalFiles Include=\"**\\*.sql\" /> to your project).");

        // ── Provider 条件分支解析 ──
        // ITM-564：段全不匹配时此前静默回退整份原文（含全部异方言语句），运行期才炸；
        // 未识别的 provider 别名（如 "postgres"）也静默落入同路径——两者都转为编译期明确失败。
        SqlSectionResolution resolution = ResolveProviderSections(sqlContent, model.TargetProvider);
        if (resolution.UnrecognizedProvider is not null)
            return GenerateError(model,
                $"SqlFile Provider '{resolution.UnrecognizedProvider}' 不是有效的 provider 名；" +
                "支持: postgresql/pg, mysql/my, sqlite/sq");
        if (resolution.HasDirectives && string.IsNullOrWhiteSpace(resolution.Resolved))
            // ITM-790(r21)：补"-- @ 即指令"提示——普通注释（如 `-- @author x`）会被当作
            // provider 指令切段，用户无从知道为什么"没有段匹配"。
            return GenerateError(model,
                $"SqlFile '{model.RelativePath}' 声明了 provider 段但没有任何段匹配 " +
                $"'{model.TargetProvider ?? "(未指定)"}'（也无 @all 段）；" +
                "注意：以 '-- @' 开头的行都会被解析为 provider 指令（如 -- @author 会被切段），" +
                "普通注释请勿以 '-- @' 开头。嵌入整份原文会在运行期执行异方言 SQL，已拒绝");
        sqlContent = resolution.Resolved;

        return GenerateMethod(model, sqlContent, fullPath);
    }

    /// <summary>在 AdditionalFiles 内容快照中按解析后的绝对路径查找 .sql 内容。
    /// 归一化分隔符后不区分大小写比较（MSBuild 传入路径的分隔符/大小写可能与
    /// GetFullPath 产物不同；Linux 大小写接受面见 Render 注释 ITM-632）。</summary>
    private static string? FindContent(ImmutableArray<SqlFileContent> sqlFiles, string fullPath)
    {
        string normalizedTarget = NormalizeSeparators(fullPath);
        foreach (SqlFileContent file in sqlFiles)
        {
            if (string.Equals(NormalizeSeparators(file.Path), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                return file.Content;
        }
        return null;
    }

    private static string NormalizeSeparators(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string GenerateMethod(SqlFileMethodModel model, string sqlContent, string sourcePath)
    {
        // 使用 C#11 raw string literal 处理含引号和反斜杠的 SQL；SQL 内容含 """ 序列时
        // 加长定界符（比内容中最长引号连串多 1），保证生成物永远合法（ITM-410 同类加固）。
        // 注意：生成物要求消费项目 LangVersion ≥ 11（评审 2026-09-02 补登记的前提）。
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
        if (model.Namespace is not null) sb.AppendLine($"namespace {model.Namespace};");
        sb.AppendLine();
        sb.AppendLine($"partial class {model.TypeName}");
        sb.AppendLine("{");
        // ITM-584: 路径含 &/< 时 XML doc 畸形（CS1570，TreatWarningsAsErrors 下变错误）——XML 实体转义
        sb.AppendLine($"    /// <summary>编译时嵌入 SQL (来源: {EscapeForXmlDoc(sourcePath)})</summary>");
        sb.Append("    public static partial string ").Append(model.MethodName).Append("() => ").AppendLine(delimiter);
        sb.AppendLine(sqlContent);
        sb.Append(delimiter).AppendLine(";");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>路径遍历检测——'..' 按路径段判定，避免误拒 my..file.sql 等合法文件名（ITM-584）。</summary>
    private static bool HasTraversalSegment(string relativePath)
        => relativePath.Split('/', '\\').Any(static segment => segment == "..");

    private static string GenerateError(SqlFileMethodModel model, string error)
    {
        // FormatLiteral 统一转义——错误消息含引号/换行时生成物仍是合法 C#（ITM-410：
        // 此前 AppendLine 在字符串字面量中间断行，生成物本身 CS1010，Obsolete 诊断被架空）
        string errorLiteral = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(error, quote: true);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (model.Namespace is not null) sb.AppendLine($"namespace {model.Namespace};");
        sb.AppendLine();
        sb.AppendLine($"partial class {model.TypeName}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>SqlFile 源生成失败。</summary>");
        sb.AppendLine($"    [global::System.Obsolete({errorLiteral}, error: true)]");
        sb.Append("    public static partial string ").Append(model.MethodName)
            .Append("() => throw new global::System.IO.FileNotFoundException(")
            .Append(errorLiteral).AppendLine(");");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private readonly record struct SqlSectionResolution(
        string Resolved, bool HasDirectives, string? UnrecognizedProvider);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "Provider 段解析：@pg/@mysql/@sqlite/@all 指令按 Provider 白名单匹配；"
            + "未识别 Provider 与「段全不匹配」两类诊断需独立分支。拆分会破坏单遍扫描语义。")]
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

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            // 检测 -- @provider 指令
            // ITM-632 登记：`-- @` 前缀是指令语法的保留前缀——任何以之行首的注释都会切换
            // 段落（含 -- @author 等普通注释，其后内容不再输出）。段名集开放（Provider 参数
            // 接受别名如 pg，由 SqlFileTests 锁定），无法白名单枚举。用户注释请勿以 `-- @`
            // 开头——这是本特性的既定语法契约。
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("-- @", StringComparison.Ordinal))
            {
                hasDirectives = true;
                string directive = trimmed.Substring(4).Trim().ToLowerInvariant();
                // 提取 provider 名（去掉 -- @ 前缀后的单词）
                int space = directive.IndexOf(' ');
                string sectionName = space > 0 ? directive.Substring(0, space) : directive;

                // 匹配当前 provider 或 @all 段
                inSection = sectionName == "all"
                    || (target is not null && sectionName == target);
                continue;
            }

            if (inSection && (result.Length > 0 || !string.IsNullOrWhiteSpace(line)))
                result.AppendLine(line);
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

    /// <summary>XML doc 注释内容转义（ITM-584）：&amp;/&lt;/&gt; 实体化防 CS1570。</summary>
    private static string EscapeForXmlDoc(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
