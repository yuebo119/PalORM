namespace PalORM.AotProbe;

/// <summary>库清单（冻结评审四件套之一；增减库只改本文件 + 补 adapters/&lt;id&gt;.cs）。
/// <para>口径（用户裁决 2026-10-28）：官方 AOT 路径主测定 + SQLite 目标应用闭包 + NuGet 包消费
/// 视角（十库统一，PalORM 用 nuget.org 发布包，PackageConsumer.Aot 先例）；Dapper 主包条目
/// 已移除（官方 AOT 路径 = 换用 Dapper.AOT，清单只承载官方路径形态）；归档/停更项目一律不测
/// （VerifiedMaintained 登记核查日期）。</para></summary>
internal static class AotProbeManifest
{
    public const string ManifestVersion = "2026-10-28.1";

    internal enum Tier
    {
        /// <summary>README 对比口径库 + 档位争议库：S1 起全矩阵。</summary>
        Headline,
        /// <summary>只跑 S1+S2（等级 + 归因），不进 S3 功能深度。</summary>
        Watch,
    }

    internal sealed record PkgRef(string Name, string Version, bool PrivateAssets = false);

    internal sealed record Library(
        string Id, Tier LibTier, IReadOnlyList<PkgRef> Packages, bool AdapterReady,
        string OfficialAotPath, string VerifiedMaintained, string Note);

    private static readonly Library[] libraries =
    [
        new("palorm", Tier.Headline,
        [
            new PkgRef("PalORM.Core", "6.8.0"),
            new PkgRef("PalORM.Sqlite", "6.8.0"),
            new PkgRef("PalORM.SourceGen", "6.8.0", PrivateAssets: true),
        ], AdapterReady: true,
            OfficialAotPath: "AOT-first 设计（IsAotCompatible 默认开）",
            VerifiedMaintained: "2026-10-28",
            Note: "本仓基准臂；已知答案 = ✅ + 归因【库级保障】"),
        new("dapper-aot", Tier.Headline,
        [
            new PkgRef("Dapper", "2.1.89"),
            new PkgRef("Dapper.AOT", "1.1.0", PrivateAssets: true),
            new PkgRef("Microsoft.Data.Sqlite", "10.0.3"),
        ], AdapterReady: false,
            OfficialAotPath: "Dapper.AOT 即官方 AOT 路径包（版本沿用 CPM 钉版，S1 冻结复核）",
            VerifiedMaintained: "2026-10-28",
            Note: "Dapper 主包条目已按裁决移除"),
        new("repodb", Tier.Headline,
        [
            new PkgRef("RepoDb.Sqlite.Microsoft", "1.17.0"),
        ], AdapterReady: false,
            OfficialAotPath: "默认形态（源内注解申报，无使用方 AOT 配置文档）",
            VerifiedMaintained: "2026-10-28",
            Note: "档位争议库（🟦 边界），B 组归因重点"),
        new("efcore", Tier.Headline,
        [
            new PkgRef("Microsoft.EntityFrameworkCore.Sqlite", "10.0.2"),
        ], AdapterReady: false,
            OfficialAotPath: "Compiled Model + 预编译查询（官方实验性 AOT 路线）",
            VerifiedMaintained: "2026-10-28",
            Note: "版本号 S1 冻结时复核钉死；适配器预算 1 天（Compiled Model 生成步骤）"),
        new("freesql", Tier.Headline,
        [
            new PkgRef("FreeSql.Provider.Sqlite", "3.5.311"),
        ], AdapterReady: false,
            OfficialAotPath: "默认形态（无官方 AOT 路径；2026-10-28 实测 2/9、Root 后 9/9）",
            VerifiedMaintained: "2026-10-28",
            Note: "对照库"),
        new("linq2db", Tier.Headline,
        [
            new PkgRef("linq2db", "6.5.1"),
            new PkgRef("linq2db.SQLite", "6.5.1"),
        ], AdapterReady: false,
            OfficialAotPath: "默认形态（无官方 AOT 路径；Assembly.Load 加载 provider 已实证失败）",
            VerifiedMaintained: "2026-10-28",
            Note: "provider 包名 S1 冻结时核实"),
        new("sqlsugar", Tier.Headline,
        [
            new PkgRef("SqlSugarCore", "5.1.4.221"),
        ], AdapterReady: false,
            OfficialAotPath: "默认形态（StaticConfig.EnableAot 已实证无效）",
            VerifiedMaintained: "2026-10-28",
            Note: "对照库"),
        new("sqlite-net", Tier.Watch,
        [
            new PkgRef("sqlite-net-pcl", "1.11.285"),
        ], AdapterReady: false,
            OfficialAotPath: "默认形态（无任何 AOT 工作）",
            VerifiedMaintained: "2026-10-28",
            Note: "『碰巧保留』机制活样本：S2 归因重点"),
        new("litedb", Tier.Watch,
        [
            new PkgRef("LiteDB", "5.0.21"),
        ], AdapterReady: false,
            OfficialAotPath: "默认形态",
            VerifiedMaintained: "2026-10-28",
            Note: "内嵌文档库（无 provider 维），报告口径需标注；raw ADO 旁路不适用"),
        new("nhibernate", Tier.Watch,
        [
            new PkgRef("NHibernate", "5.6.0"),
        ], AdapterReady: false,
            OfficialAotPath: "默认形态（重反射，预判 🟥）",
            VerifiedMaintained: "2026-10-28",
            Note: "维护核查：5.6.0@2025-10 + 5.7.x 开发中；SQLite 驱动包 S1 冻结时核实"),
    ];

    internal static IReadOnlyList<Library> Libraries => libraries;
}
