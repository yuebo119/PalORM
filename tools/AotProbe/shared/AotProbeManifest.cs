namespace PalORM.AotProbe;

/// <summary>库清单（冻结评审四件套之一；增减库只改本文件 + 补 adapters/&lt;id&gt;.cs）。
/// <para>口径（用户裁决 2026-10-28）：官方 AOT 路径主测定 + SQLite 目标应用闭包 + NuGet 包消费
/// 视角（PalORM 用 nuget.org 发布包，PackageConsumer.Aot 先例）；Dapper 主包条目
/// 已移除（官方 AOT 路径 = 换用 Dapper.AOT，清单只承载官方路径形态）；归档/停更项目一律不测
/// （VerifiedMaintained 登记核查日期）。</para>
/// <para>版本策略（用户指令 2026-10-28）：默认钉接入日最新稳定版，预发布不入口径；库升级即触发重测。</para></summary>
internal static class AotProbeManifest
{
    public const string ManifestVersion = "2026-10-29.1";

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
        string OfficialAotPath, string VerifiedMaintained, string Note,
        string Provider = "sqlite",
        // 库内部对 AOT 告警的抑制痕迹数（UnconditionalSuppressMessage / #pragma warning disable 注入）。
        // 抑制≠消除：计入"未缓解告警"同权，封顶 🟦（用户裁决 2026-10-29）。
        int SuppressedWarnings = 0,
        // AOT 实现机制（报告列）：库用什么手段应对裁剪与 AOT，与实测结果交叉印证
        string Mechanism = "");

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
            Note: "本仓基准臂；已知答案 = ✅ + 归因【库级保障】",
            Mechanism: "编译期源生成：SQL 构造、参数绑定、DDL、物化器全部生成静态代码，运行时零反射"),
        new("dapper-aot", Tier.Headline,
        [
            new PkgRef("Dapper", "2.1.89"),
            new PkgRef("Dapper.AOT", "1.1.0"), // 运行时组件须随应用发布（PrivateAssets 会致 AOT 类型加载失败，实测 2026-10-28）
            new PkgRef("Microsoft.Data.Sqlite", "10.0.14"),
        ], AdapterReady: true,
            OfficialAotPath: "Dapper.AOT 即官方 AOT 路径包",
            VerifiedMaintained: "2026-10-28",
            Note: "Dapper 主包条目已按裁决移除；零告警含抑制成分（Dapper.dll 1 处 UnconditionalSuppressMessage + 生成器 #pragma warning disable 注入，用户裁决 2026-10-29 按屏蔽计封顶 🟦）",
            Mechanism: "编译期源生成 + 拦截器：生成器接管全部 Dapper 调用（程序集级 [DapperAot]），SQL 与参数绑定为静态代码",
            SuppressedWarnings: 1),
        new("repodb", Tier.Headline,
        [
            new PkgRef("RepoDb.Sqlite.Microsoft", "1.17.0"),
        ], AdapterReady: true,
            OfficialAotPath: "默认形态（源内注解申报，无使用方 AOT 配置文档）",
            VerifiedMaintained: "2026-10-28",
            Note: "档位争议库（🟦 边界），B 组归因重点",
            Mechanism: "源内注解申报 + 表达式解释执行：公开 API 标 RequiresUnreferencedCode 等，表达式在 AOT 下解释"),
        new("efcore", Tier.Headline,
        [
            new PkgRef("Microsoft.EntityFrameworkCore.Sqlite", "10.0.14"),
            new PkgRef("Microsoft.EntityFrameworkCore.Design", "10.0.14", PrivateAssets: true),
        ], AdapterReady: true,
            OfficialAotPath: "Compiled Model + 预编译查询（官方实验性 AOT 路线）",
            VerifiedMaintained: "2026-10-28",
            Note: "钉版已复核（NuGet 最新稳定 10.0.14）；Compiled Model 两阶段编排本批攻坚，就绪前不翻牌",
            Mechanism: "编译期预生成模型：Compiled Model + 预编译查询（官方实验性），模型服务仍无预生成"),
        new("freesql", Tier.Headline,
        [
            new PkgRef("FreeSql.Provider.Sqlite", "3.5.311"),
            new PkgRef("Microsoft.Data.Sqlite", "10.0.14"),
        ], AdapterReady: true,
            OfficialAotPath: "默认形态（无官方 AOT 路径；2026-10-28 实测 2/9、Root 后 9/9）",
            VerifiedMaintained: "2026-10-28",
            Note: "对照库",
            Mechanism: "运行时反射：实体元数据与物化器运行时生成，无 AOT 适配（Root 保根后 8/8）"),
        new("linq2db", Tier.Headline,
        [
            new PkgRef("linq2db", "6.5.1"),
            new PkgRef("linq2db.SQLite", "6.5.1"),
            new PkgRef("Microsoft.Data.Sqlite", "10.0.14"),
        ], AdapterReady: true,
            OfficialAotPath: "默认形态（无官方 AOT 路径；Assembly.Load 加载 provider 已实证失败）",
            VerifiedMaintained: "2026-10-28",
            Note: "provider 包名 S1 冻结时核实",
            Mechanism: "表达式翻译 + 运行时动态加载：provider 程序集经 Assembly.Load(名字) 加载"),
        new("sqlsugar", Tier.Headline,
        [
            new PkgRef("SqlSugarCore", "5.1.4.221"),
            new PkgRef("Microsoft.Data.Sqlite", "10.0.14"),
        ], AdapterReady: true,
            OfficialAotPath: "默认形态（StaticConfig.EnableAot 已实证无效）",
            VerifiedMaintained: "2026-10-28",
            Note: "对照库",
            Mechanism: "运行时反射 + 捆绑全套驱动：依赖闭包含 Npgsql 等，ILC 编译期即死"),
        new("litedb", Tier.Watch,
        [
            new PkgRef("LiteDB", "5.0.21"),
        ], AdapterReady: true,
            OfficialAotPath: "默认形态",
            VerifiedMaintained: "2026-10-28",
            Note: "内嵌文档库（无 provider 维），报告口径需标注；raw ADO 旁路不适用",
            Mechanism: "运行时反射：BsonMapper 经 Activator.CreateInstance 构造实体被裁剪（Root 保根后 6/6）"),
        new("nhibernate", Tier.Watch,
        [
            new PkgRef("NHibernate", "5.6.0"),
            new PkgRef("System.Data.SQLite.Core", "1.0.119"), // NHibernate 官方 SQLite 驱动载体
            new PkgRef("Microsoft.Data.Sqlite", "10.0.14"),   // raw 旁路通道（RawChannel）用
        ], AdapterReady: true,
            OfficialAotPath: "默认形态（重反射，预判 🟥）",
            VerifiedMaintained: "2026-10-28",
            Note: "维护核查：5.6.0@2025-10 + 5.7.x 开发中；SQLite 官方配对 = SQLite20Driver + System.Data.SQLite（5.6 无 Microsoft.Data.Sqlite 驱动，程序集扫描定案）；raw 旁路仍用 Microsoft.Data.Sqlite",
            Mechanism: "运行时反射 + 动态代理：映射、物化、代理类生成全在运行时"),
        new("marten", Tier.Headline,
        [
            new PkgRef("Marten", "9.14.1"),
        ], AdapterReady: true,
            OfficialAotPath: "源生成路线（v7 起编译期生成，v9 全面源生成器，changelog 列 AoT support）",
            VerifiedMaintained: "2026-10-28",
            Note: "PG-only，经 opt-in PG 通道接入（用户指令 2026-10-28）；NuGet 累计下载 2000 万+；①类独立大库样本",
            Mechanism: "编译期源生成（v9 源生成器）：文档映射代码编译期生成，但 DocumentMappingBuilder 依赖无参构造被裁后失败",
            Provider: "postgres"),
        new("smartsql", Tier.Watch,
        [
            new PkgRef("SmartSql", "4.2.0"),
            new PkgRef("Microsoft.Data.Sqlite", "10.0.14"),
        ], AdapterReady: true,
            OfficialAotPath: "默认形态（XML-SQL + 动态 Repository 代理，③类预期 🟥）",
            VerifiedMaintained: "2026-10-28",
            Note: "维护核查：wiki 2026-05 更新、NuGet 4.2.0；国产 MyBatis 型",
            Mechanism: "XML-SQL + 动态 Repository 代理：语句在 XML 内，仓储接口经运行时代理实现"),
        new("insight-database", Tier.Watch,
        [
            new PkgRef("Insight.Database", "8.0.6"),
            new PkgRef("Microsoft.Data.Sqlite", "10.0.14"),
        ], AdapterReady: true,
            OfficialAotPath: "默认形态（反射 + 运行时代理，③类预期 🟥）",
            VerifiedMaintained: "2026-10-28",
            Note: "维护核查：8.0.6@2026-02 仍活跃",
            Mechanism: "运行时反射 + 动态代理：结果集映射与接口代理运行时代码生成"),
    ];

    internal static IReadOnlyList<Library> Libraries => libraries;
}
