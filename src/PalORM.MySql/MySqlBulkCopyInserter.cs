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
                    BulkCopyTimeout = ctx.CommandTimeoutSeconds > 0 ? ctx.CommandTimeoutSeconds : 30,
                };
                // ITM-615：显式按列名映射（DataTable 列名 == 目标表列名，裸名由驱动处理
                // 标识符）——消除对 DataTable 列序与表列序一致的隐式依赖（默认按序匹配，
                // 官方 issue #1375）。PK 非首列 / [Computed]/[Timestamp] 缺席形态均列序无关。
                // 映射 = DataTable 序号（自构造，i 即列序）→ 目标表列名：目标侧按名匹配，
                // 不再依赖目标表列序与 DataTable 列序一致。
                for (int i = 0; i < allColumns.Length; i++)
                    bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, allColumns[i]));
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
