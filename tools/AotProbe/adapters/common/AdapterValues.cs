using System.Globalization;
using PalORM.AotProbe;

namespace AotProbe.Adapters;

/// <summary>适配器公共底座（无外部依赖，链接进全部变体）：值常量 + 观测构造器。
/// 值常量镜像 Runner 侧 Fixtures——漂移只会假失败（断言不符即红），永远不会假通过。
/// palorm 适配器沿用自带副本（已验证绿），新适配器统一走本文件。</summary>
internal static class AdapterValues
{
    public const string Name1 = "订单\"A\"-🚀-日本語";
    public const string Name2 = "row-two-平安京";
    public const string Name3 = "row'three \"quoted\"";
    public const string AmountText = "12345.67";
    public const string AmountUpdatedText = "999.99";
    public const string Cat1 = "cat-1";
    public const string Cat2 = "cat-2";
    public const string CatalogLabelValue = "明示主键-42";
    public const long CatalogIdValue = 42;
    public const string TxKept = "tx-kept";
    public const string TxDropped = "tx-dropped";

    public static decimal Amount => decimal.Parse(AmountText, CultureInfo.InvariantCulture);
    public static decimal AmountUpdated => decimal.Parse(AmountUpdatedText, CultureInfo.InvariantCulture);

    public static ObsField F(string key, object value) =>
        new(key, value.GetType().FullName ?? "", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

    /// <summary>类型观测字段约定：TypeName 与 Value 都承载类型全名（Runner 的 TypeSuffix 断言查 TypeName）。</summary>
    public static ObsField FT(string key, Type? type) =>
        new(key, type?.FullName ?? "<null>", type?.FullName ?? "<null>");

    public static ProbeObservation Ok(ProbeId probe, params ObsField[] fields) =>
        new(probe.ToString(), ProbeStatus.Ok, null, null, fields);
}
