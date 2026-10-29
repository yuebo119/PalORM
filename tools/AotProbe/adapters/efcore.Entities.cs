namespace AotProbe.Adapters.EfCoreLib;

// 纯 POCO；映射全在 DbContext OnModelCreating（fluent）。EF SQLite decimal 走 TEXT 存储保精度。
public sealed class OrderRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public string Category { get; set; } = "";
}

public sealed class CatalogRow
{
    public long Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class CategoryCount
{
    public string Category { get; set; } = "";
    public long Count { get; set; }
}
