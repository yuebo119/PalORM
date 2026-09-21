using System.Globalization;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.PerfHub;

/// <summary>
/// PerfHub 统一数据集与方言抽象——全项目性能测试的唯一数据真源。
///
/// 设计纪律（对应 docs/性能基准规范.md §2）：
///  · 种子完全确定：所有列值由行号 i 派生，无 Random。任何一次生成的库内容逐位相同，
///    这是"三次测量可比"以及"跨方言可比"的前提；
///  · 行数档位统一：100 / 1_000 / 10_000；
///  · DDL 一律手写但**三方言逐位同构**（列名/类型/约束一致）——PerfHub 不走 MigrateAsync，
///    因为它要在同一进程里对三个方言建同构表，而 MigrateAsync 依赖注册表与方言 DDL 真源；
///    表结构与 bench_s1_narrow 保持一致，使历史数字可延续比较；
///  · 三方言用同一实体同一 SQL 模板，只有引用符与参数占位符按方言分叉。
/// </summary>
internal static class Dataset
{
    /// <summary>统一行数档位（规范 §2）。</summary>
    public static readonly int[] Tiers = [100, 1_000, 10_000];

    /// <summary>点查用的主键集合——由行号确定性派生，三方言三实现完全相同。</summary>
    public static long[] KeySet(int rows)
    {
        var keys = new long[rows];
        for (int i = 0; i < rows; i++) keys[i] = i + 1;   // 主键从 1 起
        return keys;
    }

    /// <summary>确定性构造一行（i 从 0 起）——与 StandardShapes.BenchNarrow.Seed 同口径。</summary>
    public static S1Row Seed(long i) => new()
    {
        Id = i + 1,
        Name = $"row-{i}",
        Qty = (int)(i % 1000),
        Price = i * 0.25m,
        Marker = i
    };

    public static List<S1Row> SeedRows(int count)
    {
        var list = new List<S1Row>(count);
        for (long i = 0; i < count; i++) list.Add(Seed(i));
        return list;
    }

    /// <summary>三方言同构 DDL——列名/类型/约束逐位一致，仅保留字面类型名。</summary>
    public static string CreateTableSql(Dialect dialect) => dialect switch
    {
        Dialect.Sqlite => "CREATE TABLE perf_s1 (\"Id\" INTEGER PRIMARY KEY, \"Name\" TEXT NOT NULL, \"Qty\" INTEGER NOT NULL, \"Price\" TEXT NOT NULL, \"Marker\" INTEGER NOT NULL)",
        Dialect.MySql => "CREATE TABLE perf_s1 (`Id` BIGINT PRIMARY KEY, `Name` TEXT NOT NULL, `Qty` INT NOT NULL, `Price` DECIMAL(18,4) NOT NULL, `Marker` BIGINT NOT NULL)",
        Dialect.PostgreSql => "CREATE TABLE perf_s1 (\"Id\" BIGINT PRIMARY KEY, \"Name\" TEXT NOT NULL, \"Qty\" INTEGER NOT NULL, \"Price\" NUMERIC(18,4) NOT NULL, \"Marker\" BIGINT NOT NULL)",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect))
    };

    public static string DropTableSql(Dialect dialect)
        => $"DROP TABLE IF EXISTS {(dialect == Dialect.MySql ? "`perf_s1`" : "\"perf_s1\"")}";

    /// <summary>引用标识符——三方言各自规则。</summary>
    public static string Q(Dialect dialect, string identifier)
        => dialect == Dialect.MySql ? $"`{identifier}`" : $"\"{identifier}\"";

    /// <summary>参数占位符——三方言统一 @pN（驱动层各自接受）。</summary>
    public static string P(int index) => $"@p{index}";

    /// <summary>SELECT 列清单（同构）。</summary>
    public static string SelectColumns(Dialect dialect)
        => string.Join(", ", new[] { "Id", "Name", "Qty", "Price", "Marker" }.Select(c => Q(dialect, c)));
}

/// <summary>PerfHub 统一实体 S1 Narrow——与 bench_s1_narrow 同构。
/// 同时被 PalORM（源生成）与 Dapper/ADO.NET（反射或手工映射）使用，
/// 保证三实现测的是同一份数据、同一个 CLR 形态。</summary>
[Table("perf_s1")]
public sealed partial class S1Row
{
    [Key(AutoIncrement = false)]
    [Column("Id")]
    public long Id { get; set; }

    [Column("Name")]
    public string Name { get; set; } = "";

    [Column("Qty")]
    public int Qty { get; set; }

    /// <summary>decimal 以 NUMERIC/DECIMAL 落库；SQLite 无原生 decimal，按 TEXT 存字符串形式
    /// （三方言读数一致——SQLite 驱动会把 TEXT 转回 decimal）。</summary>
    [Column("Price")]
    public decimal Price { get; set; }

    [Column("Marker")]
    public long Marker { get; set; }
}

/// <summary>方言标识——PerfHub 内部使用，避免直接依赖 Provider 静态类型分派。</summary>
internal enum Dialect
{
    Sqlite,
    MySql,
    PostgreSql
}

/// <summary>方言元数据与连接工厂。</summary>
internal sealed class DialectInfo
{
    public required Dialect Dialect { get; init; }
    public required string DisplayName { get; init; }
    public required string EnvVar { get; init; }
    public required Func<string, System.Data.Common.DbConnection> OpenConnection { get; init; }

    /// <summary>SQLite 是进程内库，无"服务器版本"概念。</summary>
    public string ServerVersion => Dialect == Dialect.Sqlite ? "in-process (SQLite3MC)" : "see env";

    public static readonly DialectInfo[] All =
    [
        new()
        {
            Dialect = Dialect.Sqlite, DisplayName = "SQLite", EnvVar = "",
            OpenConnection = cs => new Microsoft.Data.Sqlite.SqliteConnection(cs)
        },
        new()
        {
            Dialect = Dialect.MySql, DisplayName = "MySQL", EnvVar = "PALORM_BENCH_MYSQL",
            OpenConnection = cs => new MySqlConnector.MySqlConnection(cs)
        },
        new()
        {
            Dialect = Dialect.PostgreSql, DisplayName = "PostgreSQL", EnvVar = "PALORM_BENCH_PG",
            OpenConnection = cs => new Npgsql.NpgsqlConnection(cs)
        },
    ];

    public static DialectInfo Of(Dialect dialect) => All[(int)dialect];
}

/// <summary>连接串解析——SQLite 用临时文件库（避免共享内存库的跨会话可见性问题），
/// PG/MySQL 从环境变量读。任何凭据不入 git。</summary>
internal static class Connections
{
    public static string Resolve(DialectInfo info)
    {
        if (info.Dialect == Dialect.Sqlite)
        {
            string dir = Path.Combine(Path.GetTempPath(), "palorm-perfhub");
            Directory.CreateDirectory(dir);
            return $"Data Source={Path.Combine(dir, $"perf-{Environment.ProcessId}.db")}";
        }
        string? cs = Environment.GetEnvironmentVariable(info.EnvVar);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"未设置 {info.EnvVar}——{info.DisplayName} 档需要远程库连接串（见 .env.test）。");
        return cs;
    }
}

/// <summary>数字格式化——报告统一口径（不变文化、千分位）。</summary>
internal static class Fmt
{
    public static string N(double v, int decimals = 0)
        => v.ToString($"N{decimals}", CultureInfo.InvariantCulture);

    /// <summary>字节数的可读形式（B / KB / MB / GB）。</summary>
    public static string Bytes(double v) => v switch
    {
        < 1024 => $"{v:N0} B",
        < 1024 * 1024 => $"{v / 1024:N1} KB",
        < 1024 * 1024 * 1024 => $"{v / (1024 * 1024):N2} MB",
        _ => $"{v / (1024 * 1024 * 1024):N2} GB"
    };

    /// <summary>耗时可读形式（ns / µs / ms / s）。</summary>
    public static string Time(double ns) => ns switch
    {
        < 1_000 => $"{ns:N0} ns",
        < 1_000_000 => $"{ns / 1_000:N1} µs",
        < 1_000_000_000 => $"{ns / 1_000_000:N2} ms",
        _ => $"{ns / 1_000_000_000:N2} s"
    };

    public static string Pct(double v) => $"{(v >= 0 ? "+" : "")}{v:N1}%";
}
