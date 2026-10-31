using System.Data.Common;

namespace PalORM;

public sealed partial class DataSession<TProvider>
    where TProvider : IDbProvider
{
    /// <summary>创建编译查询计划（M3，2026-10-31 极致性能轮）——同形状、稳定参数类型的重复查询
    /// 用计划把 SQL 构建、形状哈希与参数对象创建从每调用一次降为每计划一次。
    /// <para><b>SQL 契约</b>：逐字执行，默认过滤（软删/租户）不适用（与 QueryAsync 同口径）；
    /// 多租户/软删场景请在 SQL 中自行携带条件。<paramref name="parameterNames"/> 按位置绑定，
    /// 名称须与 SQL 占位符一致且以 '@' 开头。计划是会话级对象（命令绑定主连接），
    /// 须在会话释放前 Dispose。</para></summary>
    public CompiledQuery<T, TProvider> Compile<T>(string sql, params string[] parameterNames)
        where T : class, new()
        => new(this, sql, parameterNames);

    /// <summary>计划命令（主连接；不进入直查复用槽——计划自己持有命令）。</summary>
    internal DbCommand CreateCompiledCommand() => CreateCommand();

    internal DbTransaction? CompiledTransaction => GetActiveTransaction();

    internal int CompiledTimeoutSeconds => _commandTimeoutSeconds;

    internal bool CompiledSyncRowRead => PreferSynchronousRowRead;

    internal int CompiledCapacity<T>() where T : class, new() => GetMaterializedCapacity<T>();

    internal void CompiledRecordCount<T>(int count) where T : class, new() => RecordMaterializedCount<T>(count);
}
