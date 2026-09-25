using System.Data.Common;

namespace PalORM;

/// <summary>事务/多语句场景的显式批量执行器——把 N 条语句的往返压成一次（方言支持时）。
/// <para><b>收益实测（PG 远程，事务内 10 条 INSERT，60 轮中位）</b>：逐条 4.56 ms →
/// 批量 1.33 ms（**3.4×**，每语句消除 ~324µs RTT）。语句数越多、RTT 越大，收益越大。</para>
/// <para><b>方言行为</b>：PostgreSQL 走 <c>DbBatch</c> 真单往返；MySQL 走 <c>DbBatch</c>
/// （驱动侧批处理）；SQLite 无批量 API——<b>全无参语句合并为单个多语句命令一次往返</b>
/// （L38，2026-09-25；返回值经驱动 RecordsAffected 跨语句累计，契约保持），其余回退为
/// 顺序执行且循环外复用单条命令（L37，消除每语句命令新建/释放）。回退对外不可见。</para>
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
    /// <summary>Append 与执行期快照的互斥门（OPS-002，2026-09-22）——门内只做 List 增删与拷贝，
    /// 不做 await（本仓库"lock 内无 await"纪律）。</summary>
    private readonly Lock _gate = new();
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
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        lock (_gate) _statements.Add((formatted, parameters));
        return this;
    }

    /// <summary>追加一条无参数原语语句（L4，v5.6.0，internal）——DDL 等运行时字符串不能经
    /// <see cref="Append"/>（<c>$"{ddl}"</c> 会把整句变成插值参数）。守卫与 Append 同口径。</summary>
    internal SessionBatch<TProvider> AppendRaw(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException(
                "Batch statement must not be empty or whitespace; an empty statement produces invalid SQL.",
                nameof(sql));
        lock (_gate) _statements.Add((sql, Array.Empty<DbParameter>()));
        return this;
    }

    /// <summary>执行全部已追加语句，返回累计受影响行数。
    /// 批量可重复执行（每次执行全部当前语句）。</summary>
    public ValueTask<int> ExecuteNonQueryAsync(CancellationToken ct = default)
        => ExecuteNonQueryAsync(operationOwner: null, ct);

    /// <summary>L4（v5.6.0）：携带外层操作 owner 的执行入口——持有操作租约的内部路径
    /// （如 MigrateAsync）经 owner 重入，不再与外层租约冲突。</summary>
    internal async ValueTask<int> ExecuteNonQueryAsync(object? operationOwner, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // OPS-002（2026-09-22）：门内只做快照，不在锁内 await（"lock 内无 await"纪律）。
        // 此前直接迭代 _statements：与并发 Append 竞争会结构性损坏 List，或抛
        // "Collection was modified"——后者在批量执行中途让整批事务回滚。
        // 快照语义：执行期间新追加的语句由下一次 Execute 执行。
        List<(string Sql, IReadOnlyList<DbParameter> Parameters)> statements;
        lock (_gate)
        {
            statements = [.. _statements];
        }
        if (statements.Count == 0) return 0;

        using SessionOperationState.SessionOperationLease operation =
            _session.EnterBatchOperation(operationOwner);
        DbConnection connection = _session.BatchConnection;
        DbTransaction? transaction = _session.GetActiveBatchTransaction();

        // 首选 DbBatch（PG/MySQL 驱动实现）；不支持（如 Microsoft.Data.Sqlite）回退顺序执行
        DbBatch? batch = TryCreateBatch(connection, transaction);
        if (batch is not null)
        {
            // 更正（2026-09-22，BATCH-001）：此前注释称"System.Data.Common.DbBatch 无 CommandTimeout 面"，
            // 实为误读——DbBatch.Timeout 自 .NET 8 起存在（8.0/9.0/10.0/11.0 参考程序集均有该属性），
            // 缺的是"设值"而非"API 面"：不设即按驱动默认（Npgsql 30s），会话 WithTimeout 在批路径静默失效。
            // 与回退路径同口径（含显式 Zero = 无限等待）。
            batch.Timeout = _session.BatchCommandTimeoutSeconds;
            try
            {
                // 慢 DDL 场景（大表 CREATE INDEX）不应走批——MigrateAsync 的索引 DDL 保持逐条。
                foreach ((string sql, IReadOnlyList<DbParameter> parameters) in statements)
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

        // L38：全无参语句合并单次往返（仅 SQLite；不可合并返回 null 走顺序路径）
        int? merged = await TryExecuteMergedParameterlessAsync(statements, connection, transaction, ct)
            .ConfigureAwait(false);
        if (merged is not null) return merged.Value;

        // 回退：顺序执行 + 单命令复用（L37）
        return await ExecuteSequentiallyAsync(statements, connection, transaction, ct).ConfigureAwait(false);
    }

    /// <summary>L38（2026-09-25，极致优化）：SQLite 全无参语句合并为单个多语句命令一次往返。
    /// SQLite 原生支持分号分隔多语句，驱动 prepare 循环逐条编译执行（含 CREATE TRIGGER 整体
    /// 消费），<c>RecordsAffected</c> 跨语句累计（驱动源码 ExecuteNonQuery 语义核实）→ 返回
    /// 契约保持，N 次往返压成 1 次。带参语句不合并（参数集合跨语句无归属）；混合形态与未知
    /// 方言保持逐条路径（多语句支持面未验证）。不可合并返回 null。</summary>
    private async ValueTask<int?> TryExecuteMergedParameterlessAsync(
        List<(string Sql, IReadOnlyList<DbParameter> Parameters)> statements,
        DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        if (TProvider.Dialect != SqlDialect.Sqlite || statements.Count <= 1)
            return null;

        foreach ((string _, IReadOnlyList<DbParameter> parameters) in statements)
            if (parameters.Count != 0)
                return null;

        var mergedSql = new System.Text.StringBuilder();
        for (int i = 0; i < statements.Count; i++)
        {
            if (i > 0) mergedSql.Append(";\n");
            mergedSql.Append(statements[i].Sql);
        }

        await using DbCommand mergedCmd = connection.CreateCommand();
        mergedCmd.Transaction = transaction;
        mergedCmd.CommandTimeout = _session.BatchCommandTimeoutSeconds;
        mergedCmd.CommandText = mergedSql.ToString();
        return await mergedCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>回退：顺序执行（语句顺序 = 追加顺序，行为与分别 ExecuteAsync 等价）。
    /// <para>L37（2026-09-25，极致优化）：循环外建一条命令复用——SQLite 本地 RTT≈0，命令
    /// 新建/释放是回退路径的主要固定开销；驱动语句缓存按命令实例生效且 CommandText 同值
    /// setter 短路（项15 探针实测），同文本语句免重编译。Clear + 克隆重加与逐条新建逐位等价。</para></summary>
    private async ValueTask<int> ExecuteSequentiallyAsync(
        List<(string Sql, IReadOnlyList<DbParameter> Parameters)> statements,
        DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        int total = 0;
        await using DbCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = _session.BatchCommandTimeoutSeconds;
        foreach ((string sql, IReadOnlyList<DbParameter> parameters) in statements)
        {
            if (!string.Equals(cmd.CommandText, sql, StringComparison.Ordinal))
                cmd.CommandText = sql;
            cmd.Parameters.Clear();
            foreach (DbParameter parameter in parameters)
                cmd.Parameters.Add(CloneParameter(parameter));
            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    private static DbBatch? TryCreateBatch(DbConnection connection, DbTransaction? transaction)
    {
        // BATCH-002（2026-09-23）：先做方言静态判定。Microsoft.Data.Sqlite 不覆写 CreateBatch，
        // 基类实现直接抛 NotSupportedException——每批一次异常抛出 + 栈捕获（约 10-50µs）是纯固定成本，
        // 而 SQLite 本地 RTT≈0、回退路径行为等价（见 ExecuteNonQueryAsync 注释）。
        // 未知方言（未来第三方 Provider）保留 try/catch 兜底，行为不变。
        if (TProvider.Dialect == SqlDialect.Sqlite) return null;

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
