namespace AotProbe.Adapters.DapperAot;

// Dapper 按名映射（大小写不敏感），SELECT 统一带别名保证生成器映射确定性。
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
