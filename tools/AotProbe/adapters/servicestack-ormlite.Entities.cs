using ServiceStack.DataAnnotations;

namespace AotProbe.Adapters.ServiceStackOrmLite;

// 列名走属性名默认映射（Name/Amount/Category/Label，SQLite 标识符大小写不敏感，断言 SQL 依旧可读小写形态）。
[Alias("ap_order")]
public sealed class OrderRow
{
    [PrimaryKey, AutoIncrement] public long Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public string Category { get; set; } = "";
}

[Alias("ap_catalog")]
public sealed class CatalogRow
{
    [PrimaryKey] public long Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class CategoryCount
{
    public string Category { get; set; } = "";
    public long Count { get; set; }
}
