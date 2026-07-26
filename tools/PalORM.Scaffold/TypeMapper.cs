using System.Globalization;

namespace PalORM.Scaffold;

/// <summary>数据库类型 → C# 类型映射（按方言分支）。
/// <para>SQLite 弱类型：按类型亲和性分组成 INTEGER/REAL/TEXT/BLOB/NUMERIC。</para>
/// <para>PG/MySQL 强类型：按精确类型名匹配（含数组、json、uuid 等）。</para>
/// <para>未识别类型 fallback 到 string（PG/MySQL）或 object（SQLite 极端情况）。
/// 可空列返回 nullable 形态（如 "long?"），由 EntityGenerator 决定是否加 ?。</para></summary>
internal static class TypeMapper
{
    /// <summary>把 DB 类型映射为 C# 类型（不含 ? 后缀）。
    /// 返回 (csharpType, isReferenceType)：isReferenceType=true 表示引用类型（string/byte[]），
    /// null 时天然可空；isReferenceType=false 表示值类型（long/int/decimal），null 时需 ?。</summary>
    public static (string CSharpType, bool IsReferenceType) Map(string dbType, SchemaDialect dialect)
        => dialect == SchemaDialect.Sqlite
            ? MapSqlite(dbType)
            : MapRelational(dbType);

    // SQLite 类型亲和性——https://www.sqlite.org/datatype3.html#type_affinity
    private static (string, bool) MapSqlite(string dbType)
    {
        string upper = dbType.ToUpper(CultureInfo.InvariantCulture);
        // SQLite "INTEGER" 亲和性（含 BIGINT/INT8/SMALLINT 等别名）
        if (upper.Contains("INT", StringComparison.Ordinal))
            return ("long", false);
        // REAL 亲和性
        if (upper.Contains("REAL", StringComparison.Ordinal)
            || upper.Contains("FLOA", StringComparison.Ordinal)
            || upper.Contains("DOUB", StringComparison.Ordinal))
            return ("double", false);
        // NUMERIC 亲和性（含 DECIMAL）——PalORM 默认 decimal 更安全
        if (upper.Contains("DECIMAL", StringComparison.Ordinal)
            || upper.Contains("NUMERIC", StringComparison.Ordinal))
            return ("decimal", false);
        // TEXT 亲和性（含 VARCHAR/CHAR/CLOB/TEXT）
        if (upper.Contains("CHAR", StringComparison.Ordinal)
            || upper.Contains("CLOB", StringComparison.Ordinal)
            || upper.Contains("TEXT", StringComparison.Ordinal))
            return ("string", true);
        // BLOB
        if (upper == "BLOB")
            return ("byte[]", true);
        // datetime/date 等用户惯例类型——SQLite 不原生支持但常用
        if (upper.Contains("DATE", StringComparison.Ordinal)
            || upper.Contains("TIME", StringComparison.Ordinal))
            return ("System.DateTime", false);
        // BOOLEAN affinity（SQLite 不原生支持但常用）
        if (upper.Contains("BOOL", StringComparison.Ordinal))
            return ("bool", false);
        // fallback：TEXT 亲和性默认
        return ("string", true);
    }

    // PG/MySQL 精确类型映射——按 dbType 小写形态匹配，分派到 4 个类别辅助方法
    private static (string, bool) MapRelational(string dbType)
    {
        // 去掉长度/精度后缀（如 'varchar(255)' → 'varchar'，'numeric(10,2)' → 'numeric'）
        string baseType = dbType.AsSpan().SliceUntil('(').ToString().Trim().ToLowerInvariant();
        // PG 数组类型（如 '_int4'、'text[]'）——fallback 数组暂用 string
        if (baseType.StartsWith('_', StringComparison.Ordinal) || baseType.EndsWith("[]", StringComparison.Ordinal))
            return ("string", true);

        // bit(1) → bool（惯例），bit(n>1) → string（位串非布尔，避免数据丢失）
        if (baseType == "bit" || baseType == "varbit" || baseType == "bit varying")
        {
            // 从原始 dbType 提取长度：bit(1) → 1, bit(64) → 64
            int lenStart = dbType.IndexOf('(');
            if (lenStart >= 0)
            {
                string lenStr = dbType.AsSpan()[(lenStart + 1)..].SliceUntil(')').ToString().Trim();
                if (int.TryParse(lenStr, out int bitLen) && bitLen > 1)
                    return ("string", true);  // bit(n>1) 是位串，不是布尔
            }
            return ("bool", false);  // bit(1) 或无长度信息 → bool
        }

        return TryNumeric(baseType)
            ?? TryDateTime(baseType)
            ?? TryStringAndBinary(baseType)
            // fallback：未识别类型（如 PG tsvector/domain/xml schema）默认 string。
            // 运行时类型转换失败时回溯到此——EntityGenerator 应对 fallback 列加 TODO 注释。
            ?? ("string", true);
    }

    // 数值类型：整数 + 浮点 + 精确小数 + 布尔
    private static (string, bool)? TryNumeric(string t) => t switch
    {
        "smallint" or "int2" => ("short", false),
        "integer" or "int" or "int4" => ("int", false),
        "bigint" or "int8" => ("long", false),
        "tinyint" => ("byte", false),  // MySQL tinyint(1) 常被当 bool，但显式 byte 更精确
        "real" or "float4" or "float" => ("float", false),
        "double precision" or "float8" or "double" => ("double", false),
        "decimal" or "numeric" or "money" => ("decimal", false),
        "boolean" or "bool" => ("bool", false),
        _ => null
    };

    // 日期/时间类型
    private static (string, bool)? TryDateTime(string t) => t switch
    {
        "date" => ("System.DateOnly", false),
        "time" or "time without time zone" => ("System.TimeOnly", false),
        "timetz" or "time with time zone" => ("System.TimeSpan", false),
        "timestamp" or "timestamp without time zone" => ("System.DateTime", false),
        "timestamptz" or "timestamp with time zone" or "datetime" => ("System.DateTime", false),
        "interval" => ("System.TimeSpan", false),
        _ => null
    };

    // 字符串/字节/特殊类型（uuid/json/xml/inet/enum）
    private static (string, bool)? TryStringAndBinary(string t) => t switch
    {
        "uuid" => ("System.Guid", false),
        "bytea" or "blob" or "binary" or "varbinary" or "tinyblob" or "mediumblob" or "longblob" => ("byte[]", true),
        "text" or "varchar" or "char" or "bpchar" or "character varying" or "character"
            or "tinytext" or "mediumtext" or "longtext" => ("string", true),
        "json" or "jsonb" or "xml" or "inet" or "cidr" or "enum" or "set" => ("string", true),
        _ => null
    };
}

/// <summary>ReadOnlySpan 辅助——截取到指定字符前。</summary>
internal static class SpanExtensions
{
    public static ReadOnlySpan<char> SliceUntil(this ReadOnlySpan<char> span, char delimiter)
    {
        int idx = span.IndexOf(delimiter);
        return idx < 0 ? span : span[..idx];
    }
}
