using SQLite;

namespace AotProbe.Adapters.SqliteNet;

[Table("ap_order")]
public sealed class OrderRow
{
    [PrimaryKey, AutoIncrement] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("amount")] public decimal Amount { get; set; }
    [Column("category")] public string Category { get; set; } = "";
}

[Table("ap_catalog")]
public sealed class CatalogRow
{
    [PrimaryKey] public long Id { get; set; }
    [Column("label")] public string Label { get; set; } = "";
}

public sealed class CategoryCount
{
    public string Category { get; set; } = "";
    public long Count { get; set; }
}
