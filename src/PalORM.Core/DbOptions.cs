namespace PalORM;

/// <summary>数据库配置——record 类型(值相等+with 表达式)。
/// <para><b>为什么是 record</b>: init-only 属性+with 表达式——修改配置返回新实例, 原实例不变。
/// 符合"零全局可变状态"原则——每个测试创建独立 DbOptions, 互不干扰。</para>
/// <para><b>为什么不是 IConfiguration</b>: 配置值在编译时已知。不需要运行时从 JSON/环境变量解析的灵活性。</para>
/// <para><b>凭据卫生（重要）</b>: ToString() 已脱敏连接串，但按属性解构的路径不受保护——
/// 请勿将本对象整体序列化（STJ/Newtonsoft）或以结构化日志解构（Serilog <c>{@Options}</c>），
/// 这些路径会输出 ConnectionString/ReadConnectionString 明文。日志请使用 <c>{Options}</c>（走 ToString）。</para></summary>
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

    /// <summary>池配置是否被显式设置（WithPool 置位）。SQLite Provider 据此拒绝
    /// 不支持的池配置——不与默认值比对，避免默认值漂移时误判（ITM-315）。</summary>
    public bool PoolExplicitlyConfigured { get; init; }

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

    /// <summary>原生 SQL 查询（QueryAsync 家族）首行列名校验（默认开启，ADR-A）。
    /// 结果集按 ordinal 映射实体，列序错位会静默交换同型列数据；开启后首行比对
    /// 结果列名与实体声明序列名，不匹配抛异常。仅校验首行，热路径零额外开销。
    /// 使用列别名/表达式列的查询需关闭此项并自行保证列序。</summary>
    public bool ValidateQueryColumnOrder { get; init; } = true;

    /// <summary>查询缓存实现（ADR-C）。未设置时使用进程级共享的有界默认缓存（1024 条）。
    /// 注入独立实例可实现会话/租户级缓存隔离；实现需线程安全。</summary>
    public IQueryCache? QueryCache { get; init; }

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
            PoolLifetimeMinutes = lifetimeMinutes,
            PoolExplicitlyConfigured = true
        };
    }

    /// <summary>日志工厂。设置后 DataSession 经其创建 ILogger（ITM-323：此前公共入口
    /// 拿不到 logger，MinimumLogLevel 是死配置）。未设置时使用 NullLogger。</summary>
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>会话日志级别下限——过滤 LoggerFactory 产出的日志（默认 Warning）。</summary>
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

    /// <summary>脱敏输出——连接串含凭据，record 合成的 ToString 会打印全部属性明文。
    /// 覆写后连接串以掩码代替，日志/异常/调试器场景不泄露密码。</summary>
    public override string ToString()
        => $"DbOptions {{ ConnectionString = ***MASKED***, " +
           $"ReadConnectionString = {(ReadConnectionString is null ? "null" : "***MASKED***")}, " +
           $"ConnectionTimeout = {ConnectionTimeout}, CommandTimeout = {CommandTimeout}, " +
           $"MaxRetries = {MaxRetries}, MaxPoolSize = {MaxPoolSize}, " +
           $"CircuitBreakerThreshold = {CircuitBreakerThreshold}, " +
           $"NamingConvention = {NamingConvention} }}";
}

/// <summary>命名策略。</summary>
public enum NamingConvention { None, SnakeCase, LowerCase }

/// <summary>日志级别简化版。</summary>
public enum LogLevel { Trace = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Critical = 5 }
