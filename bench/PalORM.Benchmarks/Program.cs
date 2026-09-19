using System.Diagnostics.CodeAnalysis;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using PalORM.Sqlite;
using PalORM.PostgreSql;
using PalORM.MySql;
// RepoDb（含 Sqlite 扩展，全部位于 RepoDb 命名空间下：SqliteGlobalConfiguration.UseSqlite）
using RepoDb;

[assembly: DapperAot]
[assembly: SuppressMessage("Design", "CA1515", Justification = "BenchmarkDotNet requires public types.")]

namespace PalORM.Benchmarks;

public static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "CLI 模式分派是顺序 if-return 的天然形态（--boxing/--workload/--memory/--stability/--bdn-debug），分支间无嵌套逻辑。")]
    public static void Main(string[] args)
    {
        // v5.0 阶段 3.4：--boxing 切到手写微基准（绕过 BDN .NET 11 preview 不兼容）
        if (args.Length > 0 && args[0] == "--boxing")
        {
            BoxingMicroBenchmark.RunAsync().GetAwaiter().GetResult();
            return;
        }
        // 并发负载测试（维度 3/4/11，规范 docs/性能基准规范.md）——BDN 单线程测不了锁/池/调度
        if (args.Length > 0 && args[0] == "--workload")
        {
            // --workload [sqlite|pg|mysql] [线程档位CSV]：方言默认 sqlite（pg/mysql 读
            // PALORM_*_CONNECTION）；档位默认 1,2,4,8——找并发拐点时传 1,4,8,16,32,64
            WorkloadHarness.RunAsync(new WorkloadOptions
            {
                Dialect = args.Length > 1 ? args[1] : "sqlite",
                ThreadTiers = args.Length > 2
                    ? [.. args[2].Split(',').Select(static tier => int.Parse(tier.Trim()))]
                    : [1, 2, 4, 8]
            }).GetAwaiter().GetResult();
            return;
        }
        // 长稳测试（维度 10：持续负载抓泄漏/衰减）——--stability <秒> [dialect]
        if (args.Length > 0 && args[0] == "--stability")
        {
            StabilityHarness.RunAsync(
                seconds: args.Length > 1 ? int.Parse(args[1]) : 180,
                dialect: args.Length > 2 ? args[2] : "sqlite").GetAwaiter().GetResult();
            return;
        }
        // 大结果集内存曲线 + 查询构建分配（维度 1/7）
        if (args.Length > 0 && args[0] is string mode && mode == "--memory")
        {
            MemoryProbe.RunAsync(new MemoryOptions()).GetAwaiter().GetResult();
            return;
        }
        // 诊断 BDN RuntimeMoniker 推断
        if (args.Length > 0 && args[0] == "--bdn-debug")
        {
            var attrs = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false);
            var attr = attrs.Length > 0 ? (System.Runtime.Versioning.TargetFrameworkAttribute)attrs[0] : null;
            Console.WriteLine($"TargetFrameworkAttribute.FrameworkName: {attr?.FrameworkName ?? "(null)"}");
            Console.WriteLine($"Environment.Version: {Environment.Version}");
            Console.WriteLine($"RuntimeInformation.FrameworkDescription: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            return;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}

// v5.0 基准体系重构：所有 benchmark 类已迁移到独立文件
// 01_CrudBenchmarks.cs / 02_BulkBenchmarks.cs / 03_GcBenchmarks.cs /
// 04_SqlBuildBenchmarks.cs / 05_SqliteSpeedBenchmarks.cs /
// 06_FeatureBenchmarks.cs / 07_OrmComparisonBenchmarks.cs /
// 08_BinaryBenchmarks.cs /
// PgBenchmarks.cs / MySqlBenchmarks.cs
// 实体定义在 BenchmarkEntities.cs，统一配置在 BenchmarkConfig.cs
