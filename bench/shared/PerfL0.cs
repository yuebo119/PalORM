// L0 快线的覆盖定义（单一事实源）——PerfCli 决定跑什么，PerfGate 决定判什么，
// 两侧共用这一份清单，避免"跑的项"与"判的项"漂移（同 PerfResultEnvelope 的共享方式）。
namespace PalORM.Bench.Shared;

/// <summary>L0 快线：门禁关键项的 5 分钟反馈通道（作战卡 §6.3）。
/// <para><b>覆盖口径</b>：批量族四形态 + GetAllAsync + GetByKey 延迟哨兵，只跑 20000 档
/// （批量与物化在 20000 档的比值劣化更敏感），三方言。行数不进测量的项本就不在 20000 档重复，
/// 故清单里只列真正按档位测量的操作。</para>
/// <para><b>为什么不含 BDN</b>：BDN 门禁集单独就要约 5 分钟，放进 L0 无法满足 5 分钟目标；
/// 分配与微基准回归仍由全量轮的 BDN 门禁覆盖。L0 自己判 PalORM 的分配与比值两个面。</para></summary>
internal static class PerfL0
{
    public const string Label = "verify-l0";
    public const int Tier = 20000;

    /// <summary>覆盖的操作（顺序即命令行 --ops 的顺序，便于日志比对）。</summary>
    public static readonly string[] Operations =
    [
        "BulkInsert",
        "BulkUpdate",
        "BulkDelete",
        "UpsertBatch",
        "GetAllAsync",
        "GetByKey"
    ];

    public static bool Contains(string operation)
        => Array.IndexOf(Operations, operation) >= 0;

    /// <summary>传给 PerfHub <c>--ops</c> 的 CSV 形态。</summary>
    public static string OpsArgument => string.Join(',', Operations);
}
