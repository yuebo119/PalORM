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

    /// <summary>命令超时的 ADO.NET 秒值——向上取整，保证亚秒超时不塌缩为 0（ADO.NET 中
    /// CommandTimeout=0 语义是"无限等待"，与调用方设置亚秒超时的意图正相反，ITM-501）。
    /// 显式 <see cref="TimeSpan.Zero"/> 表示无限等待，原样透传 0。</summary>
    internal int CommandTimeoutSeconds => ToCommandTimeoutSeconds(CommandTimeout);

    /// <summary>TimeSpan → ADO.NET 命令超时秒值：正的亚秒值向上取整为 1 秒，
    /// Zero 透传为 0（无限），负值按 0 处理。</summary>
    internal static int ToCommandTimeoutSeconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return 0;
        double seconds = Math.Ceiling(timeout.TotalSeconds);
        return seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
    }

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

    /// <summary>命名策略（默认保持原样）。
    /// <para><b>作用域限制（ITM-516）</b>：列名/表名映射在**编译期**由源生成器按 [Table]/[Column]
    /// 注解确定，本运行时选项不参与该映射——设置本项不会改变已生成的列名。仅供调用方在
    /// 自定义 SQL 中手动调用 <see cref="ApplyNaming"/> 归一标识符。要改列名请用 [Column("...")]。</para></summary>
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

    /// <summary>校验配置数值合法性（ITM-517）——init 属性可绕过 WithPool 的构造校验直接设非法值，
    /// 在会话创建入口统一兜底。CommandTimeout=Zero 是合法的"无限等待"，此处不拒绝。</summary>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(CommandTimeout.Ticks, nameof(CommandTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConnectionTimeout.Ticks, nameof(ConnectionTimeout));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetries, nameof(MaxRetries));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPoolSize, nameof(MaxPoolSize));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PoolIdleTimeoutSeconds, nameof(PoolIdleTimeoutSeconds));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PoolLifetimeMinutes, nameof(PoolLifetimeMinutes));
        ArgumentOutOfRangeException.ThrowIfNegative(CircuitBreakerThreshold, nameof(CircuitBreakerThreshold));
        ArgumentOutOfRangeException.ThrowIfNegative(CircuitBreakerResetAfter.Ticks, nameof(CircuitBreakerResetAfter));
    }

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

    /// <summary>会话日志级别下限。零消费点的死配置（ITM-423）——级别过滤请在
    /// LoggerFactory 配置（AddFilter/appsettings Logging 节），那里的过滤对全部日志出口生效。</summary>
    [Obsolete("此配置从未被消费。请在 LoggerFactory 上配置级别过滤（AddFilter）。3.0 移除。")]
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
public enum NamingConvention
{
    /// <summary>保持原样，不做转换。</summary>
    None,

    /// <summary>转为下划线命名（UserName → user_name）。</summary>
    SnakeCase,

    /// <summary>转为全小写（UserName → username）。</summary>
    LowerCase
}

/// <summary>日志级别简化版。</summary>
public enum LogLevel
{
    /// <summary>最详细级别——逐步执行细节。</summary>
    Trace = 0,

    /// <summary>调试信息——开发期诊断。</summary>
    Debug = 1,

    /// <summary>常规信息——正常流程事件。</summary>
    Information = 2,

    /// <summary>警告——异常但不中断执行的情况。</summary>
    Warning = 3,

    /// <summary>错误——当前操作失败。</summary>
    Error = 4,

    /// <summary>严重错误——进程级不可恢复故障。</summary>
    Critical = 5
}
