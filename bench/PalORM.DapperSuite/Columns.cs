using System.ComponentModel;
using System.Reflection;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace PalORM.DapperSuite;

/// <summary>官方 <c>Helpers/ORMColum.cs</c> 的移植（类名去掉拼写笔误的第二个 m）。
/// 从工作负载方法所在类型上取 <see cref="DescriptionAttribute"/>，没有则用类型名去掉
/// "Benchmarks" 后缀——因此 <c>HandCodedBenchmarks</c> → HandCoded，其余两臂读到类上的
/// <c>[Description("Dapper")]</c> / <c>[Description("PalORM")]</c>。
/// <para>官方口径一致：PriorityInCategory = -10，排在方法名列之前。</para></summary>
public sealed class OrmColumn : IColumn
{
    public string Id { get; } = nameof(OrmColumn);

    public string ColumnName { get; } = "ORM";

    public string Legend { get; } = "The object/relational mapper being tested";

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        Type type = benchmarkCase.Descriptor.WorkloadMethod.DeclaringType!;
        return type.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? type.Name.Replace("Benchmarks", string.Empty, StringComparison.Ordinal);
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        => GetValue(summary, benchmarkCase);

    public bool IsAvailable(Summary summary) => true;

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Job;

    public int PriorityInCategory => -10;

    public bool IsNumeric => false;

    public UnitType UnitType => UnitType.Dimensionless;

    public override string ToString() => ColumnName;
}
