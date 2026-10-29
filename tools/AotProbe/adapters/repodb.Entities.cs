namespace AotProbe.Adapters.RepoDb;

// RepoDb 按约定映射：Id 属性即主键；表名映射走适配器内 FluentMapper（代码映射）（默认按类名）。适用性：P0Ddl 为 N/A
// （RepoDb 无 DDL API，登记于 ProbeCatalog；bootstrap 走 RawChannel）。
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
