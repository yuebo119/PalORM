using LiteDB;

namespace AotProbe.Adapters.LiteDb;

// LiteDB 无 SQL 面：列名即属性名；[BsonId] long 交由 LiteDB 自增（BsonMapper 约定）。
// 适用性：P0Ddl / P0Aggregate 为 N/A（文档库无 DDL、无数据库端分组聚合，登记于 ProbeCatalog）。
public sealed class OrderRow
{
    [BsonId] public long Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public string Category { get; set; } = "";
}

public sealed class CatalogRow
{
    [BsonId] public long Id { get; set; }
    public string Label { get; set; } = "";
}
