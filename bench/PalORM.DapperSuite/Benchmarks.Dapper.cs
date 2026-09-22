using System.ComponentModel;
using System.Data.Common;
using BenchmarkDotNet.Attributes;
using Dapper;

namespace PalORM.DapperSuite;

/// <summary>Dapper 臂——官方 Benchmarks.Dapper.cs 的全部基准逐位移植
///（Contrib Get 除外：Dapper.Contrib 不在中央包管理内，登记为口径差）。
/// SQL 与官方一致（"select * from Posts where Id = @Id"），标识符按
/// <see cref="Database.SelectByIdSql"/> 带引号书写——PG 会把未加引号的混合大小写标识符
/// 折叠为小写，解析不到实体名 "Posts"/"Id"。</summary>
[Description("Dapper")]
public class DapperBenchmarks : BenchmarkBase
{

    [GlobalSetup]
    public void Setup()
    {
        BaseSetup();
    }
    [Benchmark(Description = "Query<T> (buffered)")]
    public Post QueryBuffered()
    {
        Step();
        return Connection.Query<Post>(
            Database.SelectByIdSql, new { Id = i }, buffered: true).First();
    }

    [Benchmark(Description = "Query<T> (unbuffered)")]
    public Post QueryUnbuffered()
    {
        Step();
        return Connection.Query<Post>(
            Database.SelectByIdSql, new { Id = i }, buffered: false).First();
    }

    [Benchmark(Description = "Query<dynamic> (buffered)")]
    public dynamic QueryBufferedDynamic()
    {
        Step();
        return Connection.Query(
            Database.SelectByIdSql, new { Id = i }, buffered: true).First();
    }

    [Benchmark(Description = "QueryFirstOrDefault<T>")]
    public Post? QueryFirstOrDefault()
    {
        Step();
        return Connection.QueryFirstOrDefault<Post>(
            Database.SelectByIdSql, new { Id = i });
    }
}
