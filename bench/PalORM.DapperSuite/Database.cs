using System.Data.Common;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.DapperSuite;

/// <summary>方言注册与 Posts 表播种——幂等（表内非 5000 行才重建），
/// BDN 每个基准方法一个进程，只有首个进程付播种成本。</summary>
internal static class Database
{
    public const int RowCount = 5000;   // 官方 Step() 的轮转上界

    public static string Dialect { get; } =
        Environment.GetEnvironmentVariable("DAPPER_SUITE_DIALECT")?.ToLowerInvariant() ?? "sqlite";

    /// <summary>官方 SQL（"select * from Posts where Id = @Id"）的方言适配。
    /// <para>官方基准只支持 SQL Server：那里大小写不敏感，未加引号的标识符解析到 Posts/Id 两个对象。
    /// PG 把未加引号的标识符折叠为小写（posts/id），解析不到实体名 <c>"Posts"</c>/<c>"Id"</c>——
    /// 实测 Dapper/HandCoded 两臂全项 <c>42P01: relation "posts" does not exist</c>。
    /// 三目标方言统一带引号书写，引用与 PalORM 生成 SQL 完全相同的对象（同一张表、同名列）。
    /// 语句语义与官方一致，只有标识符引用方式按方言适配。</para>
    /// <para>静态初始化顺序：本字段必须声明在 <see cref="Dialect"/> 之后（文本序即初始化序）。</para></summary>
    public static string SelectByIdSql { get; } = $"select * from {Q("Posts")} where {Q("Id")} = @Id";

    public static DbConnection Open()
    {
        DbConnection conn = Dialect switch
        {
            "sqlite" => new SqliteConnection(
                $"Data Source={Path.Combine(Path.GetTempPath(), "palorm-dappersuite.db")}"),
            "mysql" => new MySqlConnection(
                MySqlConnString.WithLocalInfile(Env("PALORM_MYSQL_CONNECTION"))),
            "pg" => new NpgsqlConnection(Env("PALORM_PG_CONNECTION")),
            _ => throw new InvalidOperationException($"未知方言 '{Dialect}'（sqlite/mysql/pg）")
        };
        conn.Open();
        return conn;
    }

    /// <summary>PalORM 会话工厂——按方言分派到具体 Provider（官方基准单连接复用口径）。</summary>
    public static DataSession<TProvider> Session<TProvider>(DbConnection conn) where TProvider : IDbProvider, new()
        => new(conn, new DbOptions { ConnectionString = conn.ConnectionString ?? "" }, [], null);

    public static DataSession<SqliteProvider> SqliteSession(DbConnection c) => Session<SqliteProvider>(c);
    public static DataSession<MySqlProvider> MySqlSession(DbConnection c) => Session<MySqlProvider>(c);
    public static DataSession<PostgreSqlProvider> PgSession(DbConnection c) => Session<PostgreSqlProvider>(c);

    private static string Env(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"未设置 {name}——mysql/pg 档需要连接串（与集成测试同一口径，见 .env.test）。");
        }
        return value;
    }

    private static string Q(string identifier) => Dialect == "mysql" ? $"`{identifier}`" : $"\"{identifier}\"";

    /// <summary>幂等播种：官方未定义 Posts 的建库脚本（官方默认 SQL Server 已备好库），
    /// 本套件按官方 Post 形状生成三方言同构 DDL。</summary>
    public static async Task EnsureSeededAsync(DbConnection conn)
    {
        long count = 0;
        await using (DbCommand probe = conn.CreateCommand())
        {
            probe.CommandText = $"SELECT COUNT(*) FROM {Q("Posts")}";
            try
            {
                count = Convert.ToInt64(await probe.ExecuteScalarAsync().ConfigureAwait(false));
            }
            catch (DbException)
            {
                count = -1;   // 表不存在
            }
        }
        if (count == RowCount) return;

        await ExecAsync(conn, "DROP TABLE IF EXISTS " + Q("Posts")).ConfigureAwait(false);
        await ExecAsync(conn, Dialect switch
        {
            "sqlite" => "CREATE TABLE \"Posts\" (\"Id\" INTEGER PRIMARY KEY, \"Text\" TEXT, "
                + "\"CreationDate\" TEXT NOT NULL, \"LastChangeDate\" TEXT NOT NULL, "
                + string.Join(", ", Enumerable.Range(1, 9).Select(n => $"\"Counter{n}\" INTEGER")) + ")",
            "mysql" => "CREATE TABLE `Posts` (`Id` INT PRIMARY KEY, `Text` TEXT, "
                + "`CreationDate` DATETIME(6) NOT NULL, `LastChangeDate` DATETIME(6) NOT NULL, "
                + string.Join(", ", Enumerable.Range(1, 9).Select(n => $"`Counter{n}` INT")) + ")",
            "pg" => "CREATE TABLE \"Posts\" (\"Id\" INT PRIMARY KEY, \"Text\" TEXT, "
                + "\"CreationDate\" TIMESTAMP NOT NULL, \"LastChangeDate\" TIMESTAMP NOT NULL, "
                + string.Join(", ", Enumerable.Range(1, 9).Select(n => $"\"Counter{n}\" INTEGER")) + ")",
            _ => throw new InvalidOperationException(Dialect)
        }).ConfigureAwait(false);

        const int batch = 250;
        for (int start = 1; start <= RowCount; start += batch)
        {
            int end = Math.Min(start + batch, RowCount + 1);
            var sql = new System.Text.StringBuilder();
            sql.Append("INSERT INTO ").Append(Q("Posts")).Append(" (")
                .Append(Dialect == "mysql"
                    ? "`Id`, `Text`, `CreationDate`, `LastChangeDate`, `Counter1`, `Counter2`, `Counter3`, `Counter4`, `Counter5`, `Counter6`, `Counter7`, `Counter8`, `Counter9`"
                    : "\"Id\", \"Text\", \"CreationDate\", \"LastChangeDate\", \"Counter1\", \"Counter2\", \"Counter3\", \"Counter4\", \"Counter5\", \"Counter6\", \"Counter7\", \"Counter8\", \"Counter9\"")
                .Append(") VALUES ");
            await using DbCommand cmd = conn.CreateCommand();
            int p = 0;
            for (int i = start; i < end; i++)
            {
                if (i > start) sql.Append(", ");
                sql.Append('(');
                for (int c = 0; c < 13; c++)
                {
                    if (c > 0) sql.Append(", ");
                    sql.Append($"@p{p + c}");
                }
                sql.Append(')');
                Post post = Post.Seed(i);
                AddP(cmd, p++, post.Id);
                AddP(cmd, p++, (object?)post.Text ?? DBNull.Value);
                AddP(cmd, p++, post.CreationDate);
                AddP(cmd, p++, post.LastChangeDate);
                AddP(cmd, p++, (object?)post.Counter1 ?? DBNull.Value);
                AddP(cmd, p++, (object?)post.Counter2 ?? DBNull.Value);
                AddP(cmd, p++, (object?)post.Counter3 ?? DBNull.Value);
                AddP(cmd, p++, (object?)post.Counter4 ?? DBNull.Value);
                AddP(cmd, p++, (object?)post.Counter5 ?? DBNull.Value);
                AddP(cmd, p++, (object?)post.Counter6 ?? DBNull.Value);
                AddP(cmd, p++, (object?)post.Counter7 ?? DBNull.Value);
                AddP(cmd, p++, (object?)post.Counter8 ?? DBNull.Value);
                AddP(cmd, p++, (object?)post.Counter9 ?? DBNull.Value);
            }
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    internal static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static void AddP(DbCommand cmd, int index, object? value)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = $"@p{index}";
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}

/// <summary>连接串 AllowLoadLocalInfile 追加（与 PerfHub 同口径；本套件实际不触发 BulkCopy，
/// 保留是为与 PerfHub 环境完全一致）。</summary>
internal static class MySqlConnString
{
    public static string WithLocalInfile(string cs)
        => cs.Contains("AllowLoadLocalInfile", StringComparison.OrdinalIgnoreCase)
            ? cs
            : cs.TrimEnd(';') + ";AllowLoadLocalInfile=true";
}
