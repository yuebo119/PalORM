using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;
using System.Data.Common;

namespace PalORM.Core.Tests;

/// <summary>v5.0 阶段 5.4：AuditInterceptor 单元测试。
/// 用 StubLogger 捕获日志输出，验证三段式（Before/After/Error）契约。</summary>
public sealed class AuditInterceptorTests
{
    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        await Assert.That(() => new AuditInterceptor(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Priority_Default_Is200()
    {
        var interceptor = new AuditInterceptor(new StubLogger());
        await Assert.That(interceptor.Priority).IsEqualTo(200);
    }

    [Test]
    public async Task OnBefore_LogsSqlWithoutParams_ByDefault()
    {
        var logger = new StubLogger();
        var interceptor = new AuditInterceptor(logger);
        var ctx = new QueryContext("SELECT 1", EmptyParams);

        interceptor.OnBefore(ctx);

        await Assert.That(logger.Entries.Count).IsEqualTo(1);
        await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Information);
        await Assert.That(logger.Entries[0].Message).Contains("SELECT 1");
        await Assert.That(logger.Entries[0].Message.Contains("Params", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task OnBefore_WithLogParameters_IncludesParamValues()
    {
        var logger = new StubLogger();
        var interceptor = new AuditInterceptor(logger, logParameters: true);
        // 构造带参数的 QueryContext——用 SqliteParameter 测试真实格式化
        var parameters = new List<DbParameter> { new SqliteParameter("@p0", 42) };
        var ctx = new QueryContext("SELECT @p0", parameters);

        interceptor.OnBefore(ctx);

        await Assert.That(logger.Entries[0].Message).Contains("@p0=42");
    }

    [Test]
    public async Task OnAfter_LogsRowCountAndElapsed()
    {
        var logger = new StubLogger();
        var interceptor = new AuditInterceptor(logger);
        var ctx = new QueryContext("SELECT 1", EmptyParams);

        interceptor.OnAfter(ctx, TimeSpan.FromMilliseconds(12.5), 5);

        await Assert.That(logger.Entries[0].Message).Contains("5 rows");
        await Assert.That(logger.Entries[0].Message).Contains("12.50ms");
    }

    [Test]
    public async Task OnError_LogsExceptionTypeAndMessage_WhenLogParametersTrue()
    {
        // logParameters=true 时记录完整异常消息
        var logger = new StubLogger();
        var interceptor = new AuditInterceptor(logger, logParameters: true);
        var ctx = new QueryContext("BAD SQL", EmptyParams);
        var ex = new InvalidOperationException("syntax error");

        interceptor.OnError(ctx, ex);

        await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Error);
        await Assert.That(logger.Entries[0].Message).Contains("InvalidOperationException");
        await Assert.That(logger.Entries[0].Message).Contains("syntax error");
        await Assert.That(logger.Entries[0].Exception).IsSameReferenceAs(ex);
    }

    [Test]
    public async Task OnError_LogsOnlyExceptionType_WhenLogParametersFalse()
    {
        // v5.0 P1-4 修复：logParameters=false 时不写 exception.Message（可能含参数值）
        var logger = new StubLogger();
        var interceptor = new AuditInterceptor(logger);  // logParameters=false（默认）
        var ctx = new QueryContext("BAD SQL", EmptyParams);
        var ex = new InvalidOperationException("syntax error with secret@email.com");

        interceptor.OnError(ctx, ex);

        await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Error);
        await Assert.That(logger.Entries[0].Message).Contains("InvalidOperationException");
        await Assert.That(logger.Entries[0].Message.Contains("secret@email.com", StringComparison.Ordinal)).IsFalse();
        // ITM-611：不传 exception 实例——标准 provider 渲染 exception.ToString() 含 Message（PII 击穿）
        await Assert.That(logger.Entries[0].Exception).IsNull();
    }

    [Test]
    public async Task OnBefore_IsLoggerDisabled_NoOutput()
    {
        // ILogger.IsEnabled=false 时跳过日志（性能优化）
        var logger = new StubLogger { EnabledLevel = LogLevel.Warning };  // 只接受 Warning+
        var interceptor = new AuditInterceptor(logger);
        var ctx = new QueryContext("SELECT 1", EmptyParams);

        interceptor.OnBefore(ctx);

        await Assert.That(logger.Entries.Count).IsEqualTo(0);
    }

    // 复用空参数列表，避免每次构造新 List
    private static readonly List<DbParameter> EmptyParams = [];
}

/// <summary>简单日志 stub——捕获所有日志条目供断言。</summary>
internal sealed class StubLogger : ILogger
{
    public LogLevel EnabledLevel { get; set; } = LogLevel.Trace;
    public List<LogEntry> Entries { get; } = [];
    private readonly Lock _lock = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= EnabledLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // List<T> 非线程安全——审计拦截器可能并发调用，加锁保护。
        lock (_lock)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}
