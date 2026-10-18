namespace PalORM.Integration.Tests;

/// <summary>local_infile 实例级全局状态的进程级互斥（2026-10-18 审计补）。
/// <para><b>为什么不用 TUnit 分组</b>：<c>[NotInParallel(key)]</c> 在同一 target 只能挂一个键
/// （AllowMultiple=false，无多键构造），且方法级属性覆盖类级而非合并。需要同时表达
/// 「迁移竞态组（ExtBulkTable）」与「全局变量组（LocalInfileGlobal）」时分组表达不了，
/// 跨类重叠实测发生：<c>SET GLOBAL local_infile=OFF</c> 的用例与 BulkCopy 用例并行时，
/// 对方探针读到 OFF 后静默降级多值路径，<c>MySqlBulkCopyInserter</c> 集成覆盖率长期为 0%。</para>
/// <para><b>用法</b>：任何读写 <c>local_infile</c> 全局值的用例，或依赖 BulkCopy 路径的用例，
/// 都在方法体最外层 <c>await LocalInfileTestGate.Gate.WaitAsync()</c>，finally 释放。</para>
/// </summary>
internal static class LocalInfileTestGate
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}
