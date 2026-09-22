using System.ComponentModel;
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
/// <para>会话口径：公开工厂 <c>DataSession&lt;TProvider&gt;.CreateAsync</c> 在 Setup 建一次、
/// 6500 次操作全程复用、CloseConnection 释放（README「创建会话」的最佳实践形态 + 口径差 D9）。
/// 查询写法即 README 所示的 <c>db.From&lt;T&gt;().Where(...)</c> 链式形态，不做任何改写。</para>
/// <para>列名进文本段的 Where 条件（B78）：列名拼进 FormattableString 文本段而非插值项。</para></summary>
[Description("PalORM")]
public class PalOrmBenchmarks : BenchmarkBase
{
    // 三个方言各存一个会话槽位——Provider 由类型静态选定，Setup 只填命中的那个。
    private DataSession<SqliteProvider> _sqlite = null!;
    private DataSession<MySqlProvider> _mysql = null!;
    private DataSession<PostgreSqlProvider> _pg = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        BaseSetup();
        // 最佳实践（README「创建会话」）：会话在范围入口用公开工厂 CreateAsync 建一次，
        // 全部操作复用，范围结束由 CloseConnection 释放——与官方基准"连接建一次复用"同口径
        switch (Database.Dialect)
        {
            case "sqlite":
                _sqlite = await Database.CreateSessionAsync<SqliteProvider>().ConfigureAwait(false);
                break;
            case "mysql":
                _mysql = await Database.CreateSessionAsync<MySqlProvider>().ConfigureAwait(false);
                break;
            default:
                _pg = await Database.CreateSessionAsync<PostgreSqlProvider>().ConfigureAwait(false);
                break;
        }
    }

    public override void CloseConnection()
    {
        // [GlobalCleanup] 不在计时内，此处同步等待异步释放；三个槽位只有命中方言的那个非空
        _sqlite?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _mysql?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _pg?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.CloseConnection();
    }
    [Benchmark(Description = "FirstOrDefault<T>")]
    public async Task<Post?> FirstOrDefaultAsync()
    {
        Step();
        return Dialect switch
        {
            "sqlite" => await _sqlite
                .From<Post>().Where(WhereId(i)).FirstOrDefaultAsync().ConfigureAwait(false),
            "mysql" => await _mysql
                .From<Post>().Where(WhereId(i)).FirstOrDefaultAsync().ConfigureAwait(false),
            _ => await _pg
                .From<Post>().Where(WhereId(i)).FirstOrDefaultAsync().ConfigureAwait(false)
        };
    }

    [Benchmark(Description = "Query<T> (buffered)")]
    public async Task<Post> QueryBufferedAsync()
    {
        Step();
        System.Collections.Generic.List<Post> rows = Dialect switch
        {
            "sqlite" => await _sqlite
                .From<Post>().Where(WhereId(i)).ToListAsync().ConfigureAwait(false),
            "mysql" => await _mysql
                .From<Post>().Where(WhereId(i)).ToListAsync().ConfigureAwait(false),
            _ => await _pg
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
            "sqlite" => await _sqlite
                .From<Post>().Where(WhereId(i)).FirstAsync().ConfigureAwait(false),
            "mysql" => await _mysql
                .From<Post>().Where(WhereId(i)).FirstAsync().ConfigureAwait(false),
            _ => await _pg
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
