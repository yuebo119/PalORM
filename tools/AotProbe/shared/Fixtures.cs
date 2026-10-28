namespace PalORM.AotProbe;

internal enum MatchKind
{
    /// <summary>观测值字符串（InvariantCulture 形态）与期望逐字相等。</summary>
    Exact,
    /// <summary>观测值为正整数（自增主键回读等动态值）。</summary>
    PositiveLong,
    /// <summary>观测值类型名与期望全名相等，或以 ".期望" 结尾（实体类型按名后缀断言，
    /// 因实体全名随变体程序集命名空间变化）。</summary>
    TypeSuffix,
}

internal sealed record ExpectedField(string Key, MatchKind Kind, string Expected);

/// <summary>期望值夹具：断言的唯一来源（断言反转裁决 2026-10-28）。
/// <para>Runner 侧单侧资产——严禁链接进生成变体，否则适配器可反读期望自证（实验者效应）。
/// Name 期望值刻意含 CJK/引号/emoji：编码路径随全部探针免费覆盖（原 #22 独立探针已并夹具）。</para>
/// <para>数值期望一律 InvariantCulture 字符串形态（decimal 12345.67m → "12345.67"）。</para></summary>
internal static class Fixtures
{
    public const string FixtureVersion = "S0.1";

    public const string OrderName = "订单\"A\"-🚀-日本語";
    public const string OrderName2 = "row-two-平安京";
    public const string OrderName3 = "row'three \"quoted\"";
    public const string Amount = "12345.67";
    public const string AmountUpdated = "999.99";
    public const string Category1 = "cat-1";
    public const string Category2 = "cat-2";
    public const string CatalogLabel = "明示主键-42";
    public const string CatalogId = "42";
    public const string FirstAutoId = "1"; // 每探针独立新库文件，自增首行恒为 1

    internal static readonly Dictionary<ProbeId, ExpectedField[]> PerProbe = new()
    {
        [ProbeId.P0Ddl] =
        [
            new("ddl.table.count", MatchKind.Exact, "2"),
        ],
        [ProbeId.P0InsertExplicitPk] =
        [
            new("insert.id", MatchKind.Exact, CatalogId),
            new("orm.readback.label", MatchKind.Exact, CatalogLabel),
            new("raw.stored.label", MatchKind.Exact, CatalogLabel),
            new("materialized.type", MatchKind.TypeSuffix, "CatalogRow"),
        ],
        [ProbeId.P0InsertAutoPk] =
        [
            new("insert.id", MatchKind.PositiveLong, ""),
            new("orm.readback.name", MatchKind.Exact, OrderName),
            new("raw.stored.name", MatchKind.Exact, OrderName),
            new("orm.readback.amount", MatchKind.Exact, Amount),
            new("orm.readback.amount.type", MatchKind.Exact, "System.Decimal"),
            new("materialized.type", MatchKind.TypeSuffix, "OrderRow"),
        ],
        [ProbeId.P0GetByKey] =
        [
            new("get.id", MatchKind.Exact, FirstAutoId),
            new("get.name", MatchKind.Exact, OrderName),
            new("get.category", MatchKind.Exact, Category1),
            new("materialized.type", MatchKind.TypeSuffix, "OrderRow"),
        ],
        [ProbeId.P0UpdateDelete] =
        [
            new("update.affected", MatchKind.Exact, "1"),
            new("orm.afterupdate.amount", MatchKind.Exact, AmountUpdated),
            new("delete.affected", MatchKind.Exact, "1"),
            new("orm.afterdelete.found", MatchKind.Exact, "False"),
        ],
        [ProbeId.P0QueryAll] =
        [
            new("list.count", MatchKind.Exact, "3"),
            new("item.type", MatchKind.TypeSuffix, "OrderRow"),
            new("names.byid", MatchKind.Exact, $"{OrderName}|{OrderName2}|{OrderName3}"),
        ],
        [ProbeId.P0Aggregate] =
        [
            new("count.total", MatchKind.Exact, "3"),
            new($"group.{Category1}", MatchKind.Exact, "2"),
            new($"group.{Category2}", MatchKind.Exact, "1"),
            new("group.row.type", MatchKind.TypeSuffix, "CategoryCount"),
        ],
        [ProbeId.P0Transaction] =
        [
            new("commit.visible", MatchKind.Exact, "True"),
            new("rollback.visible", MatchKind.Exact, "False"),
        ],
    };
}
