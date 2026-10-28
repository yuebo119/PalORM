namespace PalORM.AotProbe;

/// <summary>探针契约与目录（S0 = P0 八项）。
/// <para>本文件同时被两侧链接编译：工具侧（Runner 断言）与生成变体侧（宿主分发、适配器返回），
/// 因此必须零外部依赖，且不得引用 Fixtures / Manifest / GradingRules（那些是 Runner 单侧资产）。
/// 两侧各自编译进自身程序集，故全部类型 internal 即可。</para></summary>
internal enum ProbeLayer { P0, P1, P2, P3 }

internal enum ProbeKind
{
    /// <summary>结果探针：适配器按库官方推荐写法实现，只约定可观测结果，不限定 API 路径。</summary>
    Outcome,
    /// <summary>机制探针：路径钉死（异步必须真异步 API、流式必须 IAsyncEnumerable、bulk 必须库自带批量路径）。</summary>
    Mechanism,
}

internal enum ProbeId
{
    P0Ddl,
    P0InsertExplicitPk,
    P0InsertAutoPk,
    P0GetByKey,
    P0UpdateDelete,
    P0QueryAll,
    P0Aggregate,
    P0Transaction,
}

internal sealed record ProbeDef(ProbeId Id, ProbeLayer Layer, ProbeKind Kind, string Name, string Risk, bool GroupBEligible);

/// <summary>适配器的观测值（断言反转裁决 2026-10-28：适配器只交观测，不自行判定成败）。</summary>
internal sealed record ObsField(string Key, string TypeName, string Value);

internal enum ProbeStatus { Ok, Error }

internal sealed record ProbeObservation(
    string ProbeId, ProbeStatus Status, string? ErrorType, string? ErrorMessage, IReadOnlyList<ObsField> Fields);

internal static class ProbeCatalog
{
    public const string CatalogVersion = "S0-P0.1";

    private static readonly ProbeDef[] all =
    [
        new(ProbeId.P0Ddl, ProbeLayer.P0, ProbeKind.Outcome, "建表/DDL", "类型元数据枚举", GroupBEligible: true),
        new(ProbeId.P0InsertExplicitPk, ProbeLayer.P0, ProbeKind.Outcome, "单行插入（显式主键）", "属性映射 + 参数绑定", GroupBEligible: false),
        new(ProbeId.P0InsertAutoPk, ProbeLayer.P0, ProbeKind.Outcome, "单行插入（自增主键+回读）", "值回写元数据", GroupBEligible: false),
        new(ProbeId.P0GetByKey, ProbeLayer.P0, ProbeKind.Outcome, "主键查询", "物化器（构造/属性赋值）", GroupBEligible: true),
        new(ProbeId.P0UpdateDelete, ProbeLayer.P0, ProbeKind.Outcome, "更新/删除", "变更检测与命令生成", GroupBEligible: false),
        new(ProbeId.P0QueryAll, ProbeLayer.P0, ProbeKind.Outcome, "全表查询（列表物化）", "批量物化", GroupBEligible: true),
        new(ProbeId.P0Aggregate, ProbeLayer.P0, ProbeKind.Outcome, "聚合查询（Count + 分组）", "聚合与投影物化", GroupBEligible: true),
        new(ProbeId.P0Transaction, ProbeLayer.P0, ProbeKind.Outcome, "事务（提交/回滚）", "连接状态机", GroupBEligible: false),
    ];

    internal static IReadOnlyList<ProbeDef> All => all;

    /// <summary>B 组归因臂（实体仅 typeof、从不 new）只跑物化相关探针：写入引导走原生 ADO，
    /// ORM 只承担读物化。这是"库能否在无静态构造调用点下物化"的最小充分实验面。</summary>
    internal static ProbeId[] GetGroupBSet() => all.Where(d => d.GroupBEligible).Select(d => d.Id).ToArray();

    internal static ProbeDef Def(ProbeId id) => all.Single(d => d.Id == id);

    /// <summary>item × lib 适用性（B28 参照系偏置防线）。S0 阶段 P0 八项对 palorm 全适用；
    /// N/A 条目在 S1 各库接入时逐条登记（先例：#19 存储过程在 SQLite 方言全库 N/A）。</summary>
    internal static bool IsApplicable(ProbeId probe, string libraryId) => true;
}
