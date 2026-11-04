namespace PalORM.Integration.Tests.Conformance;

/// <summary>FUZ-1 宽表：把随机差分的列类型从 2 种（long、string）扩到 6 种
/// （long、string、int、short、double、bool）。<c>cf_bulk_plain</c> 保持三列不动，
/// 宽表只服务类型面：时间类型刻意不加入——SQLite 的 TEXT 往返在两侧的 CLR 形态不同
/// （PalORM 回 DateTime，裸 ADO 回 string），会把「类型往返」问题混进差分结论，
/// 这类问题由契约层的 TimeKindMatrix 专门负责。</summary>
[Table("cf_fuzz_wide")]
public partial class CfFuzzWideEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }

    [Column("name")] public string Name { get; set; } = "";

    [Column("amount")] public long Amount { get ; set; }

    [Column("qty")] public int Qty { get; set; }

    [Column("score")] public short Score { get; set; }

    [Column("ratio")] public double Ratio { get; set; }

    [Column("flag")] public bool Flag { get; set; }
}
