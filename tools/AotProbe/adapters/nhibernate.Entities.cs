namespace AotProbe.Adapters.NHibernateLib;

// NHibernate 官方 POCO 约定：非 sealed + virtual 成员（延迟加载代理的形态要求，
// InvalidProxyTypeException 实测 2026-10-28）。CategoryCount 是 LINQ DTO，不参与映射，保持 sealed。
public class OrderRow
{
    public virtual long Id { get; set; }
    public virtual string Name { get; set; } = "";
    public virtual decimal Amount { get; set; }
    public virtual string Category { get; set; } = "";
}

public class CatalogRow
{
    public virtual long Id { get; set; }
    public virtual string Label { get; set; } = "";
}

public sealed class CategoryCount
{
    public string Category { get; set; } = "";
    public long Count { get; set; }
}
