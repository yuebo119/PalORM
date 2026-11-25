namespace PalORM;

/// <summary>写入批命令时的文本适配——单一定义点（P0-1，2026-11-25）。
/// <para><b>要解决的问题</b>：MySqlConnector 3.0.0 的 <c>MySqlBatch</c> 在批内
/// <b>每条</b>命令文本都已在本连接预编译过时，会从「拼接成单个协议包」切换到
/// <c>SingleCommandPayloadCreator</c> 逐条发送；而批命令对象
/// （<c>MySqlBatchCommand</c>）的 <c>TryGetPreparedStatements()</c> 恒返回 null，
/// 逐条发出去的于是退回文本协议。净效果是这批语句既没有单包往返、也没拿到预编译，
/// 只多付 N 次往返（实测 100 条点查 3.39 ms → 26.85 ms；产品查询管道 20 条
/// 2.02 ms → 6.30 ms，且比不用管道还慢 21%）。
/// PalORM 的查询复用槽晋升时会对 MySQL 执行 <c>PrepareAsync</c>
/// （<see cref="IDbProvider.PrepareReusedCommands"/>），而驱动池让预编译状态跨请求存活，
/// 因此热查询参与的批会稳定命中该形态。</para>
/// <para><b>做法</b>：给写入 <c>DbBatchCommand.CommandText</c> 的文本追加一个尾随空格，
/// 使驱动的精确文本匹配不命中预编译缓存，批始终保持单包拼接。尾随空格对 SQL 语义无
/// 影响，也不进入 <c>SqlShapeCache</c> 等以子句文本为键的缓存——本函数只作用于
/// 「写入批命令」这一瞬间，单条 <c>DbCommand</c> 仍用原文本以保留预编译收益。</para>
/// <para><b>为什么只对 MySQL 特殊处理</b>：PostgreSQL 的 <c>NpgsqlBatch</c> 是协议级真批，
/// 且 Npgsql 的自动预编译按 SQL 文本作缓存键（<c>MaxAutoPrepare</c> /
/// <c>AutoPrepareMinUsages</c>）——给 PG 的批文本加空格会让每批都表现为「新形状」，
/// 反而阻止自动预编译生效。SQLite 不走 <c>DbBatch</c>（<c>SessionBatch.TryCreateBatch</c>
/// 按方言静态返回 null）。</para>
/// <para><b>维护约束</b>：所有写入 <c>DbBatchCommand.CommandText</c> 的路径都必须经过本函数，
/// 由 <c>BatchCommandTextWiringTests</c> 机械扫描 <c>src/PalORM.Core</c> 校验（漏接即红）。</para></summary>
internal static class BatchCommandText
{
    /// <summary>把 <paramref name="sql"/> 适配为批命令文本：MySQL 方言追加一个尾随空格，
    /// 其余方言原样返回。</summary>
    /// <param name="dialect">当前 Provider 的 SQL 方言。</param>
    /// <param name="sql">即将写入 <see cref="System.Data.Common.DbBatchCommand.CommandText"/> 的语句文本。</param>
    internal static string For(SqlDialect dialect, string sql)
        => dialect == SqlDialect.MySql ? sql + " " : sql;
}
