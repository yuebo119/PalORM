using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace PalORM.Scaffold;

/// <summary>SQLite schema 提供者——通过 sqlite_master + PRAGMA table_info 获取表结构。
/// <para>SQLite 是弱类型——PRAGMA table_info 返回的 type 列是"声明类型"（如 INTEGER/TEXT），
/// 非真实存储类型。由 <see cref="TypeMapper.MapSqlite"/> 按亲和性映射。</para></summary>
internal sealed class SqliteSchemaProvider : ISchemaProvider
{
    public SchemaDialect Dialect => SchemaDialect.Sqlite;

    public async Task<IReadOnlyList<SchemaTable>> GetTablesAsync(
        DbConnection connection, CancellationToken ct = default)
    {
        var tables = new List<SchemaTable>();

        // 获取所有用户表（排除 sqlite_% 系统表）
        using DbCommand tablesCmd = connection.CreateCommand();
        tablesCmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using DbDataReader tablesReader = await tablesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var tableNames = new List<string>();
        while (await tablesReader.ReadAsync(ct).ConfigureAwait(false))
            tableNames.Add(tablesReader.GetString(0));
        await tablesReader.DisposeAsync().ConfigureAwait(false);

        foreach (string tableName in tableNames)
        {
            var columns = new List<SchemaColumn>();
            using DbCommand colCmd = connection.CreateCommand();
            // PRAGMA 参数化不可靠（部分 SQLite 版本不支持参数），用引号包裹标识符。
            // 表名来自 sqlite_master，不含注入风险（数据库自身结构）。
            colCmd.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
            using DbDataReader colReader = await colCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await colReader.ReadAsync(ct).ConfigureAwait(false))
            {
                // PRAGMA table_info 列序：cid, name, type, notnull, dflt_value, pk
                string colName = colReader.GetString(1);
                string dbType = await colReader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? "" : colReader.GetString(2);
                bool notNull = colReader.GetInt64(3) != 0;
                int pk = colReader.GetInt32(5);  // pk=0 非主键；>0 主键序号
                bool isNullable = !notNull;

                // SQLite 自增：INTEGER PRIMARY KEY AUTOINCREMENT——PRAGMA 不暴露 autoincrement 标记。
                // 检测：PK + 类型 INTEGER + notnull（自增隐含 not null）。
                bool isAutoIncrement = pk > 0
                    && string.Equals(dbType, "INTEGER", StringComparison.OrdinalIgnoreCase);

                columns.Add(new SchemaColumn(colName, dbType, pk > 0, isAutoIncrement, isNullable));
            }
            await colReader.DisposeAsync().ConfigureAwait(false);

            tables.Add(new SchemaTable(tableName, columns));
        }
        return tables;
    }
}
