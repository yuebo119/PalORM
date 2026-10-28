using System.Text.Json;
using System.Text.Json.Serialization;
using PalORM.AotProbe;

namespace AotProbe.Variant;

// 变体宿主（静态模板，全部库共用）：单探针单进程（原生崩溃只损失当项，可续跑），
// 失败以非零退出码 + Error 观测表达。适配器约定：LibAdapter.Run 只返回观测值，
// 绝不自行判定成败（断言反转裁决 2026-10-28——期望值只在 Runner 侧）。
// 注：Program 必须 partial——嵌套的 JsonSerializerContext 源生成器要求包含类型链可增强（SYSLIB1032）。
internal static partial class Program
{
    private const string MarkerPrefix = "AOTPROBE-OBS ";

    // AOT 形态下 JsonSerializerIsReflectionEnabledByDefault=false，序列化必须走源生成上下文
    [JsonSerializable(typeof(ProbeObservation))]
    private sealed partial class VariantJsonContext : JsonSerializerContext;

    private static int Main(string[] args)
    {
        string? probe = null, db = null;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--probe": probe = args[i + 1]; break;
                case "--db": db = args[i + 1]; break;
            }
        }

        if (probe is null || db is null)
        {
            Console.Error.WriteLine("usage: --probe <id> --db <path>");
            return 4;
        }

        ProbeObservation observation;
        try
        {
            observation = AotProbe.Adapters.LibAdapter.Run(probe, db);
        }
        catch (Exception ex)
        {
            observation = new ProbeObservation(probe, ProbeStatus.Error, ex.GetType().FullName, ex.Message, []);
        }

        Console.WriteLine(MarkerPrefix + JsonSerializer.Serialize(observation, VariantJsonContext.Default.ProbeObservation));
        return observation.Status == ProbeStatus.Ok ? 0 : 3;
    }
}
