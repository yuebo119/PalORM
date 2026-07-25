using System.Data.Common;
using Npgsql;

namespace PalORM.Scaffold;

/// <summary>PostgreSQL schema 提供者——通过 information_schema 获取表结构。
/// <para><b>排除的系统 schema</b>：pg_catalog / information_schema / pg_toast。
/// 用户表在 public schema（默认）或自定义 schema。</para>
/// <para><b>类型名</b>：从 information_schema.columns 取 data_type（如 'integer'、'character varying'），
/// 部分类型带 udt_name（如 '_int4' 数组）。本实现取 data_type + 处理常见别名。</para></summary>
internal sealed class PostgreSqlSchemaProvider : ISchemaProvider
{
    public SchemaDialect Dialect => SchemaDialect.PostgreSql;

    public async Task<IReadOnlyList<SchemaTable>> GetTablesAsync(
        DbConnection connection, CancellationToken ct = default)
    {
        // 一次性查表+列+PK 信息——用 information_schema + pg_catalog 组合
        // table_type 是 tables 视图的列，需 JOIN information_schema.tables
        const string sql = """
            SELECT
                c.table_schema,
                c.table_name,
                c.column_name,
                c.data_type,
                c.udt_name,
                c.is_nullable = 'YES' AS is_nullable,
                CASE WHEN tc.constraint_type = 'PRIMARY KEY' THEN true ELSE false END AS is_pk,
                c.is_identity = 'YES' AS is_identity
            FROM information_schema.columns c
            JOIN information_schema.tables t
                ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            LEFT JOIN information_schema.key_column_usage kcu
                ON kcu.table_schema = c.table_schema
                AND kcu.table_name = c.table_name
                AND kcu.column_name = c.column_name
            LEFT JOIN information_schema.table_constraints tc
                ON tc.constraint_name = kcu.constraint_name
                AND tc.table_schema = kcu.table_schema
                AND tc.constraint_type = 'PRIMARY KEY'
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """;

        using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var dict = new Dictionary<string, List<SchemaColumn>>(StringComparer.Ordinal);
        var order = new List<(string schema, string table)>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = reader.GetString(0);
            string tableName = reader.GetString(1);
            string colName = reader.GetString(2);
            string dataType = reader.GetString(3);
            string udtName = reader.GetString(4);
            bool isNullable = reader.GetBoolean(5);
            bool isPk = reader.GetBoolean(6);
            bool isIdentity = reader.GetBoolean(7);

            // PG 数组类型（如 _int4）——用 udt_name 检测
            string effectiveType = udtName.StartsWith('_', StringComparison.Ordinal)
                ? udtName : dataType;

            string qualifiedName = $"{schema}.{tableName}";
            if (!dict.TryGetValue(qualifiedName, out var cols))
            {
                cols = new List<SchemaColumn>();
                dict[qualifiedName] = cols;
                order.Add((schema, tableName));
            }
            cols.Add(new SchemaColumn(colName, effectiveType, isPk, isIdentity, isNullable));
        }

        var result = new List<SchemaTable>(order.Count);
        foreach ((string schema, string table) in order)
        {
            string qualifiedName = $"{schema}.{table}";
            result.Add(new SchemaTable(table, dict[qualifiedName]));
        }
        return result;
    }
}
