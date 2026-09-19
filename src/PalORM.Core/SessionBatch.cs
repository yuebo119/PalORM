using System.Data.Common;

namespace PalORM;

/// <summary>事务/多语句场景的显式批量执行器——把 N 条语句的往返压成一次（方言支持时）。
/// <para><b>收益实测（PG 远程，事务内 10 条 INSERT，60 轮中位）</b>：逐条 4.56 ms →
/// 批量 1.33 ms（**3.4×**，每语句消除 ~324µs RTT）。语句数越多、RTT 越大，收益越大。</para>
/// <para><b>方言行为</b>：PostgreSQL 走 <c>DbBatch</c> 真单往返；MySQL 走 <c>DbBatch</c>
/// （驱动侧批处理）；SQLite 无批量 API——<b>回退为顺序执行</b>（行为等价，无收益也无损失，
/// 本地 RTT≈0）。回退对外不可见。</para>
/// <para><b>语义契约</b>：① 只接受非查询语句（INSERT/UPDATE/DELETE）——批量内没有
/// 每语句结果的可寻址位置，混入 SELECT 是调用方错误，执行期由驱动拒绝；
/// ② 参数化与单条路径同纪律（<c>FormattableString</c> 编译期参数化，值只进 @pN）；
/// ③ 事务：批量自动加入会话的活跃事务（有则绑定，无则裸执行——需要原子性请包
/// <c>WithTransaction</c>）；④ 返回累计受影响行数（驱动对批量的口径相加）。</para>
/// <para>用法：
/// <code>
/// using var batch = session.CreateBatch();
/// batch.Append($"INSERT INTO t (a) VALUES ({1}")")
///       .Append($"UPDATE t SET a = {2} WHERE id = {1}");
/// int affected = await batch.ExecuteNonQueryAsync();
/// </code></para></summary>
public sealed class SessionBatch<TProvider> : IDisposable
    where TProvider : IDbProvider
{
    private readonly DataSession<TProvider> _session;
    private readonly List<(string Sql, IReadOnlyList<DbParameter> Parameters)> _statements = [];
    private readonly DbCommand _scratch;
    private bool _disposed;

    internal SessionBatch(DataSession<TProvider> session)
    {
        _session = session;
        _scratch = session.CreateCommandForBatch();
    }

    /// <summary>追加一条非查询语句（编译期参数化）。可链式。</summary>
    /// <exception cref="ArgumentException">语句为空/空白（与 Where 一族的 ITM-745 守卫同口径）。</exception>
    public SessionBatch<TProvider> Append(FormattableString sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        // 每条语句参数从 @p0 起命名（批量内每条命令的参数集合独立），复用格式化纯函数缓存
        string formatted = QueryBuilder<object>.FormatFormattableSql(sql, 0);
        if (string.IsNullOrWhiteSpace(formatted))
            throw new ArgumentException(
                "Batch statement must not be empty or whitespace; an empty statement produces invalid SQL.",
                nameof(sql));
        var parameters = new DbParameter[sql.ArgumentCount];
        for (int i = 0; i < sql.ArgumentCount; i++)
        {
            DbParameter parameter = _scratch.CreateParameter();
            parameter.ParameterName = QueryBuilder<object>.GetParameterName(i);
            parameter.Value = sql.GetArgument(i) ?? DBNull.Value;
            parameters[i] = parameter;
        }
        _statements.Add((formatted, parameters));
        return this;
    }

    /// <summary>执行全部已追加语句，返回累计受影响行数。
    /// 批量可重复执行（每次执行全部当前语句）。</summary>
    public async ValueTask<int> ExecuteNonQueryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_statements.Count == 0) return 0;

        using SessionOperationState.SessionOperationLease operation = _session.EnterBatchOperation();
        DbConnection connection = _session.BatchConnection;
        DbTransaction? transaction = _session.GetActiveBatchTransaction();

        // 首选 DbBatch（PG/MySQL 驱动实现）；不支持（如 Microsoft.Data.Sqlite）回退顺序执行
        DbBatch? batch = TryCreateBatch(connection, transaction);
        if (batch is not null)
        {
            try
            {
                foreach ((string sql, IReadOnlyList<DbParameter> parameters) in _statements)
                {
                    DbBatchCommand command = batch.CreateBatchCommand();
                    command.CommandText = sql;
                    foreach (DbParameter parameter in parameters)
                        command.Parameters.Add(CloneParameter(parameter));
                    batch.BatchCommands.Add(command);
                }
                return await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await batch.DisposeAsync().ConfigureAwait(false);
            }
        }

        // 回退：逐条顺序执行（语句顺序 = 追加顺序，行为与分别 ExecuteAsync 等价）
        int total = 0;
        foreach ((string sql, IReadOnlyList<DbParameter> parameters) in _statements)
        {
            await using DbCommand cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = _session.BatchCommandTimeoutSeconds;
            cmd.CommandText = sql;
            foreach (DbParameter parameter in parameters)
                cmd.Parameters.Add(CloneParameter(parameter));
            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    private static DbBatch? TryCreateBatch(DbConnection connection, DbTransaction? transaction)
    {
        try
        {
            DbBatch batch = connection.CreateBatch();
            batch.Transaction = transaction;
            return batch;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>克隆 scratch 参数（驱动同型：scratch 由会话连接创建，批量/回退命令同连接）。
    /// 参数对象不可跨集合共享——Append 阶段是占位载体，每次执行克隆新实例，批量可重复执行。</summary>
    private DbParameter CloneParameter(DbParameter source)
    {
        DbParameter copy = _scratch.CreateParameter();
        copy.ParameterName = source.ParameterName;
        copy.Value = source.Value;
        return copy;
    }

    /// <summary>释放 scratch 命令。已追加语句随实例废弃；未执行的语句不会到达服务器。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scratch.Dispose();
    }
}
