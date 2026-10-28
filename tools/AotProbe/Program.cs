namespace PalORM.AotProbe;

internal static class Program
{
    private static int Main(string[] args) => args switch
    {
        ["run", .. var rest] => MatrixRunner.Run(rest),
        ["report", .. var rest] => ReportCommand.Run(rest),
        ["selftest"] => SelfTest.Run(),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.WriteLine($"AotProbe manifest={AotProbeManifest.ManifestVersion} catalog={ProbeCatalog.CatalogVersion} rules={GradingRules.RulesVersion} fixtures={Fixtures.FixtureVersion}");
        Console.WriteLine($"libraries={AotProbeManifest.Libraries.Count} probes={ProbeCatalog.All.Count}");
        Console.WriteLine("usage: aotprobe run --lib <id> [--forms jit,aot] [--groups A,B] [--rid win-x64] [--diagnose]");
        Console.WriteLine("       aotprobe report [--facts <path>] [--out <path>]");
        Console.WriteLine("       aotprobe selftest");
        return 0;
    }
}
