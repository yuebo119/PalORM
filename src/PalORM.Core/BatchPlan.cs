namespace PalORM;

/// <summary>单语句批量写入的批宽计算——<b>单一真源</b>（2026-11-23，架构审查 D3）。
/// <para><b>为什么需要</b>：批宽算式原在 <c>DataSession_Bulk</c> 内六处各写一遍（BulkDelete 的 IN 形态、
/// BulkUpdateBatch、BulkUpdate、版本化 BulkUpdate、BulkInsertReturning、Upsert）。同一「租户参数未扣减」
/// off-by-one 历史上被修过四次（ITM-809/883/884/r22），每修一处都要重新推导其余五处是否同源——
/// 这是可机械消除的缺陷族，不是可读性问题。</para>
/// <para><b>六处实测同形</b>：<c>min(max(1, (paramLimit - tenantParams) / parametersPerRow), rowCap)</c>。
/// 差异只在三个入参：每行参数数（IN 形态为 1、UPDATE 为 SET 列 + 主键、版本化 +1、UPSERT 为列数）、
/// 租户参数个数（0 或 1）、行数上限（<see cref="SqlLimits.MaxRowsPerBatch"/> 或按形态/方言的专用上限）。
/// 整数除法下 <c>floor(min(a, b*c)/c) == min(floor(a/c), b)</c>，故原「先乘后除」的写法与本实现等价。</para>
/// <para><b>与 IN 子句分批评分的分工</b>：<see cref="SqlLimits.InClauseBatchSize"/> 用于<b>单条语句内</b>
/// 拼 IN 片段（受 SQL 长度约束），本类用于「每批一次独立往返」的行数切分——两者不可互换
/// （BULK-001 已登记该混淆形态）。</para></summary>
internal static class BatchPlan
{
    /// <summary>单语句批宽：参数上限与行数上限双约束取较小者，至少 1 行。
    /// <para><paramref name="tenantParams"/> 必须包含该语句<b>所有</b>非行参数（租户过滤位于池尾，
    /// 不扣减则租户实体在参数恰好满批时越界一个）。</para>
    /// <para><paramref name="parametersPerRow"/> 为每行占用的参数个数（含每行内的主键/版本列）。</para></summary>
    internal static int RowsPerStatement(
        SqlDialect dialect, int tenantParams, int parametersPerRow, int rowCap)
    {
        System.Diagnostics.Debug.Assert(parametersPerRow > 0, "每行参数个数必须为正");
        System.Diagnostics.Debug.Assert(rowCap > 0, "行数上限必须为正");
        int parameterLimited = (SqlLimits.MaxBindParametersFor(dialect) - tenantParams) / parametersPerRow;
        int rows = Math.Min(Math.Max(1, parameterLimited), rowCap);
        return rows;
    }
}
