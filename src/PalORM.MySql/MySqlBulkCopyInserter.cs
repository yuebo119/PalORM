using System.Data;
using System.Data.Common;
using MySqlConnector;

namespace PalORM.MySql;

/// <summary>v5.0 阶段 4.2：MySQL 大批量插入阈值分流——≥2000 行走 MySqlBulkCopy（二进制行协议）。
/// <para><b>设计</b>：MySqlBulkCopy 走 LOAD DATA INFILE / 二进制行协议，批量插入比多值 INSERT
/// 快约 4.84 倍（roadmap 基准）。但小批量（&lt;2000 行）多值 INSERT 更快（无协议初始化开销），
/// 故采用阈值分流。</para>
/// <para><b>阈值 2000</b>：来自 roadmap 基准经验（对齐 EF Core/Dapper 实践）。低于此值多值 INSERT
/// 胜出，高于此值 BulkCopy 协议开销摊薄后大幅领先。</para>
/// <para><b>DataTable 路径</b>：用 metadata.BindInsert 把每行实体绑定到 DbCommand，提取参数值
/// 填入 DataTable。比自实现 IDataReader 简单且 MySqlBulkCopy 对 DataTable 路径有专门优化。</para>
/// <para><b>事务语义</b>：调用方传入的 transaction 一并使用；未传时内部开新事务包整批。
/// BulkCopy 失败整批回滚。</para></summary>
internal static class MySqlBulkCopyInserter
{
    /// <summary>阈值：≥此行数走 MySqlBulkCopy，否则回退多值 INSERT。</summary>
    public const int BulkCopyThreshold = 2000;

    /// <summary>执行批量插入——阈值分流入口。</summary>
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

        // MySqlBulkCopy 走 LOAD DATA LOCAL INFILE，DataTable 必须包含目标表全部列
        // （包括 AUTO_INCREMENT 主键列），主键列填 DBNull 让 MySQL 自增。否则 MySqlBulkCopy
        // 内部按 DataTable 列序映射目标表列，列数不匹配会失败（已通过最小复现验证）。
        // 源生成器 InsertColumns 已排除自增主键，需补齐主键列到 DataTable。
        int columnCount = ctx.InsertColumns.Count;
        string[] allColumns = new string[columnCount + ctx.PrimaryKeyColumns.Count];
        // 主键列放最前（对齐 MySQL 表定义：AUTO_INCREMENT 通常首列）
        for (int i = 0; i < ctx.PrimaryKeyColumns.Count; i++)
            allColumns[i] = ctx.PrimaryKeyColumns[i];
        for (int i = 0; i < columnCount; i++)
            allColumns[i + ctx.PrimaryKeyColumns.Count] = ctx.InsertColumns[i];

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
                ctx.Binder(rowCommand, entities[0], 0);
                if (rowCommand.Parameters.Count != columnCount)
                    throw new InvalidOperationException(
                        $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                        $"{rowCommand.Parameters.Count} parameters.");

                table.BeginLoadData();
                try
                {
                    for (int i = 0; i < entities.Count; i++)
                    {
                        rowCommand.Parameters.Clear();
                        ctx.Binder(rowCommand, entities[i], 0);
                        DataRow row = table.NewRow();
                        // 主键列填 DBNull（AUTO_INCREMENT 自增）
                        for (int p = 0; p < ctx.PrimaryKeyColumns.Count; p++)
                            row[p] = DBNull.Value;
                        // InsertColumns 列从 rowCommand.Parameters 按序填入
                        for (int c = 0; c < columnCount; c++)
                            row[c + ctx.PrimaryKeyColumns.Count] = rowCommand.Parameters[c].Value;
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
                    BulkCopyTimeout = ctx.CommandTimeoutSeconds > 0 ? ctx.CommandTimeoutSeconds : 30,
                };
                // 不指定 ColumnMappings——MySqlBulkCopy 按 DataTable 列序自动匹配目标表列
                // （DataTable 列已对齐目标表全部列，含主键 NULL 占位）。
                MySqlBulkCopyResult result = await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
                return result.RowsInserted;
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
    int commandTimeoutSeconds)
{
    public readonly string QuotedTable = quotedTable;
    public readonly IReadOnlyList<string> InsertColumns = insertColumns;
    /// <summary>主键列（通常为 AUTO_INCREMENT），DataTable 中填 NULL 让 MySQL 自增。</summary>
    public readonly IReadOnlyList<string> PrimaryKeyColumns = primaryKeyColumns;
    public readonly Action<DbCommand, object, int> Binder = binder;
    public readonly int CommandTimeoutSeconds = commandTimeoutSeconds;
}
