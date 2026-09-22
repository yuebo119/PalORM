using BenchmarkDotNet.Attributes;
using System.Data;
using System.Data.Common;

namespace PalORM.DapperSuite;

/// <summary>手写 ADO.NET 基线——官方 Benchmarks.HandCoded.cs 的跨方言移植。
/// <para>与官方逐点一致：GlobalSetup 预建命令并 Prepare、参数复用只写 Value、
/// CommandBehavior.SingleResult|SingleRow、13 列按序号手工物化、DataTable 变体。</para>
/// <para>方言适配登记：官方 "select Top 1" 是 SQL Server 语法，三目标方言用 LIMIT 1 等价；
/// 标识符按 <see cref="Database.SelectByIdSql"/> 带引号书写（PG 会把未加引号的混合大小写
/// 标识符折叠为小写，解析不到实体名 "Posts"/"Id"）。</para></summary>
public class HandCodedBenchmarks : BenchmarkBase
{
    private DbCommand _postCommand = null!;
    private DbParameter _idParam = null!;
    private DataTable _table = null!;

    [GlobalSetup]
    public void Setup()
    {
        BaseSetup();
        _postCommand = Connection.CreateCommand();
        _postCommand.CommandText = Database.SelectByIdSql + " limit 1";
        _idParam = _postCommand.CreateParameter();
        _idParam.ParameterName = "@Id";
        _idParam.DbType = DbType.Int32;
        _postCommand.Parameters.Add(_idParam);
        _postCommand.Prepare();
        _table = new DataTable
        {
            Columns =
            {
                { "Id", typeof(int) },
                { "Text", typeof(string) },
                { "CreationDate", typeof(DateTime) },
                { "LastChangeDate", typeof(DateTime) },
                { "Counter1", typeof(int) },
                { "Counter2", typeof(int) },
                { "Counter3", typeof(int) },
                { "Counter4", typeof(int) },
                { "Counter5", typeof(int) },
                { "Counter6", typeof(int) },
                { "Counter7", typeof(int) },
                { "Counter8", typeof(int) },
                { "Counter9", typeof(int) },
            }
        };
    }

    public override void CloseConnection()
    {
        _postCommand.Dispose();
        base.CloseConnection();
    }

    [Benchmark(Baseline = true, Description = "SqlCommand")]
    public Post? SqlCommand()
    {
        Step();
        _idParam.Value = i;

        using DbDataReader reader = _postCommand.ExecuteReader(
            CommandBehavior.SingleResult | CommandBehavior.SingleRow);
        reader.Read();
        return new Post
        {
            Id = reader.GetInt32(0),
            Text = reader.GetNullableString(1),
            CreationDate = reader.GetDateTime(2),
            LastChangeDate = reader.GetDateTime(3),

            Counter1 = reader.GetNullableValue<int>(4),
            Counter2 = reader.GetNullableValue<int>(5),
            Counter3 = reader.GetNullableValue<int>(6),
            Counter4 = reader.GetNullableValue<int>(7),
            Counter5 = reader.GetNullableValue<int>(8),
            Counter6 = reader.GetNullableValue<int>(9),
            Counter7 = reader.GetNullableValue<int>(10),
            Counter8 = reader.GetNullableValue<int>(11),
            Counter9 = reader.GetNullableValue<int>(12)
        };
    }

    [Benchmark(Description = "DataTable")]
    public dynamic DataTableDynamic()
    {
        Step();
        _idParam.Value = i;
        _table.Rows.Clear();
        var values = new object[13];
        using DbDataReader reader = _postCommand.ExecuteReader(
            CommandBehavior.SingleResult | CommandBehavior.SingleRow);
        reader.Read();
        reader.GetValues(values);
        return _table.Rows.Add(values);
    }
}
