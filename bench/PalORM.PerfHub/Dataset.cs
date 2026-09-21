using System.Globalization;
using System.Runtime.CompilerServices;
using PalORM.Testing;

namespace PalORM.PerfHub;

/// <summary>
/// PerfHub 统一数据集与方言抽象——全项目性能测试的唯一数据真源。
///
/// 设计纪律（对应 docs/性能基准规范.md §2）：
///  · 种子完全确定：所有列值由行号 i 派生，无 Random。任何一次生成的库内容逐位相同，
///    这是"两次测量可比"以及"跨方言可比"的前提；
///  · 行数档位统一：2_000 / 20_000（v5.8 起，覆盖中小表与中大表两个量级）；
///  · DDL 三方言逐位同构（列名/类型/约束一致）；
///  · 三方言用同一实体、同一 SQL 模板，只有引用符与参数占位符按方言分叉。
/// </summary>
internal static class Dataset
{
    /// <summary>统一行数档位（规范 §2）。</summary>
    public static readonly int[] Tiers = [2_000, 20_000];

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
        for (long i = 0; i < count; i++)
        {
            list.Add(Seed(i));
        }

        return list;
    }

    /// <summary>确定性构造 count 行，主键从 <paramref name="idOffset"/>+1 起。
    /// <para>写操作测量每轮必须用互不相同的主键段，否则第二轮撞主键；
    /// 该重载把「第 i 轮用哪段键」变成显式参数，避免用 Random 造成不可复现。</para></summary>
    public static List<S1Row> SeedRows(int count, long idOffset)
    {
        var list = new List<S1Row>(count);
        for (long i = 0; i < count; i++)
        {
            list.Add(Seed(idOffset + i));
        }

        return list;
    }

    /// <summary>主键集合——由行号确定性派生，三方言三实现完全相同。</summary>
    public static long[] KeySet(int rows)
    {
        long[] keys = new long[rows];
        for (int i = 0; i < rows; i++)
        {
            keys[i] = i + 1;   // 主键从 1 起
        }

        return keys;
    }

    /// <summary>三方言同构 DDL——列名/类型/约束逐位一致。</summary>
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

    /// <summary>实体列名——三方言 SELECT 列表的唯一真源，避免每次调用重建数组（CA1861）。</summary>
    private static readonly string[] ColumnNames = ["Id", "Name", "Qty", "Price", "Marker"];

    public static string SelectColumns(Dialect dialect)
        => string.Join(", ", ColumnNames.Select(c => Q(dialect, c)));

    public static string Table(Dialect dialect) => Q(dialect, "perf_s1");

    /// <summary>PalORM 的 <see cref="SqlDialect"/> 映射到 PerfHub 内部 <see cref="Dialect"/>。
    /// <para>泛型核心方法拿得到的是 <c>TProvider.Dialect</c>（PalORM 侧枚举），
    /// 而 Dataset 的 SQL 模板按 PerfHub 侧枚举分叉——这一层是两套枚举的唯一转换点。</para></summary>
    public static Dialect Of(SqlDialect dialect) => dialect switch
    {
        SqlDialect.Sqlite => Dialect.Sqlite,
        SqlDialect.MySql => Dialect.MySql,
        SqlDialect.PostgreSql => Dialect.PostgreSql,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
    };

    /// <summary>点查条件——列名拼进文本段（经 QuoteIdentifier 转义），只有值是插值项。
    /// <para>踩坑记录（B78）：若写成 <c>$"{Q("Id")} = {id}"</c>，列名也会变成插值项并被参数化，
    /// 生成 <c>WHERE (@p0 = @p1)</c> 且 @p0 是字符串——PG 报 "operator does not exist:
    /// text = bigint"。列名是标识符面，必须走文本段。</para></summary>
    public static FormattableString WhereId(Dialect dialect, long id)
        => FormattableStringFactory.Create(Q(dialect, "Id") + " = {0}", id);

    /// <summary>点查条件（PalORM 侧方言枚举重载）。</summary>
    public static FormattableString WhereId(SqlDialect dialect, long id)
        => WhereId(Of(dialect), id);

    /// <summary>键集分页条件 <c>Id &gt; @p0</c>——列名进文本段。</summary>
    public static FormattableString WhereIdGt(Dialect dialect, long id)
        => FormattableStringFactory.Create(Q(dialect, "Id") + " > {0}", id);

    /// <summary>键集分页条件（PalORM 侧方言枚举重载）。</summary>
    public static FormattableString WhereIdGt(SqlDialect dialect, long id)
        => WhereIdGt(Of(dialect), id);

    /// <summary>IN 条件（列名进文本段，值集合进插值项）。</summary>
    public static FormattableString WhereInIds(Dialect dialect, long[] ids)
    {
        string quoted = Q(dialect, "Id");
        object?[] args = new object?[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            args[i] = ids[i];
        }

        return FormattableStringFactory.Create(quoted + " IN (" + string.Join(", ", Enumerable.Range(0, ids.Length).Select(i => "{" + i + "}")) + ")", args);
    }
}

/// <summary>PerfHub 统一实体 S1 Narrow——与 bench_s1_narrow 同构。
/// 同时被 PalORM（源生成）与 Dapper/ADO.NET（手工映射）使用，
/// 保证三实现测的是同一份数据、同一个 CLR 形态。</summary>
[Table("perf_s1")]
internal sealed partial class S1Row
{
    /// <inheritdoc/>
    [Key(AutoIncrement = false)]
    [Column("Id")]
    public long Id { get; set; }

    /// <summary>可变长文本列——三方言同构，长度随行号确定性增长。</summary>
    [Column("Name")]
    public string Name { get; set; } = "";

    /// <summary>整型数量列——WHERE 条件的另一面，用于范围查询测试。</summary>
    [Column("Qty")]
    public int Qty { get; set; }

    /// <summary>decimal 以 NUMERIC/DECIMAL 落库；SQLite 无原生 decimal，按 TEXT 存字符串形式
    /// （三方言读数一致——SQLite 驱动会把 TEXT 转回 decimal）。</summary>
    [Column("Price")]
    public decimal Price { get; set; }

    /// <summary>顺序标记列——ORDER BY / 键集分页的排序列，值等于行号。</summary>
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

internal static class MySqlConnString
{
    /// <summary>连接串缺 AllowLoadLocalInfile 时追加（存在则原样返回）。</summary>
    public static string WithLocalInfile(string cs)
    {
        if (cs.Contains("AllowLoadLocalInfile", StringComparison.OrdinalIgnoreCase))
        {
            return cs;
        }

        string trimmed = cs.TrimEnd(';');
        return trimmed + ";AllowLoadLocalInfile=true";
    }
}

/// <summary>批量写路径的三臂共用 SQL 真源（PerfHub v2 三臂契约）。
/// <para>ADO 地板与 Dapper 臂必须发与 PalORM 同构的方言最优 SQL，此处集中生成，
/// 杜绝两臂各拼各的漂移。参数顺序统一为 [Name, Qty, Price, Marker, Id]——Id 在行尾，
/// 与产品 <c>BatchUpdateSqlBuilder</c> 的 BindUpdate 序一致。</para>
/// <para><b>SQLite 不走集合式</b>：CASE WHEN 在 SQLite 实测比逐条慢 6.4×（产品既有结论），
/// SQLite 的批量更新最优路径是逐条裹单事务（autocommit 每行一次 journal 同步）。</para></summary>
internal static class BulkSql
{
    public const int ParamsPerRow = 5;
    public const int PkOffsetInRow = 4;

    /// <summary>分批行数基准——与 PalORM 产品的批量插入批宽对齐（1000 行 × 5 参数 = 5000 参数，
    /// PG/MySQL 参数上限 65535 内）。</summary>
    public const int BatchRows = 1000;

    /// <summary>方言有效批宽——SQLite 沿用产品的 999 参数上限口径
    /// （min(1000, 999 ÷ 5) = 199 行/语句），其余方言 1000。</summary>
    public static int EffectiveBatchRows(Dialect dialect)
        => dialect == Dialect.Sqlite ? 199 : BatchRows;

    private static readonly string[] SetColumns = ["Name", "Qty", "Price", "Marker"];

    /// <summary>集合式批量 UPDATE。PG：UPDATE FROM VALUES（产品实测 4×）；
    /// MySQL：CASE WHEN（可移植主流）。SQLite 调用方不得使用。</summary>
    public static string UpdateBatch(Dialect dialect, int rowCount)
    {
        if (dialect == Dialect.Sqlite)
            throw new NotSupportedException("SQLite 批量更新走逐条裹事务（CASE WHEN 慢 6.4×）。");
        string q(string c)
        {
            return Dataset.Q(dialect, c);
        }

        string table = Dataset.Table(dialect);
        var sb = new System.Text.StringBuilder(96 + (rowCount * 48));
        if (dialect == Dialect.PostgreSql)
        {
            sb.Append("UPDATE ").Append(table).Append(" AS tgt SET ");
            for (int c = 0; c < SetColumns.Length; c++)
            {
                if (c > 0) sb.Append(", ");
                sb.Append(q(SetColumns[c])).Append(" = v.col").Append(c);
            }
            sb.Append(" FROM (VALUES ");
            for (int r = 0; r < rowCount; r++)
            {
                if (r > 0) sb.Append(", ");
                AppendRowPlaceholders(sb, r * ParamsPerRow);
            }
            sb.Append(") AS v(col0, col1, col2, col3, col_pk) WHERE tgt.")
                .Append(q("Id")).Append(" = v.col_pk");
            return sb.ToString();
        }

        sb.Append("UPDATE ").Append(table).Append(" SET ");
        for (int c = 0; c < SetColumns.Length; c++)
        {
            if (c > 0) sb.Append(", ");
            sb.Append(q(SetColumns[c])).Append(" = CASE ").Append(q("Id"));
            for (int r = 0; r < rowCount; r++)
            {
                int b = r * ParamsPerRow;
                sb.Append(" WHEN ").Append(Dataset.P(b + PkOffsetInRow))
                    .Append(" THEN ").Append(Dataset.P(b + c));
            }
            sb.Append(" END");
        }
        sb.Append(" WHERE ").Append(q("Id")).Append(" IN (");
        for (int r = 0; r < rowCount; r++)
        {
            if (r > 0) sb.Append(", ");
            sb.Append(Dataset.P((r * ParamsPerRow) + PkOffsetInRow));
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>多值 INSERT 的 VALUES 段（含每行占位符）——批量插入三臂共用。</summary>
    public static string ValuesRows(int rowCount)
    {
        var sb = new System.Text.StringBuilder(16 + (rowCount * 32));
        for (int r = 0; r < rowCount; r++)
        {
            if (r > 0) sb.Append(", ");
            AppendRowPlaceholders(sb, r * ParamsPerRow);
        }
        return sb.ToString();
    }

    private static void AppendRowPlaceholders(System.Text.StringBuilder sb, int baseIdx)
    {
        sb.Append('(');
        for (int c = 0; c < ParamsPerRow; c++)
        {
            if (c > 0) sb.Append(", ");
            sb.Append(Dataset.P(baseIdx + c));
        }
        sb.Append(')');
    }
}

/// <summary>方言元数据与连接工厂。</summary>
internal sealed class DialectInfo
{
    public required Dialect Dialect { get; init; }
    public required string DisplayName { get; init; }
    public required Func<string, System.Data.Common.DbConnection> OpenConnection { get; init; }

    /// <summary>SQLite 是进程内库，无"服务器版本"概念。</summary>
    public string ServerVersion => Dialect == Dialect.Sqlite ? "in-process (SQLite3MC)" : "see env";

    public static readonly DialectInfo[] All =
    [
        new()
        {
            Dialect = Dialect.Sqlite, DisplayName = "SQLite",
            OpenConnection = cs => new Microsoft.Data.Sqlite.SqliteConnection(cs)
        },
        new()
        {
            Dialect = Dialect.MySql, DisplayName = "MySQL",
            // AllowLoadLocalInfile=true：MySqlBulkCopy（LOAD DATA 协议）的前提——地板与
            // PalORM 的 MySQL 批量天花板路径都依赖它。基准环境统一追加，三臂同等生效。
            OpenConnection = cs => new MySqlConnector.MySqlConnection(MySqlConnString.WithLocalInfile(cs))
        },
        new()
        {
            Dialect = Dialect.PostgreSql, DisplayName = "PostgreSQL",
            OpenConnection = cs => new Npgsql.NpgsqlConnection(cs)
        },
    ];

    public static DialectInfo Of(Dialect dialect) => All[(int)dialect];
}

/// <summary>连接串解析——SQLite 用临时文件库（避免共享内存库的跨会话可见性问题），
/// PG/MySQL 从环境变量读。任何凭据不入 git。</summary>
internal static class Connections
{
    /// <summary>凭据口径与集成测试完全一致：环境变量 PALORM_PG_CONNECTION /
    /// PALORM_MYSQL_CONNECTION 优先，仓库根 .env.test 兜底补缺失项（见
    /// <see cref="TestEnvironment.LoadDotEnvIfPresent"/>）。任何凭据不入 git、不回显。</summary>
    public static string Resolve(DialectInfo info) => info.Dialect switch
    {
        Dialect.Sqlite => SqlitePath(),
        Dialect.MySql => TestEnvironment.ResolveMySqlConnectionString(),
        Dialect.PostgreSql => TestEnvironment.ResolvePostgreSqlConnectionString(),
        _ => throw new ArgumentOutOfRangeException(nameof(info), info, null)
    };

    private static string SqlitePath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "palorm-perfhub");
        Directory.CreateDirectory(dir);
        return $"Data Source={Path.Combine(dir, $"perf-{Environment.ProcessId}.db")}";
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

    /// <summary>比例格式化——入参是比例（0.983），输出百分比（+98.3%）。
    /// 负数自带符号，不再额外加正负号（避免出现 "+-1.0%"）。</summary>
    public static string Pct(double fraction)
        => $"{(fraction >= 0 ? "+" : "")}{fraction * 100:N1}%";
}
