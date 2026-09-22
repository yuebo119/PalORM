using System.ComponentModel;
using System.Data.Common;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.DapperSuite;

/// <summary>PalORM 臂——与官方 Dapper 基准同语义的对照项。
/// <para>官方基准全为同步 API；PalORM 公共 API 是 async-only（与 Dapper 的形态差异
/// 本身就是对照的一部分），基准返回 Task 由 BDN 原生支持。</para>
/// <para>列名进文本段的 Where 条件（B78）：列名拼进 FormattableString 文本段而非插值项。</para></summary>
[Description("PalORM")]
public class PalOrmBenchmarks : BenchmarkBase
{

    [GlobalSetup]
    public void Setup()
    {
        BaseSetup();
    }
    [Benchmark(Description = "FirstOrDefault<T>")]
    public async Task<Post?> FirstOrDefaultAsync()
    {
        Step();
        return Dialect switch
        {
            "sqlite" => await Database.SqliteSession(Connection)
                .From<Post>().Where(WhereId(i)).FirstOrDefaultAsync().ConfigureAwait(false),
            "mysql" => await Database.MySqlSession(Connection)
                .From<Post>().Where(WhereId(i)).FirstOrDefaultAsync().ConfigureAwait(false),
            _ => await Database.PgSession(Connection)
                .From<Post>().Where(WhereId(i)).FirstOrDefaultAsync().ConfigureAwait(false)
        };
    }

    [Benchmark(Description = "Query<T> (buffered)")]
    public async Task<Post> QueryBufferedAsync()
    {
        Step();
        System.Collections.Generic.List<Post> rows = Dialect switch
        {
            "sqlite" => await Database.SqliteSession(Connection)
                .From<Post>().Where(WhereId(i)).ToListAsync().ConfigureAwait(false),
            "mysql" => await Database.MySqlSession(Connection)
                .From<Post>().Where(WhereId(i)).ToListAsync().ConfigureAwait(false),
            _ => await Database.PgSession(Connection)
                .From<Post>().Where(WhereId(i)).ToListAsync().ConfigureAwait(false)
        };
        return rows[0];
    }

    [Benchmark(Description = "QueryFirst<T>")]
    public async Task<Post> QueryFirstAsync()
    {
        Step();
        return Dialect switch
        {
            "sqlite" => await Database.SqliteSession(Connection)
                .From<Post>().Where(WhereId(i)).FirstAsync().ConfigureAwait(false),
            "mysql" => await Database.MySqlSession(Connection)
                .From<Post>().Where(WhereId(i)).FirstAsync().ConfigureAwait(false),
            _ => await Database.PgSession(Connection)
                .From<Post>().Where(WhereId(i)).FirstAsync().ConfigureAwait(false)
        };
    }

    /// <summary>Id = @p0 条件——列名走文本段（与 PerfHub Dataset.WhereId 同防 B78 坑）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FormattableString WhereId(int id)    {
        string quoted = Database.Dialect == "mysql" ? "`Id`" : "\"Id\"";
        return FormattableStringFactory.Create(quoted + " = {0}", id);
    }
}
