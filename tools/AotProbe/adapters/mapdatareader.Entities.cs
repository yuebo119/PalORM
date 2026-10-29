using MapDataReader;

namespace AotProbe.Adapters.MapDataReaderLib;

// 纯映射器：无 DDL/事务/聚合，探针面 = 读取物化路径（GetByKey + QueryAll）
[GenerateDataReaderMapper]
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
