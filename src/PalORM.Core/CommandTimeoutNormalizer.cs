using System.Diagnostics;

namespace PalORM;

/// <summary>命令超时的统一归一（D4 与 D7）。
/// <para><b>D4 解包</b>：驱动把命令超时包在自身异常里（Npgsql 的 <c>NpgsqlException</c> 内层是超时异常），
/// 而连接超时直接抛 <see cref="TimeoutException"/>。这里解包，使调用方按超时类型捕获时能覆盖
/// 弹性路径与直通路径两种情形。</para>
/// <para><b>D7 耗时判定</b>：MySQL 驱动在 <c>CommandTimeout</c> 到期时中断查询，但<b>不抛异常</b>，
/// 而是返回部分结果（实测 <c>SELECT SLEEP(5)</c> 在 1 秒后返回 1）。原生 <c>CommandTimeout</c> 与
/// CTS 取消两条路都是这个行为，属驱动语义。因此在该方言上按耗时判定：耗时达到命令超时即视为超时。
/// 计时用 <see cref="Stopwatch.GetTimestamp"/>（无分配），且只在 MySQL 且设置了命令超时时启用，
/// 其他方言与未设超时的场景零开销。</para></summary>
internal static class CommandTimeoutNormalizer
{
    /// <summary>是否需要按耗时判定（仅 MySQL 且命令超时为正）。</summary>
    internal static bool RequiresElapsedCheck(SqlDialect dialect, int timeoutSeconds)
        => dialect == SqlDialect.MySql && timeoutSeconds > 0;

    /// <summary>开始计时；不需要判定时返回 0（表示未启用）。</summary>
    internal static long Begin(bool enabled) => enabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>执行操作，成功返回时做耗时判定，异常路径做超时解包。</summary>
    internal static async ValueTask<T> RunAsync<T>(
        ValueTask<T> operation, long startTimestamp, int timeoutSeconds)
    {
        try
        {
            T result = await operation.ConfigureAwait(false);
            if (startTimestamp != 0L && timeoutSeconds > 0)
            {
                double elapsedSeconds =
                    (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
                if (elapsedSeconds >= timeoutSeconds)
                {
                    throw new TimeoutException(
                        $"Command exceeded its {timeoutSeconds}s timeout: the provider interrupted the query "
                        + "but returned a partial result instead of throwing (MySQL semantics).");
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is not TimeoutException && ex.InnerException is TimeoutException timeout)
        {
            throw new TimeoutException(timeout.Message, ex);
        }
    }
}