namespace AotProbe.Adapters.MartenLib;

// Marten 文档实体：CatalogRow 用 string 主键（Marten 默认客户端分配策略）承接显式主键 "42"；
// OrderRow 用 long 主键（Marten HiLo 序列自动分配 = 自增语义）。无表列映射：文档序列化为 JSONB。
public sealed class OrderRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public string Category { get; set; } = "";
}

public sealed class CatalogRow
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class CategoryCount
{
    public string Category { get; set; } = "";
    public long Count { get; set; }
}
