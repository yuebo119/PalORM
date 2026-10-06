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
    /// <summary>已追加语句（SQL 文本 + 实参载体）。<b>参数物化延后（step14，2026-10-03）</b>：
    /// 原实现存 <c>DbParameter[]</c>——Append 每条语句建 N 个参数对象，执行时再 <c>CloneParameter</c>
    /// 克隆一遍，同一批参数被分配两次。PerfHub 实测该形态每条语句比 ADO 臂多 1.2KB（SQLite）/
    /// 1.68KB（PG），而延迟比值仅 1.02（纯分配问题）。改为直接持有调用方的
    /// <see cref="FormattableString"/>（本就已分配，零额外开销），参数在执行期一次物化。
    /// <para>值语义不变：FormattableString 是纯值载体（Append 后调用方无法改动其内容），
    /// 重复执行每次重建参数，与"每执行克隆一份"逐位等价。</para></summary>
    private readonly List<(string Sql, FormattableString? Values)> _statements = [];
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
        // step14：只存 SQL 文本与实参载体，参数对象留到执行期物化（见 _statements 注释）
        lock (_gate) _statements.Add((formatted, sql));
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
        lock (_gate) _statements.Add((sql, null));
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
        List<(string Sql, FormattableString? Values)> statements;
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
            try
            {
                // ITM-832（r23 实修）：Timeout 赋值移入 try——setter 抛异常（驱动验证拒绝）
                // 时 DbBatch 原形态永不 Dispose；try/finally 保证批对象释放不受赋值影响。
                batch.Timeout = _session.BatchCommandTimeoutSeconds;
                // 慢 DDL 场景（大表 CREATE INDEX）不应走批——MigrateAsync 的索引 DDL 保持逐条。
                foreach ((string sql, FormattableString? values) in statements)
                {
                    DbBatchCommand command = batch.CreateBatchCommand();
                    command.CommandText = sql;
                    AddParameters(command.Parameters, values);
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
        List<(string Sql, FormattableString? Values)> statements,
        DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        if (TProvider.Dialect != SqlDialect.Sqlite || statements.Count <= 1)
            return null;

        foreach ((string _, FormattableString? values) in statements)
            if (values is not null)
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
        List<(string Sql, FormattableString? Values)> statements,
        DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        // 命令复用仅限 SQLite（L37 优化的适用面）：本回退路径对已知方言只有 SQLite 可达；
        // 未知方言（第三方 Provider + 无 DbBatch）的驱动可能有语句缓存（auto-prepare 族），
        // 复用命令 + Clear 换新参数实例是 R-UNNESTB 静默错值形态——逐条新建命令免疫。
        // （2026-10-06，10-05 审计 P3 边界收口）
        bool reuseSafe = TProvider.Dialect == SqlDialect.Sqlite;
        DbCommand? shared = reuseSafe ? connection.CreateCommand() : null;
        try
        {
            int total = 0;
            foreach ((string sql, FormattableString? values) in statements)
            {
                // ITM-887（r24）：循环内不再 await using——SQLite 复用路径（shared 非 null）下
                // 每迭代末的 Dispose 会释放 shared（次迭代起用已释放命令，违反 DbCommand 契约且
                // 清空驱动语句缓存使 L37 复用收益每迭代归零，finally 还构成二次 Dispose）。
                // 复用路径的所有权归方法级 finally；逐条新建路径（shared null）保持每迭代释放。
                DbCommand cmd = shared ?? connection.CreateCommand();
                try
                {
                    cmd.Transaction = transaction;
                    cmd.CommandTimeout = _session.BatchCommandTimeoutSeconds;
                    if (!string.Equals(cmd.CommandText, sql, StringComparison.Ordinal))
                        cmd.CommandText = sql;
                    // PARAM-REUSE-OK[nodbbatch] shared 仅 SQLite 方言可达（无 auto-prepare 行为）；
                    // 非 SQLite 走逐条新建命令（cmd 每条新实例，无复用面）
                    cmd.Parameters.Clear();
                    AddParameters(cmd.Parameters, values);
                    total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    if (shared is null)
                        await cmd.DisposeAsync().ConfigureAwait(false);
                }
            }
            return total;
        }
        finally
        {
            if (shared is not null)
                await shared.DisposeAsync().ConfigureAwait(false);
        }
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

    /// <summary>把一条语句的实参物化为参数并加入目标集合（step14，2026-10-03）。
    /// <para>参数对象从 scratch 命令创建（驱动同型：scratch 由会话连接创建，批量/回退命令同连接）；
    /// 参数对象不可跨集合共享，故每次执行都新建——这与原实现"Append 建一份 + 执行克隆一份"的
    /// 值语义一致，但把两次分配压成一次。命名与单条路径同纪律（@p0 起、值只进 @pN）。</para>
    /// <para><paramref name="values"/> 为 null 表示无参语句（<see cref="AppendRaw"/>）。</para></summary>
    private void AddParameters(DbParameterCollection target, FormattableString? values)
    {
        if (values is null)
            return;
        for (int i = 0; i < values.ArgumentCount; i++)
        {
            DbParameter parameter = _scratch.CreateParameter();
            parameter.ParameterName = QueryBuilder<object>.GetParameterName(i);
            parameter.Value = values.GetArgument(i) ?? DBNull.Value;
            target.Add(parameter);
        }
    }

    /// <summary>释放 scratch 命令。已追加语句随实例废弃；未执行的语句不会到达服务器。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scratch.Dispose();
    }
}
