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

    /// <summary>v5.0 阶段 5.2：主连接首次激活后执行的 SQL（一次性会话级配置）。
    /// <para>典型用途：<c>SET TIME ZONE 'UTC'</c>、<c>SET search_path TO 'app, public'</c>、
    /// <c>SET statement_timeout = 30000</c> 等。多条 SQL 用分号分隔，PalORM 一次
    /// ExecuteNonQueryAsync 执行（PG/MySQL/SQLite 均支持多语句）。</para>
    /// <para><b>作用域</b>：仅主连接（<see cref="DataSession{TProvider}.CreateAsync"/> 打开后）。
    /// 读副本连接请用 <see cref="ReadSessionSetupSql"/>。</para>
    /// <para><b>用户责任</b>：SQL 方言正确性由调用方保证。PalORM 不解析、不验证内容，
    /// 原样提交给数据库执行。</para></summary>
    public string? SessionSetupSql { get; init; }

    /// <summary>v5.0 阶段 5.2：读副本连接首次激活后执行的 SQL（一次性会话级配置）。
    /// <para>语义同 <see cref="SessionSetupSql"/>，但作用于 ForRead 路由到的只读副本连接。
    /// 未设置时读副本不执行任何额外 SQL。</para></summary>
    public string? ReadSessionSetupSql { get; init; }

    /// <summary>校验配置数值合法性（ITM-517）——init 属性可绕过 WithPool 的构造校验直接设非法值，
    /// 在会话创建入口统一兜底。CommandTimeout=Zero 是合法的"无限等待"，此处不拒绝。
    /// <para><b>v4.6 公开化</b>：用户可在 CreateAsync 前主动调用，实现 fail-fast。</para></summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(CommandTimeout.Ticks, nameof(CommandTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConnectionTimeout.Ticks, nameof(ConnectionTimeout));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetries, nameof(MaxRetries));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPoolSize, nameof(MaxPoolSize));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PoolIdleTimeoutSeconds, nameof(PoolIdleTimeoutSeconds));
        // r19/ITM-695：PG 侧 checked(分钟*60) 会在极大值抛 OverflowException（Npgsql 连接串
        // 的 ConnectionLifetime 为 int 秒）——Validate 统一兜底上限，三 Provider 行为一致。
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            PoolLifetimeMinutes, int.MaxValue / 60, nameof(PoolLifetimeMinutes));
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

    // ── v4.6 预设配置：常见场景一键初始化 ──────────────────────────────

    /// <summary>开发环境预设：宽松超时 + 禁用熔断 + 单次重试。
    /// <para>快速迭代优先，遇到错误快速失败而非重试堆积。</para></summary>
    public static DbOptions Development(string connectionString) => new DbOptions
    {
        ConnectionString = connectionString,
        CommandTimeout = TimeSpan.FromSeconds(60),
        ConnectionTimeout = TimeSpan.FromSeconds(30),
        MaxRetries = 1,
        CircuitBreakerThreshold = 0
    };

    /// <summary>生产环境预设：严格超时 + 高重试 + 激进熔断。
    /// <para>稳定性优先，瞬时故障自动重试，连续失败快速熔断保护下游。</para></summary>
    public static DbOptions Production(string connectionString, string? readConnectionString = null) => new DbOptions
    {
        ConnectionString = connectionString,
        ReadConnectionString = readConnectionString,
        CommandTimeout = TimeSpan.FromSeconds(30),
        ConnectionTimeout = TimeSpan.FromSeconds(15),
        MaxRetries = 5,
        CircuitBreakerThreshold = 10,
        CircuitBreakerResetAfter = TimeSpan.FromSeconds(60)
    }.WithPool(maxSize: 100);

    /// <summary>测试环境预设：短超时 + 零重试 + 禁用熔断。
    /// <para>测试确定性优先，瞬时故障应立即暴露而非重试掩盖。</para></summary>
    public static DbOptions Testing(string connectionString) => new DbOptions
    {
        ConnectionString = connectionString,
        CommandTimeout = TimeSpan.FromSeconds(5),
        ConnectionTimeout = TimeSpan.FromSeconds(5),
        MaxRetries = 0,
        CircuitBreakerThreshold = 0,
        ValidateQueryColumnOrder = true
    };

    // ── v4.6 环境变量覆盖：Docker/K8s 部署友好 ──────────────────────

    /// <summary>从环境变量构建配置。未设置的环境变量使用默认值。
    /// <para>支持的变量：PALORM_CONNECTION（必需）、PALORM_READ_CONNECTION、
    /// PALORM_COMMAND_TIMEOUT、PALORM_CONNECTION_TIMEOUT、PALORM_MAX_RETRIES、
    /// PALORM_CIRCUIT_BREAKER_THRESHOLD、PALORM_MAX_POOL_SIZE。</para></summary>
    /// <param name="connectionEnv">主库连接串环境变量名（默认 PALORM_CONNECTION）。</param>
    public static DbOptions FromEnvironment(string connectionEnv = "PALORM_CONNECTION")
    {
        string? cs = Environment.GetEnvironmentVariable(connectionEnv)
            ?? throw new InvalidOperationException(
                $"Environment variable '{connectionEnv}' is not set. "
                + "Set it to your database connection string.");

        var options = new DbOptions { ConnectionString = cs };

        string? readCs = Environment.GetEnvironmentVariable("PALORM_READ_CONNECTION");
        if (readCs is not null)
            options = options with { ReadConnectionString = readCs };

        if (int.TryParse(Environment.GetEnvironmentVariable("PALORM_COMMAND_TIMEOUT"), out int cmdTimeout))
            options = options with { CommandTimeout = TimeSpan.FromSeconds(cmdTimeout) };

        if (int.TryParse(Environment.GetEnvironmentVariable("PALORM_CONNECTION_TIMEOUT"), out int connTimeout))
            options = options with { ConnectionTimeout = TimeSpan.FromSeconds(connTimeout) };

        if (int.TryParse(Environment.GetEnvironmentVariable("PALORM_MAX_RETRIES"), out int retries))
            options = options with { MaxRetries = retries };

        if (int.TryParse(Environment.GetEnvironmentVariable("PALORM_CIRCUIT_BREAKER_THRESHOLD"), out int cbThreshold))
            options = options with { CircuitBreakerThreshold = cbThreshold };

        if (int.TryParse(Environment.GetEnvironmentVariable("PALORM_MAX_POOL_SIZE"), out int poolSize))
            options = options with { MaxPoolSize = poolSize, PoolExplicitlyConfigured = true };

        // ITM-636：环境变量是用户输入面——组装完立即 Validate（非法值在此报，
        // 而非延迟到 CreateAsync）。预设方法为编译期字面量，正确性由代码审查保证。
        options.Validate();
        return options;
    }

    /// <summary>日志工厂。设置后 DataSession 经其创建 ILogger。
    /// 级别过滤请在 LoggerFactory 配置（AddFilter / appsettings Logging 节），
    /// 那里的过滤对全部日志出口生效。</summary>
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; init; }

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
    /// 覆写后连接串与会话级 SQL（可能内嵌敏感字面量，ITM-650）以掩码代替，
    /// 日志/异常/调试器场景不泄露密码。</summary>
    public override string ToString()
        => $"DbOptions {{ ConnectionString = ***MASKED***, " +
           $"ReadConnectionString = {(ReadConnectionString is null ? "null" : "***MASKED***")}, " +
           $"SessionSetupSql = {(SessionSetupSql is null ? "null" : "***MASKED***")}, " +
           $"ReadSessionSetupSql = {(ReadSessionSetupSql is null ? "null" : "***MASKED***")}, " +
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
