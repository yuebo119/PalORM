using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalORM.Testing;

/// <summary>测试环境配置读取器——双层覆盖：环境变量 &gt; appsettings.test.json 占位符。
/// <para><b>不引入 Microsoft.Extensions.Configuration 依赖</b>——直接用 System.Text.Json 源生成上下文，
/// 保证 PalORM.Testing 与 Native AOT 全链路兼容。</para>
/// <para><b>占位符语法</b>：JSON 中 <c>${VAR_NAME}</c> 被替换为同名环境变量值；环境变量缺失时显式失败
/// （不静默回退到 localhost 等默认值——避免误写系统库，ITM-428 凭据卫生）。</para>
/// <para><b>完整连接串覆盖</b>：设置 <c>PALORM_PG_CONNECTION</c> / <c>PALORM_MYSQL_CONNECTION</c>
/// 直接返回该值，绕过 JSON 模板与拆分项。</para>
/// <para><b>本地凭据兜底（v5.6）</b>：解析前自动尝试从仓库根 <c>.env.test</c>（gitignored）补入
/// <b>缺失</b>的 <c>PALORM_*</c> 环境变量，见 <see cref="LoadDotEnvIfPresent"/>。此前该文件只能靠
/// 手工 <c>source scripts/set-test-env.sh</c> 生效——漏做时集成测试以"环境变量未设置"失败，
/// 而该报错容易被误读为"没有可用数据库实例"（实测已发生一次误判）。</para>
/// <para><b>查找路径</b>：从 <see cref="AppContext.BaseDirectory"/> 向上回溯最多 6 层；
/// 找到第一个 appsettings.test.json 即停止（便于从 test/&lt;proj&gt;/bin/Debug/net11.0/ 反向定位到仓库根）。
/// 上限 8 层：常规输出路径需 6 层（net11.0→Release→bin→&lt;proj&gt;→test→仓库根），RID 特定输出或 AOT publish 会再深一层。</para>
/// <para>线程安全：首次调用懒加载并缓存；进程内同一实例返回。</para></summary>
public static class TestEnvironment
{
    private const string _settingsFileName = "appsettings.test.json";
    private const string _dotEnvFileName = ".env.test";
    private const int _maxDirectoryDepth = 8;
    private const string _pgFullEnvVar = "PALORM_PG_CONNECTION";
    private const string _mySqlFullEnvVar = "PALORM_MYSQL_CONNECTION";

    /// <summary>只接受 <c>PALORM_</c> 前缀的键——该文件是测试配置载体，不应成为注入任意
    /// 进程环境变量的通道。</summary>
    private const string _dotEnvKeyPrefix = "PALORM_";

    /// <summary>0 = 未尝试，1 = 已尝试。只做一次，避免每次解析都走文件系统。</summary>
    private static int _dotEnvAttempted;

    // ITM-648：惰性加载 + 失败可重试——静态字段初始化抛异常会以 TypeInitializationException
    // 永久污染类型（文件后补也无法自愈）。Lazy(PublicationOnly) 不缓存异常：加载失败后
    // 下次调用自动重试，文件后补可自愈；并发首调由 Lazy 去重，不再竞态各自加载。
    private static readonly Lazy<TestSettings> _settings = new(
        Load, LazyThreadSafetyMode.PublicationOnly);

    private static TestSettings Settings => _settings.Value;

    /// <summary>从仓库根 <c>.env.test</c> 补入缺失的 <c>PALORM_*</c> 环境变量。
    /// <para><b>只补缺失，绝不覆盖</b>：显式设置的环境变量（CI secret、手工 export、
    /// <c>source scripts/set-test-env.sh</c>）恒优先，故 CI 路径不会读到该文件——那两处都
    /// 已注入完整连接串时本方法直接返回，零文件 IO。</para>
    /// <para><b>失败静默</b>：文件缺失/不可读时不抛异常——兜底路径不该把"没配本地凭据"
    /// 变成新的失败点，后续占位符解析仍会给出显式且可操作的报错。</para>
    /// <para><b>凭据卫生</b>：只写环境变量，不回显键值，异常不携带文件内容（P0 红线）。</para>
    /// <para>只尝试一次（进程级），后续调用为一次原子读。</para></summary>
    public static void LoadDotEnvIfPresent()
    {
        if (Interlocked.Exchange(ref _dotEnvAttempted, 1) != 0) return;
        try
        {
            string? path = FindFileUpwards(_dotEnvFileName);
            if (path is null) return;
            foreach ((string key, string value) in ParseDotEnv(File.ReadAllLines(path)))
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    Environment.SetEnvironmentVariable(key, value);
            }
        }
        catch (IOException) { /* 兜底路径：交给后续占位符解析给出显式报错 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
        catch (ArgumentException) { /* 键/值含非法字符——同上，不覆盖既有报错 */ }
    }

    /// <summary>解析 PostgreSQL 连接串。
    /// 优先级：<c>PALORM_PG_CONNECTION</c> &gt; JSON 模板 + <c>${PALORM_PG_*}</c> 占位符替换。</summary>
    /// <exception cref="InvalidOperationException">占位符对应的环境变量未设置。</exception>
    /// <exception cref="InvalidDataException">连接串模板格式非法（未闭合 <c>${</c> 或占位符名为空）。
    /// ITM-746(r21)：异常消息不回显模板内容（模板可能含字面量凭据），仅报偏移量。</exception>
    public static string ResolvePostgreSqlConnectionString()
        => ResolveWithFullOverride(Settings.ConnectionStrings.PostgreSql, _pgFullEnvVar);

    /// <summary>解析 MySQL 连接串。同 PG 的优先级规则。</summary>
    /// <exception cref="InvalidOperationException">占位符对应的环境变量未设置。</exception>
    /// <exception cref="InvalidDataException">连接串模板格式非法（未闭合 <c>${</c> 或占位符名为空）。
    /// ITM-746(r21)：异常消息不回显模板内容（模板可能含字面量凭据），仅报偏移量。</exception>
    public static string ResolveMySqlConnectionString()
        => ResolveWithFullOverride(Settings.ConnectionStrings.MySql, _mySqlFullEnvVar);

    /// <summary>SQLite 连接串（无凭据，固定 <c>Data Source=:memory:</c>）。</summary>
    public static string ResolveSqliteConnectionString() => Settings.ConnectionStrings.Sqlite;

    /// <summary>获取默认连接参数（超时/重试/池大小）。JSON 未配置时返回内置默认值。</summary>
    public static DefaultsSection Defaults => Settings.Defaults ?? new DefaultsSection();

    /// <summary>获取 PG 通知监听器默认配置。</summary>
    public static NotificationSection Notification
        => Settings.Notification ?? new NotificationSection();

    /// <summary>获取 Scaffold CLI 默认命名空间。</summary>
    public static string ScaffoldDefaultNamespace
        => Settings.Scaffold?.DefaultNamespace ?? "Models";

    private static TestSettings Load()
    {
        string path = FindFileUpwards(_settingsFileName)
            ?? throw new FileNotFoundException(
                $"{_settingsFileName} not found within {_maxDirectoryDepth} parent directories " +
                $"of {AppContext.BaseDirectory}. Expected at repository root.");

        string json = File.ReadAllText(path);
        TestSettings? settings = JsonSerializer.Deserialize(json, TestSettingsJsonContext.Default.TestSettings);
        if (settings is null || settings.ConnectionStrings is null)
        {
            throw new InvalidDataException(
                $"{path}: invalid JSON or missing 'ConnectionStrings' section.");
        }
        return settings;
    }


    private static string ResolveWithFullOverride(string template, string fullEnvVar)
    {
        // ITM-776(r21)：JSON 显式 null 会让 template 为 null——占位符展开首行 Contains 直接 NRE，
        // 与公开方法声明的 InvalidDataException 形态不符。入口统一守卫。
        if (template is null)
            throw new InvalidDataException(
                $"Connection string template in {_settingsFileName} is null (explicit JSON null). " +
                "Provide a template string or set the full-override environment variable.");
        // v5.6：本地凭据兜底——在读环境变量之前补入 .env.test 中缺失的 PALORM_* 键。
        // 放在此处而非调用方：两个 Resolve 方法、整串覆盖与占位符展开两条路径一次覆盖。
        LoadDotEnvIfPresent();
        string? full = Environment.GetEnvironmentVariable(fullEnvVar);
        return string.IsNullOrEmpty(full) ? ExpandPlaceholders(template, fullEnvVar) : full;
    }

    /// <summary>解析 <c>${VAR}</c> 占位符——不含 <c>$</c> 直接返回原串，避免无意义分配。</summary>
    private static string ExpandPlaceholders(string template, string contextEnvVar)
    {
        const char dollar = '$';
        if (!template.Contains(dollar, StringComparison.Ordinal))
            return template;

        var sb = new System.Text.StringBuilder(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            // ITM-746 附带：原条件 `i + 2 < template.Length` 漏检"模板恰以 ${ 结尾"（无第三字符）——
            // 该形态此前静默原样输出，后续连接失败无格式错误提示。改为 i+1 判定，end<0 分支报错。
            if (i + 1 < template.Length && template[i] == dollar && template[i + 1] == '{')
            {
                int end = template.IndexOf('}', i + 2, StringComparison.Ordinal);
                if (end < 0)
                    // ITM-746(r20)：模板含字面量密码时，整串回显会把凭据写进异常消息/日志（P0 红线）。
                    // 只报位置，不回显模板内容。
                    throw new InvalidDataException(
                        $"Malformed placeholder in connection string template (unclosed '{{' at offset {i}); " +
                        "connection string values are redacted to avoid leaking credentials.");

                // ITM-746：占位符名本身不含凭据，可安全回显；模板其余部分不回显
                string varName = template[(i + 2)..end];
                if (string.IsNullOrWhiteSpace(varName))
                    throw new InvalidDataException(
                        $"Malformed placeholder in connection string template (empty name at offset {i}); " +
                        "connection string values are redacted to avoid leaking credentials.");
                string? value = Environment.GetEnvironmentVariable(varName);
                if (string.IsNullOrEmpty(value))
                    throw new InvalidOperationException(
                        $"Environment variable '{varName}' (referenced in {_settingsFileName}) is not set. " +
                        $"Set it, or add it to the repository-root {_dotEnvFileName} " +
                        $"(auto-loaded for unset {_dotEnvKeyPrefix}* keys), " +
                        $"or set {contextEnvVar} to bypass the template.");

                sb.Append(value);
                i = end + 1;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>从 <see cref="AppContext.BaseDirectory"/> 向上回溯最多 <see cref="_maxDirectoryDepth"/>
    /// 层查找指定文件，未找到返回 null。appsettings 与 .env.test 共用同一回溯口径。
    /// <para><b>为什么先 TrimEndingDirectorySeparator</b>：<see cref="AppContext.BaseDirectory"/>
    /// 以目录分隔符结尾（<c>...\net11.0\</c>），而 <see cref="Path.GetDirectoryName(string)"/>
    /// 首次调用只会剥掉该分隔符、返回<b>同一层</b>——不归一化就等于白耗一次迭代，深度上限
    /// 实际少一层。该差一错误此前被 <c>appsettings.test.json</c> 的"复制到输出目录"（i=0 即命中）
    /// 掩盖，只在查找未被复制的 <c>.env.test</c> 时显形。</para></summary>
    internal static string? FindFileUpwards(string fileName) => FindFileUpwards(fileName, _maxDirectoryDepth);

    /// <summary>以指定深度上限回溯查找（起点固定为 <see cref="AppContext.BaseDirectory"/>）。</summary>
    internal static string? FindFileUpwards(string fileName, int maxDepth)
        => FindFileUpwards(fileName, maxDepth, AppContext.BaseDirectory);

    /// <summary>以指定起点与深度上限回溯查找。
    /// <para><b>为什么暴露起点</b>：只有让测试用「已知层数的临时目录树 + 带尾部分隔符的起点」
    /// 作基准，才能独立于本仓库的输出布局锁定"尾部分隔符偷走一次迭代"的差一错误。
    /// 依赖真实仓库布局的断言会随 <c>bin/&lt;cfg&gt;/&lt;tfm&gt;</c> 形状变化而误报；
    /// 而"先量出实现深度再自洽验证"的断言没有区分力（两种实现都自洽，实测假阴性）。</para></summary>
    internal static string? FindFileUpwards(string fileName, int maxDepth, string startDirectory)
    {
        // 归一化起点：调用方传入的起点常以分隔符结尾（AppContext.BaseDirectory 即如此），
        // Path.GetDirectoryName 首次调用只剥该分隔符、返回同一层，不归一化就白耗一次迭代。
        string dir = Path.TrimEndingDirectorySeparator(startDirectory);
        for (int i = 0; i < maxDepth && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
        return null;
    }

    /// <summary>解析 <c>.env.test</c> 行——<c>KEY=VALUE</c>，跳过空行与 <c>#</c> 注释，
    /// 剥掉成对的外层引号（<c>set -a</c> 加载的写法）。只产出 <c>PALORM_</c> 前缀的键。
    /// <para>值不参与任何日志或异常——调用方只把结果写进环境变量。</para></summary>
    internal static IEnumerable<(string Key, string Value)> ParseDotEnv(IEnumerable<string> lines)
    {
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;
            string key = line[..separator].Trim();
            if (!key.StartsWith(_dotEnvKeyPrefix, StringComparison.Ordinal)) continue;
            string value = line[(separator + 1)..].Trim();
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }
            yield return (key, value);
        }
    }
}

/// <summary>测试配置根——对应 appsettings.test.json 结构。</summary>
internal sealed class TestSettings
{
    [JsonPropertyName("ConnectionStrings")]
    public ConnectionStringsSection ConnectionStrings { get; set; } = new();

    [JsonPropertyName("Defaults")]
    public DefaultsSection? Defaults { get; set; }

    [JsonPropertyName("Notification")]
    public NotificationSection? Notification { get; set; }

    [JsonPropertyName("Scaffold")]
    public ScaffoldSection? Scaffold { get; set; }
}

internal sealed class ConnectionStringsSection
{
    [JsonPropertyName("PostgreSql")]
    public string PostgreSql { get; set; } = string.Empty;

    [JsonPropertyName("MySql")]
    public string MySql { get; set; } = string.Empty;

    [JsonPropertyName("Sqlite")]
    public string Sqlite { get; set; } = "Data Source=:memory:";
}

/// <summary>默认连接参数（来自 JSON 的 Defaults 段）。</summary>
public sealed class DefaultsSection
{
    /// <summary>连接超时秒数（默认 15）。</summary>
    [JsonPropertyName("ConnectionTimeoutSeconds")]
    public int ConnectionTimeoutSeconds { get; set; } = 15;

    /// <summary>命令超时秒数（默认 30）。</summary>
    [JsonPropertyName("CommandTimeoutSeconds")]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>最大重试次数（默认 3）。</summary>
    [JsonPropertyName("MaxRetries")]
    public int MaxRetries { get; set; } = 3;

    /// <summary>连接池最大连接数（默认 100）。</summary>
    [JsonPropertyName("MaxPoolSize")]
    public int MaxPoolSize { get; set; } = 100;
}

/// <summary>PG 通知监听器默认配置（来自 JSON 的 Notification 段）。</summary>
public sealed class NotificationSection
{
    /// <summary>最大重连尝试次数（默认 5）。</summary>
    [JsonPropertyName("ReconnectMaxAttempts")]
    public int ReconnectMaxAttempts { get; set; } = 5;

    /// <summary>重连基础退避秒数（默认 1，每次线性递增）。</summary>
    [JsonPropertyName("ReconnectBaseDelaySeconds")]
    public int ReconnectBaseDelaySeconds { get; set; } = 1;
}

internal sealed class ScaffoldSection
{
    [JsonPropertyName("DefaultNamespace")]
    public string DefaultNamespace { get; set; } = "Models";
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    ReadCommentHandling = JsonCommentHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TestSettings))]
internal sealed partial class TestSettingsJsonContext : JsonSerializerContext;
