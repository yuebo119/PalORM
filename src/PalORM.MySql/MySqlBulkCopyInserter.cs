using System.Data;
using System.Data.Common;
using MySqlConnector;

namespace PalORM.MySql;

/// <summary>v5.0 阶段 4.2：MySQL 批量插入——MySqlBulkCopy（LOAD DATA LOCAL INFILE 协议）。
/// <para><b>设计</b>：MySqlBulkCopy 走 LOAD DATA INFILE / 二进制行协议，批量插入比多值 INSERT
/// 快约 4.84 倍（roadmap 基准）。</para>
/// <para><b>调用判据</b>：由 MySqlProvider.BulkInsertAsync 检测 local_infile=ON 后调用（无阈值）。
/// local_infile=OFF 时走多值 INSERT 路径。</para>
/// <para><b>DataTable 路径</b>：用 metadata.BindInsert 把每行实体绑定到 DbCommand，提取参数值
/// 填入 DataTable。比自实现 IDataReader 简单且 MySqlBulkCopy 对 DataTable 路径有专门优化。</para>
/// <para><b>事务语义</b>：调用方传入的 transaction 一并使用；未传时内部开新事务包整批。
/// BulkCopy 失败整批回滚。</para></summary>
internal static class MySqlBulkCopyInserter
{
    /// <summary>执行批量插入。</summary>
    public static async Task<long> ExecuteAsync<T>(
        MySqlConnection conn,
        MySqlTransaction? transaction,
        IReadOnlyList<T> entities,
        MySqlBulkCopyContext ctx,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);

        // ITM-710(r20)：按 batchSize 分批物化 + 提交——此前把整表一次性灌进 DataTable 单条
        // LOAD DATA（batchSize 未进入本方法签名，契约静默失效），百万行场景内存峰值可达 OOM。
        // 与 MultiValueBulkInsert 的分批语义对齐（每批一次 WriteToServer）。
        int batchSize = Math.Max(1, ctx.BatchSize);
        long totalInserted = 0;
        for (int start = 0; start < entities.Count; start += batchSize)
        {
            int end = Math.Min(start + batchSize, entities.Count);
            totalInserted += await ExecuteBatchAsync(conn, transaction, entities, start, end, ctx, ct)
                .ConfigureAwait(false);
        }
        return totalInserted;
    }

    private static async Task<long> ExecuteBatchAsync<T>(
        MySqlConnection conn,
        MySqlTransaction? transaction,
        IReadOnlyList<T> entities,
        int start,
        int end,
        MySqlBulkCopyContext ctx,
        CancellationToken ct)
        where T : class
    {
        // MySqlBulkCopy 走 LOAD DATA LOCAL INFILE。DataTable 必须包含目标表全部列
        // （包括 AUTO_INCREMENT 主键列），主键列填 DBNull 让 MySQL 自增。
        // ITM-615：MySqlConnector 不指定 ColumnMappings 时按 DataTable 列序匹配目标表
        // （官方 issue #1375 确认，此前"按列名匹配"注释结论错误已订正）——下方显式按列名
        // 映射，列序不再是契约。
        // 源生成器 InsertColumns 已排除自增主键，需补齐主键列到 DataTable。
        int columnCount = ctx.InsertColumns.Count;
        // 复检轮发现（预存缺陷）：非自增 PK（Guid/string 键）实体的 PK 已含于 InsertColumns
        // ——原构造重复添加列名致 DataTable 抛 DuplicateNameException。仅补 InsertColumns
        // 缺席的 PK（= 自增 PK，生成器已将其排除于 InsertColumns）。补位列仍在最前（对齐
        // MySQL 表定义：AUTO_INCREMENT 通常首列），下方 DBNull 填值与参数填值的偏移配对不变。
        string[] pksToAdd = [.. ctx.PrimaryKeyColumns
            .Where(pk => !ctx.InsertColumns.Contains(pk, StringComparer.Ordinal))];
        string[] allColumns = [.. pksToAdd, .. ctx.InsertColumns];

        var table = new DataTable();
        try
        {
            foreach (string col in allColumns)
                table.Columns.Add(col, typeof(object));

            DbCommand rowCommand = conn.CreateCommand();
            try
            {
                // probe：首次验证 binder 输出参数数与列数一致（与 MultiValueBulkInsert 对齐）。
                rowCommand.Parameters.Clear();
                ctx.Binder(rowCommand, entities[start], 0);
                if (rowCommand.Parameters.Count != columnCount)
                    throw new InvalidOperationException(
                        $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                        $"{rowCommand.Parameters.Count} parameters.");

                table.BeginLoadData();
                try
                {
                    for (int i = start; i < end; i++)
                    {
                        rowCommand.Parameters.Clear();
                        ctx.Binder(rowCommand, entities[i], 0);
                        DataRow row = table.NewRow();
                        // 补位 PK 列填 DBNull（AUTO_INCREMENT 自增；非自增 PK 已在参数值内）
                        for (int p = 0; p < pksToAdd.Length; p++)
                            row[p] = DBNull.Value;
                        // InsertColumns 列从 rowCommand.Parameters 按序填入
                        for (int c = 0; c < columnCount; c++)
                            row[c + pksToAdd.Length] = rowCommand.Parameters[c].Value;
                        table.Rows.Add(row);
                    }
                }
                finally
                {
                    table.EndLoadData();
                }

                var bulk = new MySqlBulkCopy(conn, transaction)
                {
                    DestinationTableName = ctx.QuotedTable,
                    // ITM-655(r4)：0=无限（全库契约，非 30 秒）；正数透传
                    BulkCopyTimeout = ctx.CommandTimeoutSeconds,
                };
                // ITM-615：显式按列名映射（DataTable 列名 == 目标表列名，裸名由驱动处理
                // 标识符）——消除对 DataTable 列序与表列序一致的隐式依赖（默认按序匹配，
                // 官方 issue #1375）。PK 非首列 / [Computed]/[Timestamp] 缺席形态均列序无关。
                // 映射 = DataTable 序号（自构造，i 即列序）→ 目标表列名：目标侧按名匹配，
                // 不再依赖目标表列序与 DataTable 列序一致。
                // ITM-721(r20) 反证结案：曾疑"裸列名与仓库其余路径 QuoteIdentifier 不对称"。
                // 核对驱动源码（MySqlConnector MySqlBulkCopy.cs）：ColumnMappings 的
                // DestinationColumn 在非表达式形态下由驱动执行 QuoteIdentifier（反引号包裹 +
                // 内嵌反引号翻倍）；此处传入已引用名会导致双重引用（`` `order` `` 被当作字面量）。
                // 故裸名是正确契约，保留。
                for (int i = 0; i < allColumns.Length; i++)
                    bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, allColumns[i]));
                MySqlBulkCopyResult result = await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
                // ITM-709(r20)：驱动文档明示"MySqlBulkCopy 用户应检查 Warnings 为非空，
                // 否则可能因数据类型转换失败静默丢数据"。当前只取行数会把"截断/转换失败"
                // 报成成功——非空即显式失败，避免静默数据损坏。
                if (result.Warnings.Count > 0)
                {
                    // ITM-777(r21)：不回显 Warnings[0].Message 原文——MySQL 1366 等告警内嵌
                    // 被拒原始值（"Incorrect integer value: '<value>'"），若该列是 [SensitiveData]，
                    // 真实值会随异常消息进日志（掩码只作用于参数面，不覆盖此路径）。
                    // 只报数量/错误码/Level，内容留给调用方按需自查。
                    var first = result.Warnings[0];
                    throw new InvalidOperationException(
                        $"MySqlBulkCopy reported {result.Warnings.Count} warning(s); data may have been " +
                        $"truncated or converted incorrectly. First warning: ErrorCode={first.ErrorCode}, Level={first.Level} " +
                        "(message content redacted to avoid echoing rejected values).");
                }
                // ITM-656(r4)：MySqlConnector 服务端不报行数时返 -1——规范化为本批实体数
                long inserted = result.RowsInserted;
                return inserted >= 0 ? inserted : end - start;
            }
            finally
            {
                await rowCommand.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            table.Dispose();
        }
    }
}

/// <summary>MySqlBulkCopy 上下文——参数打包避免 S107（>7 参数方法）。</summary>
internal readonly struct MySqlBulkCopyContext(
    string quotedTable,
    IReadOnlyList<string> insertColumns,
    IReadOnlyList<string> primaryKeyColumns,
    Action<DbCommand, object, int> binder,
    int commandTimeoutSeconds,
    int batchSize)
{
    public readonly string QuotedTable = quotedTable;
    public readonly IReadOnlyList<string> InsertColumns = insertColumns;
    /// <summary>主键列（通常为 AUTO_INCREMENT），DataTable 中填 NULL 让 MySQL 自增。</summary>
    public readonly IReadOnlyList<string> PrimaryKeyColumns = primaryKeyColumns;
    public readonly Action<DbCommand, object, int> Binder = binder;
    public readonly int CommandTimeoutSeconds = commandTimeoutSeconds;
    /// <summary>ITM-710：每批最多物化的实体数——与回退多值路径同口径。</summary>
    public readonly int BatchSize = batchSize;
}
