using PalORM;

namespace AotProbe.Adapters.PalORM;

// 实体定义（A 组编入宿主程序集；B 组编入 BEntities 独立程序集，public 以便跨程序集 typeof 引用，
// AotModels.First 先例）。值常量在 LibAdapter（adapters/palorm.cs）——两文件都不引用 Fixtures。
[Table("ap_order")]
public sealed partial class OrderRow
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("amount")] public decimal Amount { get; set; }
    [Column("category")] public string Category { get; set; } = "";
}

[Table("ap_catalog")]
public sealed partial class CatalogRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("label")] public string Label { get; set; } = "";
}
