using System.Globalization;
using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>FUZ-5：随机 DDL 建表与往返对照。
/// <para>按种子生成 50 张表的列类型与约束组合（列数 2 到 8、类型、可空、主键、唯一、默认值），
/// 在三方言执行建表，然后每张表插入一行、读回、按声明类型归一后比对。目的不是覆盖 DDL 语法，
/// 而是让随机组合出的表形状经过真实的「建表—插入—读回」链路：类型映射错误、约束组合不被方言
/// 接受这类问题，靠固定用例很难穷举。</para>
/// <para>不支持的组合不静默：生成器返回跳过原因并登记（例如 MySQL 的 TEXT 列加 UNIQUE 需要前缀长度，
/// 本生成器不生成该组合）。</para></summary>
public static class RandomDdlGenerator
{
    internal enum TypeKind
    {
        Integer,
        Real,
        Text,
        Boolean,
        Blob,
        Decimal,
        Timestamp
    }

    internal sealed record ColumnSpec(
        string Name, TypeKind Kind, bool NotNull, bool PrimaryKey, bool Unique, string? DefaultLiteral);

    internal sealed record TableSpec(string Name, IReadOnlyList<ColumnSpec> Columns);

    private static readonly string[] TableNames =
    [
        "rnd_alpha", "rnd_beta", "rnd_gamma", "rnd_delta", "rnd_epsilon", "rnd_zeta", "rnd_eta", "rnd_theta"
    ];

    private static readonly string[] ColumnNames = ["c0", "c1", "c2", "c3", "c4", "c5", "c6", "c7"];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394",
        Justification = "随机 DDL 用确定性伪随机数：同种子可复现是用例与对照的前提")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2245",
        Justification = "随机 DDL 需要同种子可复现：用例重放与三方言对照都依赖确定性生成")]
    internal static IReadOnlyList<TableSpec> Generate(int seed, int tableCount)
    {
        var random = new Random(seed);
        var tables = new List<TableSpec>(tableCount);
        for (int t = 0; t < tableCount; t++)
        {
            int columnCount = random.Next(2, 9);
            var columns = new List<ColumnSpec>(columnCount);
            for (int c = 0; c < columnCount; c++)
            {
                TypeKind kind = (TypeKind)random.Next(0, 7);
                bool primaryKey = c == 0 && random.Next(0, 3) == 0;   // 主键只落首列，且不是每张表都有
                columns.Add(new ColumnSpec(
                    ColumnNames[c],
                    kind,
                    NotNull: primaryKey || random.Next(0, 2) == 0,
                    PrimaryKey: primaryKey,
                    Unique: !primaryKey && random.Next(0, 5) == 0,
                    DefaultLiteral: random.Next(0, 6) == 0 ? DefaultLiteralFor(kind) : null));
            }
            tables.Add(new TableSpec($"{TableNames[t % TableNames.Length]}_{t:00}", [.. columns]));
        }
        return tables;
    }

    private static string DefaultLiteralFor(TypeKind kind) => kind switch
    {
        TypeKind.Integer => "0",
        TypeKind.Real => "0.0",
        TypeKind.Text => "'x'",
        TypeKind.Boolean => "0",
        TypeKind.Decimal => "0.00",
        _ => "'2020-01-01 00:00:00'"
    };

    /// <summary>生成建表 DDL，并把「本方言不支持的约束/默认值」记为适配项（省略该子句，表照常建）。
    /// <para>为什么不整表跳过：跳过会让「50 张表」的目标随方言漂移（MySQL 实测跳过 42%），
    /// 而跳过原因本身就是两条方言事实（TEXT/BLOB 不能进键、不能带字面量默认值），
    /// 省略子句后表仍能完成建表—插入—读回，适配项随用例输出登记。</para></summary>
    internal static (string Ddl, List<string> Adapted) BuildDdl(SqlDialect dialect, TableSpec table)
    {
        var sb = new StringBuilder();
        var tableConstraints = new List<string>();
        var columnFragments = new List<string>(table.Columns.Count);
        var adapted = new List<string>();
        sb.Append("CREATE TABLE ").Append(DialectSql.Quote(dialect, table.Name)).Append(" (");
        foreach (ColumnSpec column in table.Columns)
        {
            var columnSql = new StringBuilder();
            AppendColumn(columnSql, tableConstraints, dialect, column, adapted);
            columnFragments.Add(columnSql.ToString());
        }
        sb.Append(string.Join(", ", columnFragments));
        if (tableConstraints.Count > 0)
        {
            sb.Append(", ").Append(string.Join(", ", tableConstraints));
        }
        sb.Append(')');
        return (sb.ToString(), adapted);
    }

    private static void AppendColumn(
        StringBuilder sb, List<string> tableConstraints, SqlDialect dialect, ColumnSpec column, List<string> adapted)
    {
        string? defaultLiteral = AdaptDefaultLiteral(column.DefaultLiteral, dialect, column.Kind, column.Name, adapted);
        (bool primaryKey, bool unique) = AdaptKey(column.PrimaryKey, column.Unique, dialect, column.Kind, column.Name, adapted);

        sb.Append(DialectSql.Quote(dialect, column.Name)).Append(' ').Append(TypeName(dialect, column.Kind));
        if (column.NotNull) sb.Append(" NOT NULL");
        if (defaultLiteral is not null) sb.Append(" DEFAULT ").Append(defaultLiteral);
        if (primaryKey) tableConstraints.Add($"PRIMARY KEY ({DialectSql.Quote(dialect, column.Name)})");
        if (unique) tableConstraints.Add($"UNIQUE ({DialectSql.Quote(dialect, column.Name)})");
    }

    /// <summary>默认值的方言适配：不支持的字面量默认值省略并登记；PG 的 BOOLEAN 把 0/1 改写成 false/true
    /// （PG 实测 42804：column is of type boolean but default expression is of type integer）。</summary>
    private static string? AdaptDefaultLiteral(
        string? literal, SqlDialect dialect, TypeKind kind, string columnName, List<string> adapted)
    {
        if (literal is null) return null;
        if (dialect == SqlDialect.MySql && kind is TypeKind.Text or TypeKind.Blob)
        {
            adapted.Add($"{columnName}: MySQL 的 {kind} 列省略字面量默认值");
            return null;
        }
        if (dialect == SqlDialect.PostgreSql && kind == TypeKind.Blob)
        {
            adapted.Add($"{columnName}: PostgreSQL 的 bytea 列省略字面量默认值");
            return null;
        }
        if (dialect == SqlDialect.PostgreSql && kind == TypeKind.Boolean)
        {
            if (literal == "0") return "false";
            if (literal == "1") return "true";
            return literal;
        }
        return literal;
    }

    /// <summary>键约束的方言适配：MySQL 的 TEXT/BLOB 列进任何键都需要前缀长度，本生成器不生成前缀键。</summary>
    private static (bool PrimaryKey, bool Unique) AdaptKey(
        bool primaryKey, bool unique, SqlDialect dialect, TypeKind kind, string columnName, List<string> adapted)
    {
        if (dialect == SqlDialect.MySql
            && kind is TypeKind.Text or TypeKind.Blob
            && (primaryKey || unique))
        {
            adapted.Add($"{columnName}: MySQL 的 {kind} 列键（主键/唯一）需前缀长度，已省略");
            return (false, false);
        }
        return (primaryKey, unique);
    }

    internal static string TypeName(SqlDialect dialect, TypeKind kind) => (kind, dialect) switch
    {
        (TypeKind.Integer, SqlDialect.Sqlite) => "INTEGER",
        (TypeKind.Integer, _) => "BIGINT",
        (TypeKind.Real, SqlDialect.Sqlite) => "REAL",
        (TypeKind.Real, SqlDialect.PostgreSql) => "DOUBLE PRECISION",
        (TypeKind.Real, _) => "DOUBLE",
        (TypeKind.Text, _) => "TEXT",
        (TypeKind.Boolean, SqlDialect.Sqlite) => "INTEGER",
        (TypeKind.Boolean, SqlDialect.PostgreSql) => "BOOLEAN",
        (TypeKind.Boolean, _) => "TINYINT(1)",
        (TypeKind.Blob, SqlDialect.PostgreSql) => "BYTEA",
        (TypeKind.Blob, _) => "BLOB",
        (TypeKind.Decimal, _) => "NUMERIC(12,4)",
        (TypeKind.Timestamp, SqlDialect.PostgreSql) => "TIMESTAMPTZ",
        _ => "DATETIME"
    };
}

/// <summary>随机 DDL 的往返执行器：建表 → 插一行 → 读回 → 按类型归一比对 → 删表。</summary>
internal static class RandomDdlRunner
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394",
        Justification = "随机 DDL 用确定性伪随机数：同种子可复现是用例与对照的前提")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2245",
        Justification = "同上：插入值也按种子确定，失败时能原样重放")]
    internal static async Task<(int Created, List<string> Adapted)> RunAsync(
        DbConnection raw, SqlDialect dialect, int seed, int tableCount, CancellationToken ct)
    {
        var random = new Random(seed);
        var adapted = new List<string>();
        int created = 0;
        foreach (RandomDdlGenerator.TableSpec table in RandomDdlGenerator.Generate(seed, tableCount))
        {
            (string ddl, List<string> adaptedForTable) = RandomDdlGenerator.BuildDdl(dialect, table);
            adapted.AddRange(adaptedForTable);

            await ExecuteAsync(raw, ddl, ct: ct);
            try
            {
                await RoundTripAsync(raw, dialect, table, random, ct);
                created++;
            }
            finally
            {
                await ExecuteAsync(raw, $"DROP TABLE {DialectSql.Quote(dialect, table.Name)}", ct: ct);
            }
        }
        return (created, adapted);
    }

    private static async Task RoundTripAsync(
        DbConnection raw, SqlDialect dialect, RandomDdlGenerator.TableSpec table, Random random, CancellationToken ct)
    {
        string names = string.Join(", ", table.Columns.Select(column => DialectSql.Quote(dialect, column.Name)));
        string placeholders = string.Join(", ", Enumerable.Range(0, table.Columns.Count).Select(i => $"@p{i}"));
        var parameters = new List<(string Name, object? Value)>(table.Columns.Count);
        for (int i = 0; i < table.Columns.Count; i++)
        {
            parameters.Add(($"@p{i}", ValueFor(table.Columns[i].Kind, random)));
        }

        await ExecuteAsync(raw,
            $"INSERT INTO {DialectSql.Quote(dialect, table.Name)} ({names}) VALUES ({placeholders})", parameters, ct);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await MirrorTable.QueryAsync(
            raw, dialect, $"SELECT {names} FROM {DialectSql.Quote(dialect, table.Name)}", null, ct);

        if (rows.Count != 1)
        {
            throw new InvalidOperationException($"{table.Name}: 期望读回 1 行，实际 {rows.Count} 行");
        }
        for (int i = 0; i < table.Columns.Count; i++)
        {
            object? actual = rows[0][table.Columns[i].Name];
            object? expected = parameters[i].Value;
            if (!Equals(Normalize(actual, table.Columns[i].Kind), Normalize(expected, table.Columns[i].Kind)))
            {
                throw new InvalidOperationException(
                    $"{table.Name}.{table.Columns[i].Name}: 读回 {actual ?? "(null)"}（{actual?.GetType().Name ?? "null"}）"
                    + $" ≠ 插入 {expected ?? "(null)"}（{expected?.GetType().Name ?? "null"}）");
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394",
        Justification = "同上：插入值按种子确定，重放依赖确定性")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2245",
        Justification = "同上：同种子同值")]
    private static object? ValueFor(RandomDdlGenerator.TypeKind kind, Random random) => kind switch
    {
        RandomDdlGenerator.TypeKind.Integer => random.Next(-1000, 1001),
        RandomDdlGenerator.TypeKind.Real => random.NextDouble() * 2000d - 1000d,
        RandomDdlGenerator.TypeKind.Text => $"t{random.Next(0, 1000)}",
        RandomDdlGenerator.TypeKind.Boolean => random.Next(0, 2) == 0,
        RandomDdlGenerator.TypeKind.Blob => new byte[] { (byte)random.Next(0, 256), (byte)random.Next(0, 256) },
        RandomDdlGenerator.TypeKind.Decimal => random.Next(-1000, 1001) / 100m,
        _ => new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(random.Next(0, 100_000))
    };

    private static object? Normalize(object? value, RandomDdlGenerator.TypeKind kind)
    {
        switch (kind)
        {
            case RandomDdlGenerator.TypeKind.Boolean:
                if (value is bool flag) return flag ? 1L : 0L;
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case RandomDdlGenerator.TypeKind.Blob:
                return value is byte[] bytes ? Convert.ToHexString(bytes) : value;
            case RandomDdlGenerator.TypeKind.Timestamp:
                // 三方言读回形态不同（SQLite TEXT、PG timestamptz、MySQL datetime），统一成 UTC ticks：
                // 字符串按 AssumeUniversal 解析，避免本地时区把同一时刻换算成不同值。
                if (value is DateTimeOffset offset) return offset.UtcDateTime.Ticks;
                if (value is DateTime time) return time.Ticks;
                if (value is string text
                    && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                {
                    return parsed.Ticks;
                }
                return value;
            case RandomDdlGenerator.TypeKind.Decimal:
            case RandomDdlGenerator.TypeKind.Real:
            case RandomDdlGenerator.TypeKind.Integer:
                return NormalizeNumeric(value, kind);
            default:
                return value;
        }
    }

    private static object NormalizeNumeric(object? value, RandomDdlGenerator.TypeKind kind)
    {
        // Decimal：SQLite 的 NUMERIC 亲和会把小数读回 REAL，统一按 decimal 比较。
        // 不能用 Convert.ToDecimal(double)：它会完整搬入二进制误差（8.88d → 8.8800000000000007816），
        // 按最短往返字符串解析才得到 8.88m（与 RowComparer 的口径一致）。
        if (kind == RandomDdlGenerator.TypeKind.Decimal)
        {
            if (value is decimal decimalValue) return decimalValue;
            if (value is double rawDouble
                && decimal.TryParse(rawDouble.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out decimal fromDouble))
            {
                return fromDouble;
            }
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        if (kind == RandomDdlGenerator.TypeKind.Real && value is double doubleValue) return doubleValue;
        if (kind == RandomDdlGenerator.TypeKind.Integer) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (value is double asDouble) return asDouble;
        return value ?? DBNull.Value;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "SQL 由本文件的编译期常量拼接，标识符经 DialectSql.Quote 引用，不接收外部输入")]
    private static async Task ExecuteAsync(
        DbConnection raw, string sql,
        IReadOnlyList<(string Name, object? Value)>? parameters = null, CancellationToken ct = default)
    {
        if (raw.State != ConnectionState.Open) await raw.OpenAsync(ct);
        await using DbCommand command = raw.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach ((string name, object? value) in parameters)
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>SQLite 通道：随机 DDL 不需要真库，跟着 SQLite-only 通道跑。</summary>
[Property("Category", "RandomDdl")]
[NotInParallel("RandomDdl")]
public sealed class RandomDdlTests
{
    [Test]
    public async Task Sqlite_RandomTables_CreateAndRoundTrip()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("random_ddl_sqlite");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await MirrorTable.EnsureAsync(db, SqlDialect.Sqlite);

        (int created, List<string> adapted) = await RandomDdlRunner.RunAsync(
            db.GetRawConnection(), SqlDialect.Sqlite, seed: 20261104, tableCount: 50, default);

        await Assert.That(created).IsEqualTo(50)
            .Because($"建表成功 {created}/50，方言适配 {adapted.Count} 项：{string.Join(" ; ", adapted.Take(5))}");
    }
}

/// <summary>真库通道：PG 与 MySQL 各 50 张随机表。</summary>
[Property("Category", "ExternalDatabase")]
[Property("Category", "RandomDdl")]
[NotInParallel("RandomDdlRemote")]
public sealed class RandomDdlRemoteTests
{
    [Test]
    public async Task PostgreSql_RandomTables_CreateAndRoundTrip()
    {
        await using var connection = new NpgsqlConnection(TestEnvironment.ResolvePostgreSqlConnectionString());
        (int created, List<string> adapted) = await RandomDdlRunner.RunAsync(
            connection, SqlDialect.PostgreSql, seed: 20261104, tableCount: 50, default);
        await Assert.That(created).IsEqualTo(50)
            .Because($"建表成功 {created}/50，方言适配 {adapted.Count} 项：{string.Join(" ; ", adapted.Take(5))}");
    }

    [Test]
    public async Task MySql_RandomTables_CreateAndRoundTrip()
    {
        await using var connection = new MySqlConnection(TestEnvironment.ResolveMySqlConnectionString());
        (int created, List<string> adapted) = await RandomDdlRunner.RunAsync(
            connection, SqlDialect.MySql, seed: 20261104, tableCount: 50, default);
        await Assert.That(created).IsEqualTo(50)
            .Because($"建表成功 {created}/50，方言适配 {adapted.Count} 项：{string.Join(" ; ", adapted.Take(5))}");
    }
}
