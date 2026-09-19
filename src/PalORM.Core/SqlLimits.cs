namespace PalORM;

/// <summary>SQL 协议与批量路径的共享上限——审计 2026-09-19 CORE-010：魔法数字单点化。
/// 原先五处字面量各自硬编码（QueryBuilder / DataSession_Bulk / MySqlProvider），驱动上限
/// 变更需多点同步、漂移风险真实；收敛后单一实现点可 grep 验证。
/// <para><b>可见性</b>：标 public 而非 internal——Provider 是独立程序集需跨程序集访问。
/// 仍是 PalORM 内部 API（不写入公共文档/不保证兼容，同 <see cref="BulkOperationFramework"/> 口径）。</para></summary>
public static class SqlLimits
{
    /// <summary>单条语句绑定参数上限——PostgreSQL 协议 int16 上限（三方言最严）。
    /// 超限应分批或改临时表 JOIN；静默生成越界 SQL 会在运行期协议层才报错（ITM-514）。</summary>
    public const int MaxBindParameters = 65535;

    /// <summary>IN 子句分批大小——规避各数据库单条 IN 片段的参数上限（ITM 批切分基线）。</summary>
    public const int InClauseBatchSize = 500;

    /// <summary>MySQL 裸 OFFSET（无 LIMIT）的补位哨兵——<c>LIMIT offset, max</c> 惯用法。</summary>
    public const ulong MySqlOffsetOnlyLimit = ulong.MaxValue;
}
