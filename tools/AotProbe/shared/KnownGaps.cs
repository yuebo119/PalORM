namespace PalORM.AotProbe;

/// <summary>已知缺口登记册（只注记、不改档——裁决 2026-10-28，测量与沟通分离）。
/// 报告据此把失败分为"已知缺口"与"新发现"；登记条目全部来自本矩阵或先行审计的实证，
/// 升级重测时若缺口消失须同步删除条目。</summary>
internal static class KnownGaps
{
    internal sealed record Gap(string Lib, string Probe, string Summary);

    internal static readonly Gap[] All =
    [
        new("sqlite-net", "P0InsertAutoPk",
            "库固有：SQLite NUMERIC 亲和力下 decimal 走 REAL 浮点存储，读回精度损坏（JIT 同败，非 AOT 特有）"),
        new("sqlite-net", "P0UpdateDelete", "同 P0InsertAutoPk：decimal 精度损坏（库固有）"),
        new("sqlsugar", "P0InsertAutoPk", "库固有：decimal 经 double 存储精度损坏（JIT 同败，非 AOT 特有）"),
        new("sqlsugar", "P0UpdateDelete", "同 P0InsertAutoPk：decimal 精度损坏（库固有）"),
        new("servicestack-ormlite", "P0InsertAutoPk", "库固有：decimal 经 REAL 存储精度损坏（JIT 同败，非 AOT 特有）"),
        new("servicestack-ormlite", "P0UpdateDelete", "同 P0InsertAutoPk：decimal 精度损坏（库固有）"),
        new("efcore", "*",
            "官方实验性路径现状：Compiled Model 生成并链接成功，但 EF 10.0.14 生成代码未预生成 ShadowValuesFactory，"
            + "运行期回退被 NativeAOT 阻断（模型服务覆盖缺口，非编排问题）"),
        new("dapper-aot", "*",
            "元组查询 Query&lt;(string,long)&gt; 回退不安全路径（DAP012，2026-10-28 手工审计实证；"
            + "本矩阵聚合探针走名映射 DTO 形态避开，故不体现为失败）"),
        new("repodb", "*",
            "属性名与列名必须精确同形（Name≠name 即『Must add values for parameters』）；"
            + "显式主键表须 BIGINT 而非 INTEGER PRIMARY KEY（rowid 别名会被判 identity 丢显式值）——"
            + "两条均最小复现定案 2026-10-28"),
    ];

    internal static string? Match(string lib, string probe) =>
        All.FirstOrDefault(g => g.Lib == lib && (g.Probe == "*" || g.Probe == probe))?.Summary;
}
