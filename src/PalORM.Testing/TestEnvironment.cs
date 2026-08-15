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
/// <para><b>查找路径</b>：从 <see cref="AppContext.BaseDirectory"/> 向上回溯最多 6 层；
/// 找到第一个 appsettings.test.json 即停止（便于从 test/&lt;proj&gt;/bin/Debug/net11.0/ 反向定位到仓库根）。</para>
/// <para>线程安全：首次调用懒加载并缓存；进程内同一实例返回。</para></summary>
public static class TestEnvironment
{
    private const string _settingsFileName = "appsettings.test.json";
    private const int _maxDirectoryDepth = 6;
    private const string _pgFullEnvVar = "PALORM_PG_CONNECTION";
    private const string _mySqlFullEnvVar = "PALORM_MYSQL_CONNECTION";

    // ITM-648：惰性加载 + 失败可重试——静态字段初始化抛异常会以 TypeInitializationException
    // 永久污染类型（文件后补也无法自愈）。Lazy(PublicationOnly) 不缓存异常：加载失败后
    // 下次调用自动重试，文件后补可自愈；并发首调由 Lazy 去重，不再竞态各自加载。
    private static readonly Lazy<TestSettings> _settings = new(
        Load, LazyThreadSafetyMode.PublicationOnly);

    private static TestSettings Settings => _settings.Value;

    /// <summary>解析 PostgreSQL 连接串。
    /// 优先级：<c>PALORM_PG_CONNECTION</c> &gt; JSON 模板 + <c>${PALORM_PG_*}</c> 占位符替换。</summary>
    /// <exception cref="InvalidOperationException">占位符对应的环境变量未设置。</exception>
    public static string ResolvePostgreSqlConnectionString()
        => ResolveWithFullOverride(Settings.ConnectionStrings.PostgreSql, _pgFullEnvVar);

    /// <summary>解析 MySQL 连接串。同 PG 的优先级规则。</summary>
    /// <exception cref="InvalidOperationException">占位符对应的环境变量未设置。</exception>
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
        string path = FindSettingsFile()
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

    private static string? FindSettingsFile()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < _maxDirectoryDepth && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, _settingsFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
        return null;
    }

    private static string ResolveWithFullOverride(string template, string fullEnvVar)
    {
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
            if (i + 2 < template.Length && template[i] == dollar && template[i + 1] == '{')
            {
                int end = template.IndexOf('}', i + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new InvalidDataException(
                        $"Malformed placeholder in connection string template: '{template}'.");

                string varName = template[(i + 2)..end];
                string? value = Environment.GetEnvironmentVariable(varName);
                if (string.IsNullOrEmpty(value))
                    throw new InvalidOperationException(
                        $"Environment variable '{varName}' (referenced in {_settingsFileName}) is not set. " +
                        $"Run: source scripts/set-test-env.sh, or set {contextEnvVar} to bypass the template.");

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
