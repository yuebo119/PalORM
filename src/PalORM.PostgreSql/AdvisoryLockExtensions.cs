using System.Data.Common;
using Npgsql;

namespace PalORM.PostgreSql;

/// <summary>v5.0 阶段 5.5b：PostgreSQL 咨询锁（advisory lock）扩展方法。
/// <para><b>背景</b>：PG 咨询锁是应用层显式获取的锁，与行锁/表锁正交。适合跨表不变量、
/// 队列式负载、限流、串行化临界区等场景。详见
/// <a href="https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-ADVISORY-LOCKS">PG 文档</a>。</para>
/// <para><b>XactLock vs 普通锁</b>：本扩展仅提供 <c>pg_advisory_xact_lock</c>——
/// 事务级锁，事务结束（COMMIT/ROLLBACK）时<strong>自动释放</strong>，无需显式 unlock，
/// 不会因异常或忘记释放而死锁。需要跨事务锁请用 <c>pg_advisory_lock</c>（不在本扩展范围）。</para>
/// <para><b>使用前提</b>：调用方必须已在事务内（<see cref="DataSession{TProvider}.BeginTransactionAsync"/>）。
/// 未在事务内调用会获得锁但立即释放（PG 在隐式事务边界结束）——本扩展不阻止这种无意义用法，
/// 但文档明确建议在显式事务内使用。</para>
/// <para><b>阻塞语义</b>：<c>pg_advisory_xact_lock</c> 阻塞至获取锁为止。
/// 调用方需自行用 <c>CancellationToken</c> 控制超时（命令级 CommandTimeout 不直接生效，
/// PG 咨询锁不受 statement_timeout 治理；锁等待由 <c>lock_timeout</c> 或 idle_in_transaction_session_timeout 治理）。</para></summary>
public static class AdvisoryLockExtensions
{
    /// <summary>获取事务级咨询锁（单 bigint key 版本）。
    /// <para>阻塞至获取锁。事务结束自动释放。锁空间与双 int key 版本独立（同 key 不冲突）。</para></summary>
    /// <param name="session">会话（必须在事务内）。</param>
    /// <param name="key">锁键（64 位有符号整数）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async ValueTask AcquireXactLockAsync(
        this DataSession<PostgreSqlProvider> session, long key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // FormattableString 重载：key 经 ExecuteAsync 参数化为 @p0，非字符串拼接（SQL 注入安全）
        await session.ExecuteAsync($"SELECT pg_advisory_xact_lock({key})", ct).ConfigureAwait(false);
    }

    /// <summary>获取事务级咨询锁（双 int key 版本）。
    /// <para>阻塞至获取锁。事务结束自动释放。双键允许分区命名空间（如 key1=租户ID，key2=资源ID）。
    /// 锁空间与单 bigint key 版本独立——同 key 不冲突。</para></summary>
    /// <param name="session">会话（必须在事务内）。</param>
    /// <param name="key1">第一键（int，0..2^31-1）。</param>
    /// <param name="key2">第二键（int，0..2^31-1）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async ValueTask AcquireXactLockAsync(
        this DataSession<PostgreSqlProvider> session, int key1, int key2, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // FormattableString 重载：key1/key2 参数化为 @p0/@p1，非字符串拼接（SQL 注入安全）
        await session.ExecuteAsync($"SELECT pg_advisory_xact_lock({key1}, {key2})", ct).ConfigureAwait(false);
    }

    /// <summary>尝试获取事务级咨询锁（非阻塞，单 bigint key 版本）。
    /// <para>立即返回：成功返回 true，已被占用返回 false（不阻塞）。事务结束自动释放。</para></summary>
    /// <param name="session">会话（必须在事务内）。</param>
    /// <param name="key">锁键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true=获取成功，false=已被占用。</returns>
    public static async ValueTask<bool> TryAcquireXactLockAsync(
        this DataSession<PostgreSqlProvider> session, long key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // FormattableString 重载：key 参数化为 @p0，非字符串拼接（SQL 注入安全）
        return await session.ScalarAsync<bool>($"SELECT pg_try_advisory_xact_lock({key})", ct).ConfigureAwait(false);
    }

    /// <summary>尝试获取事务级咨询锁（非阻塞，双 int key 版本）。</summary>
    /// <inheritdoc cref="TryAcquireXactLockAsync(DataSession{PostgreSqlProvider}, long, CancellationToken)"/>
    public static async ValueTask<bool> TryAcquireXactLockAsync(
        this DataSession<PostgreSqlProvider> session, int key1, int key2, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // FormattableString 重载：key1/key2 参数化为 @p0/@p1，非字符串拼接（SQL 注入安全）
        return await session.ScalarAsync<bool>(
            $"SELECT pg_try_advisory_xact_lock({key1}, {key2})", ct).ConfigureAwait(false);
    }
}
