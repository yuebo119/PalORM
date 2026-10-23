using System.Data.Common;

namespace PalORM;

public sealed partial class DataSession<TProvider>
{
    /// <summary>创建请求级只读查询管道——把多条独立查询合并为一次往返（Provider 支持时）。
    /// 见 <see cref="QueryPipeline{TProvider}"/> 的语义契约与能力门禁。</summary>
    public QueryPipeline<TProvider> CreatePipeline() => new(this);

    /// <summary>管道使用的主连接（管道是会话级单活动操作，与并行读作用域互斥）。</summary>
    internal DbConnection PipelineConnection => _conn;

    /// <summary>管道执行时的活动事务（无事务为 null）。</summary>
    internal DbTransaction? PipelineTransaction => GetActiveTransaction();

    /// <summary>会话默认命令超时——管道整批超时取各查询请求值与此值的最大者。</summary>
    internal TimeSpan PipelineCommandTimeout => _options.CommandTimeout;

    /// <summary>并行读作用域是否活动——管道使用主连接，与作用域内的并发读不可重叠。</summary>
    internal bool PipelineParallelReadScopeActive => _operationState.ParallelReadsEnabled;

    /// <summary>管道执行的操作租约（只读租约，与现有查询入口同语义）。</summary>
    internal SessionOperationState.SessionOperationLease EnterPipelineRead()
        => _operationState.EnterReadOnly();

    /// <summary>管道顺序回退路径的命令工厂（超时/事务由 CreateCommand 统一设置）。</summary>
    internal DbCommand CreatePipelineCommand() => CreateCommand();
}
