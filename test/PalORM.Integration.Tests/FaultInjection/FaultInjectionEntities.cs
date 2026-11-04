namespace PalORM.Integration.Tests.FaultInjection;

/// <summary>故障注入用例专用表：与契约/批量用例的 <c>cf_bulk_plain</c> 分开，
/// 避免真库通道里并行用例互相看到对方的行（2026-11-04 实测：共用表导致
/// BulkAtomicity 的 PG 用例与本组用例互相污染，两条同时红）。</summary>
[Table("palorm_fault_row")]
public partial class FaultInjectionRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }

    [Column("name")] public string Name { get; set; } = "";

    [Column("amount")] public long Amount { get; set; }
}
