using System.Data.Common;
using MySqlConnector;

namespace PalORM.Scaffold;

/// <summary>MySQL schema 提供者——通过 information_schema 获取表结构。
/// <para><b>排除</b>：系统库（mysql/sys/performance_schema/information_schema）。
/// 用户表在连接串指定的库（如 palorm_bench）。</para>
/// <para><b>类型名</b>：从 information_schema.columns 取 DATA_TYPE（如 'int'、'varchar'），
/// 不带长度——长度信息在 COLUMN_TYPE 列（如 'varchar(255)'），由 TypeMapper.SliceUntil 处理。</para>
/// <para><b>主键</b>：通过 information_schema.key_column_usage 判断。
/// <b>自增</b>：EXTRA 列含 'auto_increment'。</para></summary>
internal sealed class MySqlSchemaProvider : ISchemaProvider
{
    public SchemaDialect Dialect => SchemaDialect.MySql;

    public async Task<IReadOnlyList<SchemaTable>> GetTablesAsync(
        DbConnection connection, CancellationToken ct = default)
    {
        // 一次性查表+列+PK+自增信息。
        // 列序：table_name, column_name, data_type, is_nullable, column_key, extra
        const string sql = """
            SELECT
                c.TABLE_NAME,
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE = 'YES' AS is_nullable,
                c.COLUMN_KEY = 'PRI' AS is_pk,
                c.EXTRA LIKE '%auto_increment%' AS is_auto_increment
            FROM information_schema.COLUMNS c
            WHERE c.TABLE_SCHEMA = DATABASE()
            ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION
            """;

        using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var dict = new Dictionary<string, List<SchemaColumn>>(StringComparer.Ordinal);
        var order = new List<string>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string tableName = reader.GetString(0);
            string colName = reader.GetString(1);
            string dataType = reader.GetString(2);
            bool isNullable = reader.GetBoolean(3);
            bool isPk = reader.GetBoolean(4);
            bool isAutoIncrement = reader.GetBoolean(5);

            if (!dict.TryGetValue(tableName, out var cols))
            {
                cols = new List<SchemaColumn>();
                dict[tableName] = cols;
                order.Add(tableName);
            }
            cols.Add(new SchemaColumn(colName, dataType, isPk, isAutoIncrement, isNullable));
        }

        var result = new List<SchemaTable>(order.Count);
        foreach (string table in order)
            result.Add(new SchemaTable(table, dict[table]));
        return result;
    }
}
