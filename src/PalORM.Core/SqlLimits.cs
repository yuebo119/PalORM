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

    /// <summary>IN 子句分批大小——规避各数据库单条 IN 片段的参数上限（ITM 批切分基线）。
    /// <para>用于<b>单条语句内</b>拼 IN 片段（受 SQL 长度与可读性约束）。每批一次独立往返的路径
    /// （如 BulkDelete）请用 <see cref="MaxBindParametersFor"/> 与 <see cref="MaxRowsPerBatch"/> 组合，
    /// 不要复用本值——那是把单语句约束误用到多语句场景（BULK-001，2026-09-23）。</para></summary>
    public const int InClauseBatchSize = 500;

    /// <summary>方言级绑定参数上限（BULK-001，2026-09-23）：SQLite 取保守值 999——引擎编译
    /// 选项 <c>MAX_VARIABLE_NUMBER=32766</c>（3.32+ 默认值，2026-09-25 引擎探针实测确认）
    /// 虽支持大值，但 2026-09-26 PerfHub 同轮 A/B 实测证伪：32766 使 SQLite 批量路径劣化
    /// BulkInsert 6.08×、UpsertBatch 15.80×、BulkDelete 1.84×（999 臂全部回 1.00~1.03×，
    /// ADO 臂逐位稳定无环境漂移）——单语句参数绑定成本随参数数超线性，减少语句往返的收益
    /// 远不抵绑定开销。PG/MySQL 取协议上限 <see cref="MaxBindParameters"/>。
    /// 批量路径按批切分必须用它——用全局上限会在 SQLite 上越界。</summary>
    public static int MaxBindParametersFor(SqlDialect dialect)
        => dialect == SqlDialect.Sqlite ? 999 : MaxBindParameters;

    /// <summary>单批行数上限（BULK-001，2026-09-23）：参数上限只约束"参数个数"，不约束
    /// <b>语句文本规模</b>——CASE WHEN 形态的批量 UPDATE 文本按 O(行数×列数) 增长，
    /// 16383 行 × 3 列的批可生成 MB 级单语句，客户端构建、驱动解析、服务端解析全线性膨胀。
    /// 5000 行把单语句控制在数百 KB 量级，同时让 10 万行只需 20 次往返（旧口径 200 次）。</summary>
    public const int MaxRowsPerBatch = 5000;

    /// <summary>MySQL 裸 OFFSET（无 LIMIT）的补位哨兵——<c>LIMIT offset, max</c> 惯用法。</summary>
    public const ulong MySqlOffsetOnlyLimit = ulong.MaxValue;
}
