namespace AotProbe.Adapters.SmartSql;

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
