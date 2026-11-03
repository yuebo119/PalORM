namespace PalORM.Integration.Tests;

/// <summary>local_infile 实例级全局状态的进程级互斥（2026-10-18 审计补）。
/// <para><b>为什么不用 TUnit 分组</b>：<c>[NotInParallel(key)]</c> 在同一 target 只能挂一个键
/// （AllowMultiple=false，无多键构造），且方法级属性覆盖类级而非合并。需要同时表达
/// 「迁移竞态组（ExtBulkTable）」与「全局变量组（LocalInfileGlobal）」时分组表达不了，
/// 跨类重叠实测发生：<c>SET GLOBAL local_infile=OFF</c> 的用例与 BulkCopy 用例并行时，
/// 对方探针读到 OFF 后静默降级多值路径，<c>MySqlBulkCopyInserter</c> 集成覆盖率长期为 0%。</para>
/// <para><b>用法</b>：任何读写 <c>local_infile</c> 全局值的用例，或依赖 BulkCopy 路径的用例，
/// 都在方法体最外层 <c>await LocalInfileTestGate.Gate.WaitAsync()</c>，finally 释放。</para>
/// <para><b>2026-11-03 更正（上段第一句已过期）</b>：TUnit 1.76 已支持多键
/// <c>[NotInParallel(new[] { "A", "B" })]</c>（官方 XML doc 有该示例，本仓已实测编译通过），
/// 且多键不会丢掉类级键——两个键同时生效。因此新代码应优先用多键属性（框架级保证），
/// Gate 信号量只作跨程序集或无法表达为键的场景的兜底。</para>
/// <para><b>本次实测教训</b>：<c>ExternalDatabaseBulkTests</c> 的两条 MySQL BulkCopy 用例
/// 此前只挂方法级 <c>LocalInfileGlobal</c>，按"方法级覆盖类级"的旧语义丢了类级
/// <c>ExtBulkTable</c>，于是与本类的 <c>DROP TABLE ext_bulk_entities</c> 并行，
/// <c>BulkAtomicityTests</c> 的 <c>MigrateAsync</c>（建全 Registry 表）撞"表不存在"，
/// 表现为随机 1/4 概率的单用例失败。已改为多键。</para>
/// </summary>
internal static class LocalInfileTestGate
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}
