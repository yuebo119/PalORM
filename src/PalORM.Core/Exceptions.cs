namespace PalORM;

/// <summary>PalORM 基础异常。</summary>
public class PalORMException : Exception
{
    /// <summary>以错误描述创建异常。</summary>
    public PalORMException(string message) : base(message) { }

    /// <summary>以错误描述和原始异常创建异常——保留底层失败原因供调用方追溯。</summary>
    public PalORMException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>熔断器打开异常。</summary>
public sealed class CircuitBreakerOpenException : PalORMException
{
    /// <summary>以熔断状态描述（含恢复时间点）创建异常。</summary>
    public CircuitBreakerOpenException(string message) : base(message) { }
}

/// <summary>并发冲突异常（乐观锁）。</summary>
public sealed class ConcurrencyConflictException : PalORMException
{
    /// <summary>以冲突描述（实体/版本信息）创建异常。</summary>
    public ConcurrencyConflictException(string message) : base(message) { }
}
