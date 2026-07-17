namespace PalORM;

/// <summary>数据库配置——record 类型(值相等+with 表达式)。
/// <para><b>为什么是 record</b>: init-only 属性+with 表达式——修改配置返回新实例, 原实例不变。
/// 符合"零全局可变状态"原则——每个测试创建独立 DbOptions, 互不干扰。</para>
/// <para><b>为什么不是 IConfiguration</b>: 配置值在编译时已知。不需要运行时从 JSON/环境变量解析的灵活性。</para></summary>
public sealed record DbOptions
{
    /// <summary>主库连接串（必需）。支持环境变量引用：$ENV:VAR_NAME。</summary>
    public required string ConnectionString { get; init; }

    /// <summary>只读副本连接串（可选）。配置后 ForRead() 自动路由到副本。</summary>
    public string? ReadConnectionString { get; init; }

    /// <summary>连接超时（默认 15 秒）。</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>命令默认超时（默认 30 秒）。</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>最大重试次数（默认 3 次）。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>重试退避策略（默认 100ms→200ms→400ms）。</summary>
    public Func<int, TimeSpan>? RetryBackoff { get; init; }

    /// <summary>连接池最大连接数（默认 100）。</summary>
    public int MaxPoolSize { get; init; } = 100;

    /// <summary>连接池空闲超时（默认 30 秒）。</summary>
    public int PoolIdleTimeoutSeconds { get; init; } = 30;

    /// <summary>连接最大生命周期（默认 60 分钟）。</summary>
    public int PoolLifetimeMinutes { get; init; } = 60;

    /// <summary>断路器：连续失败次数阈值（0 = 禁用）。</summary>
    public int CircuitBreakerThreshold { get; init; } = 5;

    /// <summary>断路器：熔断后恢复等待时间（默认 30 秒）。</summary>
    public TimeSpan CircuitBreakerResetAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>命名策略（默认保持原样）。</summary>
    public NamingConvention NamingConvention { get; init; } = NamingConvention.None;
    /// <summary>应用命名策略（None=原样, SnakeCase=下划线, LowerCase=全小写）。</summary>
    public string ApplyNaming(string name) => NamingConvention switch
    {
        NamingConvention.SnakeCase => ToSnakeCase(name),
        NamingConvention.LowerCase => name.ToLowerInvariant(),
        _ => name
    };
    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>查询拦截器列表。</summary>
    public IReadOnlyList<IQueryInterceptor>? Interceptors { get; init; }

    /// <summary>连接池配置入口。所有数值必须为正数。</summary>
    public DbOptions WithPool(int maxSize, int idleTimeoutSeconds = 30, int lifetimeMinutes = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(idleTimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lifetimeMinutes);
        return this with
        {
            MaxPoolSize = maxSize,
            PoolIdleTimeoutSeconds = idleTimeoutSeconds,
            PoolLifetimeMinutes = lifetimeMinutes
        };
    }

    /// <summary>日志级别（默认 Warning）。</summary>
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Warning;

    /// <summary>解析主库连接串中的环境变量引用。</summary>
    public string ResolveConnectionString() => ResolveConnectionString(ConnectionString);

    /// <summary>解析只读副本连接串中的环境变量引用；未配置时返回 <see langword="null"/>。</summary>
    public string? ResolveReadConnectionString()
        => ReadConnectionString is null ? null : ResolveConnectionString(ReadConnectionString);

    private static string ResolveConnectionString(string connectionString)
    {
        if (!connectionString.StartsWith("$ENV:", StringComparison.Ordinal))
            return connectionString;

        string envName = connectionString[5..];
        return Environment.GetEnvironmentVariable(envName)
            ?? throw new InvalidOperationException($"Environment variable '{envName}' not set.");
    }
}

/// <summary>命名策略。</summary>
public enum NamingConvention { None, SnakeCase, LowerCase }

/// <summary>日志级别简化版。</summary>
public enum LogLevel { Trace = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Critical = 5 }
