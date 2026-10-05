// PalORM.PerfCli 统一入口（脚本 C# 化整改方案 Phase 4：perf.sh 家族五件套 + set-test-env 并入）。
// 调用形态：dotnet run --project tools/PalORM.PerfCli -- <子命令> [参数]
namespace PalORM.PerfCli;

internal static class Program
{
    public static int Main(string[] args)
    {
        Perf.UseUtf8Output();
        // NuGetAudit=false：BDN 自动生成工程 restore 时拉取漏洞数据失败会被根级
        // TreatWarningsAsErrors 提升为 error（NU1900），整个 BDN 图（含被引用的 src 工程）被阻断。
        // 本机 NuGet 走 WinINET 系统代理拿不到 nuget.org 服务索引，env 变量在 Windows 上对 NuGet
        // 无效的问题用 MSBuild 属性通道解决：环境变量自动成为 MSBuild 属性、先于 SDK 默认值生效，
        // 且随 BDN 派生的 msbuild 子进程继承。仅审计静默，包解析不受影响（restore 实测通过）。
        Environment.SetEnvironmentVariable("NuGetAudit", "false");

        var cmd = args.Length > 0 ? args[0] : "";
        var rest = args.Skip(1).ToArray();
        if (cmd is "-h" or "--help" or "help")
            return PerfEntry.Usage();
        return cmd switch
        {
            "smoke" => PerfEntry.Smoke(),
            "full" => PerfEntry.Full(rest),
            "compare" => PerfHubAb.Run(rest),
            "gate" => PerfEntry.Gate(),
            "report" => PerfEntry.Report(),
            "index" => PerfEntry.Index(),
            // 内部步骤子命令：保留独立触发能力（对齐原步骤脚本"单独跑用于调试"语义）
            "bench-matrix" => BenchMatrix.Run(rest),
            "full-perf" => FullPerf.Run(
                skipReport: Environment.GetEnvironmentVariable("SKIP_REPORT") == "1",
                withRemote: Environment.GetEnvironmentVariable("WITH_REMOTE") == "1"),
            "dappersuite" => DapperSuite.Run(rest),
            _ => PerfEntry.Usage(),
        };
    }
}
