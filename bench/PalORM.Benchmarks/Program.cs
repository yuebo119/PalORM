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
    public static void Main(string[] args)
    {
        // v5.0 阶段 3.4：--boxing 切到手写微基准（绕过 BDN .NET 11 preview 不兼容）
        if (args.Length > 0 && args[0] == "--boxing")
        {
            BoxingMicroBenchmark.RunAsync().GetAwaiter().GetResult();
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
