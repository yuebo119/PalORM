using PalORM;

namespace PalORM.DapperSuite;

/// <summary>Dapper 官方基准数据集（DapperLib/Dapper benchmarks/Dapper.Tests.Performance/Post.cs）。
/// 成员与官方逐位一致：Id(int) + Text(string) + 两个 DateTime + Counter1..9(int?)，共 13 列。
/// <para>偏差登记：PalORM 需要属性标注（[Table]/[Key]/[Column]），Dapper 与手写 ADO.NET
/// 按列名/序号工作不受标注影响——同一实体服务三臂。</para></summary>
[Table("Posts")]
public sealed partial class Post
{
    [Key(AutoIncrement = false)]
    [Column("Id")]
    public int Id { get; set; }

    [Column("Text")]
    public string? Text { get; set; }

    [Column("CreationDate")]
    public DateTime CreationDate { get; set; }

    [Column("LastChangeDate")]
    public DateTime LastChangeDate { get; set; }

    [Column("Counter1")] public int? Counter1 { get; set; }
    [Column("Counter2")] public int? Counter2 { get; set; }
    [Column("Counter3")] public int? Counter3 { get; set; }
    [Column("Counter4")] public int? Counter4 { get; set; }
    [Column("Counter5")] public int? Counter5 { get; set; }
    [Column("Counter6")] public int? Counter6 { get; set; }
    [Column("Counter7")] public int? Counter7 { get; set; }
    [Column("Counter8")] public int? Counter8 { get; set; }
    [Column("Counter9")] public int? Counter9 { get; set; }

    /// <summary>确定性种子（官方未定义种子内容，本套件按行号派生，三库逐位相同）。</summary>
    public static Post Seed(int i) => new()
    {
        Id = i,
        Text = i % 7 == 0 ? null : $"post {i}",
        CreationDate = DateTime.UnixEpoch.AddMinutes(i),
        LastChangeDate = DateTime.UnixEpoch.AddMinutes(i * 2),
        Counter1 = i % 2 == 0 ? null : i,
        Counter2 = i % 3 == 0 ? null : i + 2,
        Counter3 = i % 4 == 0 ? null : i + 3,
        Counter4 = i % 5 == 0 ? null : i + 4,
        Counter5 = i % 6 == 0 ? null : i + 5,
        Counter6 = i % 7 == 0 ? null : i + 6,
        Counter7 = i % 8 == 0 ? null : i + 7,
        Counter8 = i % 9 == 0 ? null : i + 8,
        Counter9 = i % 10 == 0 ? null : i + 9
    };
}

/// <summary>官方 HandCoded 基线用的可空读列扩展（官方 SqlDataReaderHelper.cs 的跨驱动移植）。</summary>
internal static class ReaderExtensions
{
    public static string? GetNullableString(this System.Data.Common.DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static T? GetNullableValue<T>(this System.Data.Common.DbDataReader reader, int ordinal)
        where T : struct
        => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
}
