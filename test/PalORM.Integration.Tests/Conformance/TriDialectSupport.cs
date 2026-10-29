using System.Data;
using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>三方言手写 SQL 生成（方案 §4.2 的"手写侧独立"约束落到方言层）。
/// <para>标识符引用：SQLite 与 PostgreSQL 用双引号，MySQL 用反引号（MySQL 默认未开
/// <c>ANSI_QUOTES</c>，双引号会被当成字符串字面量）。upsert 语法三方言不同，各自独立成形。</para></summary>
internal static class DialectSql
{
    public static string Quote(SqlDialect dialect, string identifier)
        => dialect == SqlDialect.MySql ? $"`{identifier}`" : $"\"{identifier}\"";

    public static string ColumnList(SqlDialect dialect, IReadOnlyList<string> columns)
        => string.Join(", ", columns.Select(c => Quote(dialect, c)));

    /// <summary>多值 INSERT 的参数占位序列：@p0, @p1 ...（三方言驱动都接受命名参数）。</summary>
    public static string Placeholders(int rowCount, int columnCount)
    {
        var rows = new string[rowCount];
        for (int row = 0; row < rowCount; row++)
        {
            var columns = new string[columnCount];
            for (int column = 0; column < columnCount; column++)
                columns[column] = $"@p{row * columnCount + column}";
            rows[row] = $"({string.Join(", ", columns)})";
        }
        return string.Join(", ", rows);
    }

    public static string Insert(
        SqlDialect dialect, string table, IReadOnlyList<string> columns, int rowCount)
        => $"INSERT INTO {Quote(dialect, table)} ({ColumnList(dialect, columns)}) "
            + $"VALUES {Placeholders(rowCount, columns.Count)}";

    public static string Upsert(
        SqlDialect dialect, string table, IReadOnlyList<string> columns, string conflictColumn, int rowCount)
    {
        string insert = Insert(dialect, table, columns, rowCount);
        if (dialect == SqlDialect.MySql)
        {
            // MySQL 8.0.19+ 的行别名形式，避免已弃用的 VALUES() 函数
            var assignments = columns
                .Where(c => !string.Equals(c, conflictColumn, StringComparison.Ordinal))
                .Select(c => $"{Quote(dialect, c)} = new.{Quote(dialect, c)}");
            return $"{insert} AS new ON DUPLICATE KEY UPDATE {string.Join(", ", assignments)}";
        }
        var updates = columns
            .Where(c => !string.Equals(c, conflictColumn, StringComparison.Ordinal))
            .Select(c => $"{Quote(dialect, c)} = excluded.{Quote(dialect, c)}");
        return $"{insert} ON CONFLICT ({Quote(dialect, conflictColumn)}) DO UPDATE SET {string.Join(", ", updates)}";
    }

    public static string UpdateColumn(
        SqlDialect dialect, string table, string setColumn, string keyColumn)
        => $"UPDATE {Quote(dialect, table)} SET {Quote(dialect, setColumn)} = @p0 "
            + $"WHERE {Quote(dialect, keyColumn)} = @p1";

    public static string DeleteByKeys(SqlDialect dialect, string table, string keyColumn, int keyCount)
        => $"DELETE FROM {Quote(dialect, table)} WHERE {Quote(dialect, keyColumn)} IN "
            + $"({string.Join(", ", Enumerable.Range(0, keyCount).Select(i => $"@p{i}"))})";

    public static string SelectAll(SqlDialect dialect, string table, IReadOnlyList<string> columns)
        => $"SELECT {ColumnList(dialect, columns)} FROM {Quote(dialect, table)}";

    public static string CreateMirror(SqlDialect dialect, string table)
    {
        // 与被测实体生成的 DDL 同构：Id 显式主键，name/amount 非空
        string id = Quote(dialect, "Id");
        string name = Quote(dialect, "name");
        string amount = Quote(dialect, "amount");
        string idType = dialect == SqlDialect.Sqlite ? "INTEGER" : "BIGINT";
        return $"CREATE TABLE IF NOT EXISTS {Quote(dialect, table)} ("
            + $"{id} {idType} PRIMARY KEY NOT NULL, "
            + $"{name} TEXT NOT NULL, "
            + $"{amount} BIGINT NOT NULL)";
    }
}

/// <summary>能力键测试专用镜像表的清理与建表（三方言共用）。</summary>
[NotInParallel("ConformanceRemote")]
internal static class MirrorTable
{
    public const string BulkTable = "cf_bulk_plain";
    public const string BulkMirror = "cf_bulk_plain_mirror";
    public const string SoftDeleteTable = "cf_soft_delete";
    public const string SoftDeleteMirror = "cf_soft_delete_mirror";

    public static async ValueTask EnsureAsync<TProvider>(
        DataSession<TProvider> db, SqlDialect dialect, CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        // 先迁移（同时把会话连接打开），再取裸连接：GetRawConnection 在未初始化时可能返回空
        await db.MigrateAsync(ct);
        System.Data.Common.DbConnection raw = db.GetRawConnection();
        await ExecuteAsync(raw, dialect, DialectSql.CreateMirror(dialect, BulkMirror), ct: ct);
        await CleanAsync(raw, dialect, ct);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "SQL 由 DialectSql 按方言常量模板生成，表名与列名都是本文件的编译期常量")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077",
        Justification = "表名为本文件编译期常量，插值不接收外部输入")]
    public static async ValueTask CleanAsync(
        DbConnection raw, SqlDialect dialect, CancellationToken ct = default)
    {
        foreach (string table in (string[])[BulkTable, BulkMirror, SoftDeleteTable, SoftDeleteMirror])
        {
            await using DbCommand command = raw.CreateCommand();
            command.CommandText = $"DELETE FROM {DialectSql.Quote(dialect, table)}";
            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (DbException)
            {
                // 某些方言上镜像表可能尚未建立（首次运行），清理是尽力而为
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "SQL 由 DialectSql 按方言常量模板生成，表名与列名都是本文件的编译期常量")]
    public static async ValueTask ExecuteAsync(
        DbConnection raw, SqlDialect dialect, string constantSql,
        IReadOnlyList<(string Name, object? Value)>? parameters = null,
        CancellationToken ct = default)
    {
        if (raw.State != ConnectionState.Open) await raw.OpenAsync(ct);
        await using DbCommand command = raw.CreateCommand();
        command.CommandText = constantSql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "SQL 由 DialectSql 按方言常量模板生成，表名与列名都是本文件的编译期常量")]
    public static async ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        DbConnection raw, SqlDialect dialect, string constantSql,
        IReadOnlyList<(string Name, object? Value)>? parameters = null,
        CancellationToken ct = default)
    {
        if (raw.State != ConnectionState.Open) await raw.OpenAsync(ct);
        await using DbCommand command = raw.CreateCommand();
        command.CommandText = constantSql;
        AddParameters(command, parameters);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.GetValue(i) is DBNull ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static void AddParameters(
        DbCommand command, IReadOnlyList<(string Name, object? Value)>? parameters)
    {
        if (parameters is null) return;
        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}

/// <summary>远程方言（PG/MySQL）的一致性用例夹具：连容器库、建全部注册表、暴露裸连接。</summary>
internal sealed class RemoteConformanceFixture<TProvider> : IAsyncDisposable
    where TProvider : IDbProvider
{
    private readonly DataSession<TProvider> _session;

    private RemoteConformanceFixture(DataSession<TProvider> session, SqlDialect dialect)
    {
        _session = session;
        Dialect = dialect;
    }

    public SqlDialect Dialect { get; }

    public DataSession<TProvider> Session => _session;

    public DbConnection Raw => _session.GetRawConnection();

    public static async ValueTask<RemoteConformanceFixture<TProvider>> CreateAsync(
        SqlDialect dialect, Func<Task<DataSession<TProvider>>> factory, CancellationToken ct = default)
    {
        DataSession<TProvider> session = await factory();
        await MirrorTable.EnsureAsync(session, dialect, ct);
        return new RemoteConformanceFixture<TProvider>(session, dialect);
    }

    public async ValueTask DisposeAsync() => await _session.DisposeAsync();
}

/// <summary>方言到夹具的创建入口，供三个 [Test] 方法各自调用。</summary>
internal static class ConformanceDialects
{
    public static async ValueTask<RemoteConformanceFixture<PostgreSqlProvider>> PostgreSqlAsync(
        CancellationToken ct = default)
        => await RemoteConformanceFixture<PostgreSqlProvider>.CreateAsync(
            SqlDialect.PostgreSql, () => TestDb.PostgreSqlAsync(ct), ct);

    public static async ValueTask<RemoteConformanceFixture<MySqlProvider>> MySqlAsync(
        CancellationToken ct = default)
        => await RemoteConformanceFixture<MySqlProvider>.CreateAsync(
            SqlDialect.MySql, () => TestDb.MySqlAsync(ct), ct);
}