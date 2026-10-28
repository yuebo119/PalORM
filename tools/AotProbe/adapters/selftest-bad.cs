using PalORM.AotProbe;

namespace AotProbe.Adapters;

// 变异注入用例（验证验证者）：纯说谎适配器——不做任何 DB 工作，返回貌似合理的观测值。
// 若 Runner 把它判 pass，断言反转架构已失效（selftest case 1 必红）。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath) => probeId switch
    {
        nameof(ProbeId.P0InsertAutoPk) => new(ProbeId.P0InsertAutoPk.ToString(), ProbeStatus.Ok, null, null,
            [
                new ObsField("insert.id", "System.Int64", "1"),
                new ObsField("orm.readback.name", "System.String", "spoofed-value"),
                new ObsField("raw.stored.name", "System.String", "spoofed-value"),
                new ObsField("orm.readback.amount", "System.String", "0.00"),
                new ObsField("orm.readback.amount.type", "System.Type", "System.Decimal"),
                new ObsField("materialized.type", "AotProbe.Adapters.PalORM.OrderRow", "AotProbe.Adapters.PalORM.OrderRow"),
            ]),
        _ => throw new NotSupportedException($"probe {probeId} not implemented by bad adapter"),
    };
}
