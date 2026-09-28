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

/// <summary>唯一约束冲突（R4，v6.0）——PG 23505 / MySQL 1062 / SQLite 扩展码 2067/1555 的
/// 跨方言统一翻译，InsertAsync/SaveAsync（新增行）撞唯一键或主键重复时抛出。
/// <para><see cref="Exception.InnerException"/> 保留驱动原生异常（纯翻译不吞，约束名等
/// 方言细节从中读取——跨方言不抽象约束名字段）。非唯一约束违规（NOT NULL/FK/CHECK）
/// 不抛本类型——SQLite 主码 19 涵盖全部约束违规，按扩展码精确判定（ITM-403）。</para></summary>
public sealed class UniqueConstraintViolationException : PalORMException
{
    internal UniqueConstraintViolationException(Exception inner)
        : base("Unique constraint violation. See InnerException for provider details.", inner) { }
}
