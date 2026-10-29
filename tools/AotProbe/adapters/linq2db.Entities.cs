using LinqToDB.Mapping;

namespace AotProbe.Adapters.Linq2Db;

[Table(Name = "ap_order")]
public sealed class OrderRow
{
    [PrimaryKey, Identity] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("amount")] public decimal Amount { get; set; }
    [Column("category")] public string Category { get; set; } = "";
}

[Table(Name = "ap_catalog")]
public sealed class CatalogRow
{
    [PrimaryKey] public long Id { get; set; }
    [Column("label")] public string Label { get; set; } = "";
}

public sealed class CategoryCount
{
    [Column("Category")] public string Category { get; set; } = "";
    [Column("Count")] public long Count { get; set; }
}
