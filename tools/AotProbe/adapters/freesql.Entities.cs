using FreeSql.DataAnnotations;

namespace AotProbe.Adapters.FreeSql;

[Table(Name = "ap_order")]
public sealed class OrderRow
{
    [Column(IsPrimary = true, IsIdentity = true)] public long Id { get; set; }
    [Column(Name = "name")] public string Name { get; set; } = "";
    [Column(Name = "amount")] public decimal Amount { get; set; }
    [Column(Name = "category")] public string Category { get; set; } = "";
}

[Table(Name = "ap_catalog")]
public sealed class CatalogRow
{
    [Column(IsPrimary = true)] public long Id { get; set; }
    [Column(Name = "label")] public string Label { get; set; } = "";
}

public sealed class CategoryCount
{
    public string Category { get; set; } = "";
    public long Count { get; set; }
}
