using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>会话级环境归一：把 MySQL 服务端 <c>local_infile</c> 置 ON，让 provider 的
/// 批量写入路径不依赖容器默认值。
/// <para><b>为什么需要</b>：<c>mysql:8.4</c> 镜像该变量默认 OFF，本地容器默认 ON。测试套件里
/// 大量 <c>BulkInsertAsync</c> 用例不关心走 BulkCopy 还是多值回退（两条路径都验证行数与值），
/// 于是 MySql 模块的行覆盖读数由"容器默认值 + 用例调度"决定：同一提交在 CI 上稳定读到
/// 66.85%、本地稳定读到 68.80%，差的 7 行全在 <c>ExecuteBulkCopyAsync</c> 与
/// <c>FallbackOrRethrowAsync</c>（run 41676178297 / 41688638985 的 cov-integration.xml 逐函数对账）。
/// 双模态读数与 <c>assert-coverage</c> 的 1pp 自收紧上限互斥——不存在同时满足
/// <c>floor ≤ 两模态下限</c> 与 <c>两模态上限 - floor ≤ 1pp</c> 的地板值，只能让读数确定。</para>
/// <para><b>为什么是会话级</b>：逐用例置 ON 已在三条 BulkCopy 用例里做过，但覆盖率是"任意用例
/// 只要走到该行即算覆盖"，其余几十条 bulk 用例仍按默认值分流。归一化放会话入口一处，
/// 所有用例看到的初值一致。</para>
/// <para><b>与既有排除机制的关系</b>：本钩子在所有用例之前跑一次，不参与并行调度；
/// 需要 OFF 窗口的用例（ITM-892 回退探针、批容量探针）仍在方法体内自己 <c>SET GLOBAL</c>
/// 并在 finally 还原——它们保存的是"当前值"（此时已归一为 ON），还原后回到 ON，语义不变。</para>
/// <para><b>失败静默</b>：MySQL 不可用（如只跑 SQLite-only 通道）时不阻断套件——归一化是
/// 为覆盖率稳定性服务的，不该成为新的失败点。服务端拒绝 <c>SET GLOBAL</c>（无 SUPER 权限）时
/// 同样静默：那是环境差异，报错会掩盖真正的测试结果。</para></summary>
internal static class MySQLSessionDefaults
{
    [Before(HookType.TestSession)]
    public static async Task NormalizeLocalInfileAsync()
    {
        try
        {
            await using var db = await TestDb.MySqlAsync();
            await db.ExecuteAsync($"SET GLOBAL local_infile=ON");
            PalORM.MySql.MySqlProvider.ResetLocalInfileProbeCache();
        }
        catch (Exception)
        {
            // 见类注释「失败静默」
        }
    }
}