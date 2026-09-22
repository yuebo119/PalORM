using BenchmarkDotNet.Running;

namespace PalORM.DapperSuite;

/// <summary>Dapper 官方基准套件的 PalORM 三臂移植版运行器。
///
/// 用法（方言经环境变量选择，与官方单库运行方式对应）：
///   DAPPER_SUITE_DIALECT=sqlite dotnet run -c Release -- -f * --join
///   DAPPER_SUITE_DIALECT=mysql  dotnet run -c Release -- -f * --join   （需 PALORM_MYSQL_CONNECTION）
///   DAPPER_SUITE_DIALECT=pg     dotnet run -c Release -- -f * --join   （需 PALORM_PG_CONNECTION）
///
/// 数据集与方法学真源：DapperLib/Dapper benchmarks/Dapper.Tests.Performance
///（Post.cs 13 列 POCO / Step() 轮转 1..5000 / Config.cs ShortRun+unroll500）。
/// 方言适配与口径差登记见各文件头注释。</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine($"[DapperSuite] 方言 = {Database.Dialect}（DAPPER_SUITE_DIALECT）");
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("用法: dotnet run -c Release -- -f * --join");
            return 0;
        }
        _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new Config());
        return 0;
    }
}
