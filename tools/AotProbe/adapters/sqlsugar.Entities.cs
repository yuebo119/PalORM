using SqlSugar;

namespace AotProbe.Adapters.SqlSugar;

[SugarTable("ap_order")]
public sealed class OrderRow
{
    // SQLite 硬约束：AUTOINCREMENT 只允许出现在 "INTEGER PRIMARY KEY" 上——
    // SqlSugar 对 long 自增默认生成 BIGINT 变体导致建表失败，显式钉列类型
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")] public long Id { get; set; }
    [SugarColumn(ColumnName = "name")] public string Name { get; set; } = "";
    [SugarColumn(ColumnName = "amount")] public decimal Amount { get; set; }
    [SugarColumn(ColumnName = "category")] public string Category { get; set; } = "";
}

[SugarTable("ap_catalog")]
public sealed class CatalogRow
{
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(ColumnName = "label")] public string Label { get; set; } = "";
}

public sealed class CategoryCount
{
    public string Category { get; set; } = "";
    public long Count { get; set; }
}
