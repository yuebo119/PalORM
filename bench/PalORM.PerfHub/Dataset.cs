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

    // ── 阶段 2 新表 DDL（三方言同构；SQLite 动态类型按存储类映射）──

    public static string DropWideTableSql(Dialect dialect)
        => $"DROP TABLE IF EXISTS {Q(dialect, "perf_wide")}";

    public static string CreateWideTableSql(Dialect dialect) => dialect switch
    {
        Dialect.Sqlite => "CREATE TABLE perf_wide (\"Id\" INTEGER PRIMARY KEY, \"c01\" INTEGER NOT NULL, \"c02\" INTEGER NOT NULL, \"c03\" INTEGER NOT NULL, \"c04\" TEXT NOT NULL, \"c05\" INTEGER NOT NULL, \"c06\" TEXT NOT NULL, \"c07\" REAL NOT NULL, \"c08\" REAL NOT NULL, \"c09\" TEXT NOT NULL, \"c10\" TEXT NOT NULL, \"c11\" INTEGER, \"c12\" INTEGER, \"c13\" TEXT, \"c14\" TEXT, \"c15\" INTEGER, \"c16\" INTEGER NOT NULL, \"c17\" INTEGER, \"c18\" REAL, \"c19\" TEXT NOT NULL)",
        Dialect.MySql => "CREATE TABLE perf_wide (`Id` BIGINT PRIMARY KEY, `c01` BIGINT NOT NULL, `c02` INT NOT NULL, `c03` SMALLINT NOT NULL, `c04` TEXT NOT NULL, `c05` TINYINT(1) NOT NULL, `c06` DECIMAL(38,9) NOT NULL, `c07` DOUBLE NOT NULL, `c08` FLOAT NOT NULL, `c09` DATETIME(6) NOT NULL, `c10` CHAR(36) NOT NULL, `c11` INT, `c12` BIGINT, `c13` TEXT, `c14` DECIMAL(38,9), `c15` TINYINT(1), `c16` TINYINT UNSIGNED NOT NULL, `c17` SMALLINT, `c18` DOUBLE, `c19` DATETIME(6) NOT NULL)",
        Dialect.PostgreSql => "CREATE TABLE perf_wide (\"Id\" BIGINT PRIMARY KEY, \"c01\" BIGINT NOT NULL, \"c02\" INTEGER NOT NULL, \"c03\" SMALLINT NOT NULL, \"c04\" TEXT NOT NULL, \"c05\" BOOLEAN NOT NULL, \"c06\" NUMERIC(38,9) NOT NULL, \"c07\" DOUBLE PRECISION NOT NULL, \"c08\" REAL NOT NULL, \"c09\" TIMESTAMP NOT NULL, \"c10\" UUID NOT NULL, \"c11\" INTEGER, \"c12\" BIGINT, \"c13\" TEXT, \"c14\" NUMERIC(38,9), \"c15\" BOOLEAN, \"c16\" SMALLINT NOT NULL, \"c17\" SMALLINT, \"c18\" DOUBLE PRECISION, \"c19\" TIMESTAMP NOT NULL)",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
    };

    public static string DropAutoIncTableSql(Dialect dialect)
        => $"DROP TABLE IF EXISTS {Q(dialect, "perf_autoinc")}";

    public static string CreateAutoIncTableSql(Dialect dialect) => dialect switch
    {
        Dialect.Sqlite => "CREATE TABLE perf_autoinc (\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Qty\" INTEGER NOT NULL)",
        Dialect.MySql => "CREATE TABLE perf_autoinc (`Id` BIGINT PRIMARY KEY AUTO_INCREMENT, `Name` TEXT NOT NULL, `Qty` INT NOT NULL)",
        Dialect.PostgreSql => "CREATE TABLE perf_autoinc (\"Id\" BIGSERIAL PRIMARY KEY, \"Name\" TEXT NOT NULL, \"Qty\" INTEGER NOT NULL)",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
    };

    public static string DropChildTableSql(Dialect dialect)
        => $"DROP TABLE IF EXISTS {Q(dialect, "perf_child")}";

    public static string CreateChildTableSql(Dialect dialect) => dialect switch
    {
        Dialect.Sqlite => "CREATE TABLE perf_child (\"ChildId\" INTEGER PRIMARY KEY, \"ParentId\" INTEGER NOT NULL, \"Note\" TEXT NOT NULL)",
        Dialect.MySql => "CREATE TABLE perf_child (`ChildId` BIGINT PRIMARY KEY, `ParentId` BIGINT NOT NULL, `Note` TEXT NOT NULL)",
        Dialect.PostgreSql => "CREATE TABLE perf_child (\"ChildId\" BIGINT PRIMARY KEY, \"ParentId\" BIGINT NOT NULL, \"Note\" TEXT NOT NULL)",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
    };

    /// <summary>宽表列名——三方言 SELECT 列表的唯一真源（CA1861：static readonly）。</summary>
    private static readonly string[] WideColumnNames =
    [
        "Id", "c01", "c02", "c03", "c04", "c05", "c06", "c07", "c08", "c09",
        "c10", "c11", "c12", "c13", "c14", "c15", "c16", "c17", "c18", "c19"
    ];

    public static string WideSelectColumns(Dialect dialect)
        => string.Join(", ", WideColumnNames.Select(c => Q(dialect, c)));

    public static string WideTable(Dialect dialect) => Q(dialect, "perf_wide");
    public static string AutoIncTable(Dialect dialect) => Q(dialect, "perf_autoinc");
    public static string ChildTable(Dialect dialect) => Q(dialect, "perf_child");

    /// <summary>子表种子——每父确定性 3 子（parent p 的子键为 p*3+1..p*3+3）。</summary>
    public static List<ChildRow> SeedChildren(int parents)
    {
        var list = new List<ChildRow>(parents * 3);
        for (long parent = 1; parent <= parents; parent++)
        {
            for (int k = 0; k < 3; k++)
            {
                list.Add(new ChildRow
                {
                    ChildId = (parent * 3) + k,
                    ParentId = parent,
                    Note = $"c{parent}-{k}",
                });
            }
        }
        return list;
    }


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

    /// <summary>父页过滤 <c>Id &lt;= @p0</c>——IncludeJoin 的父行上界（列名进文本段）。</summary>
    public static FormattableString WhereIdLe(Dialect dialect, long id)
        => FormattableStringFactory.Create(Q(dialect, "Id") + " <= {0}", id);

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

// ═══════════════════════════════════════════════════════════════════════
// 阶段 2 新数据集：宽表 / 自增回填 / 1:N 子表（v2 方案 §3）
// ═══════════════════════════════════════════════════════════════════════

/// <summary>宽表实体（19 列）——物化器按列数伸缩性的测量载体。
/// 列型与 StandardShapes.S2 Wide 对齐，仅两处偏差：
/// ① DateTimeOffset → DateTime（MySQL 无带偏移类型，跨方言往返有损）；
/// ② 表名 perf_wide（PerfHub 命名域）。</summary>
[Table("perf_wide")]
public sealed partial class WideRow
{
    [Key(AutoIncrement = false)]
    [Column("Id")]
    public long Id { get; set; }

    [Column("c01")] public long C01 { get; set; }
    [Column("c02")] public int C02 { get; set; }
    [Column("c03")] public short C03 { get; set; }
    [Column("c04")] public string C04 { get; set; } = "";
    [Column("c05")] public bool C05 { get; set; }
    [Column("c06")] public decimal C06 { get; set; }
    [Column("c07")] public double C07 { get; set; }
    [Column("c08")] public float C08 { get; set; }
    [Column("c09")] public DateTime C09 { get; set; }
    [Column("c10")] public Guid C10 { get; set; }
    [Column("c11")] public int? C11 { get; set; }
    [Column("c12")] public long? C12 { get; set; }
    [Column("c13")] public string? C13 { get; set; }
    [Column("c14")] public decimal? C14 { get; set; }
    [Column("c15")] public bool? C15 { get; set; }
    [Column("c16")] public byte C16 { get; set; }
    [Column("c17")] public short? C17 { get; set; }
    [Column("c18")] public double? C18 { get; set; }
    [Column("c19")] public DateTime C19 { get; set; }

    /// <summary>确定性种子——列型白名单主力类型全覆盖，与 S2 Wide 同式。</summary>
    public static WideRow Seed(long i) => new()
    {
        Id = i + 1,
        C01 = i,
        C02 = (int)(i % int.MaxValue),
        C03 = (short)(i % short.MaxValue),
        C04 = $"w{i}",
        C05 = i % 2 == 0,
        C06 = i * 0.5m,
        C07 = i * 1.5,
        C08 = i * 0.5f,
        C09 = DateTime.UnixEpoch.AddSeconds(i),
        C10 = new Guid((int)(i % int.MaxValue), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        C11 = i % 3 == 0 ? null : (int)i,
        C12 = i % 5 == 0 ? null : i,
        C13 = i % 4 == 0 ? null : $"n{i}",
        C14 = i % 6 == 0 ? null : i * 0.1m,
        C15 = i % 7 == 0 ? null : i % 2 == 0,
        C16 = (byte)(i % byte.MaxValue),
        C17 = i % 8 == 0 ? null : (short)(i % 100),
        C18 = i % 9 == 0 ? null : i * 0.25,
        C19 = DateTime.UnixEpoch.AddMinutes(i)
    };
}

/// <summary>自增回填实体——InsertReturningId 测量载体（主键由数据库生成）。</summary>
[Table("perf_autoinc")]
public sealed partial class AutoIncRow
{
    [Key(AutoIncrement = true)]
    [Column("Id")]
    public long Id { get; set; }

    [Column("Name")] public string Name { get; set; } = "";
    [Column("Qty")] public int Qty { get; set; }
}

/// <summary>1:N 子实体——IncludeJoin 测量载体（每父确定性 3 子）。</summary>
[Table("perf_child")]
public sealed partial class ChildRow
{
    [Key(AutoIncrement = false)]
    [Column("ChildId")]
    public long ChildId { get; set; }

    [Column("ParentId")] public long ParentId { get; set; }
    [Column("Note")] public string Note { get; set; } = "";
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

    /// <summary>多行 UPSERT 的完整 SQL——PG/SQLite 用 ON CONFLICT DO UPDATE（excluded 引用），
    /// MySQL 用 ON DUPLICATE KEY UPDATE（VALUES(c) 引用，与产品 BuildUpsertSqlShape 同形态）。
    /// 与产品差异说明：产品注释称 MySQL 8.0.20+ 推荐行别名语法（AS new），但产品实现
    /// 维持 VALUES(c) 形态与其单行 upsert 一致——三臂共用本形态保证同构。</summary>
    public static string UpsertBatch(Dialect dialect, int rowCount)
    {
        string q(string c)
        {
            return Dataset.Q(dialect, c);
        }

        string[] upsertColumns = ["Name", "Qty", "Price", "Marker"];
        string conflict = dialect == Dialect.MySql
            ? " ON DUPLICATE KEY UPDATE " + string.Join(", ",
                upsertColumns.Select(c => $"{q(c)} = VALUES({q(c)})"))
            : " ON CONFLICT (" + q("Id") + ") DO UPDATE SET " + string.Join(", ",
                upsertColumns.Select(c => $"{q(c)} = excluded.{q(c)}"));
        return "INSERT INTO " + Dataset.Table(dialect) + " (" + Dataset.SelectColumns(dialect)
            + ") VALUES " + ValuesRows(rowCount) + conflict;
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

    /// <summary>进度用的时长形式（秒 / m分s秒 / h时m分），不带小数——进度条要的是量级不是精度。</summary>
    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1)
        {
            return $"{(int)t.TotalHours}h{t.Minutes}m";
        }

        return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:D2}s" : $"{t.TotalSeconds:F0}s";
    }

    /// <summary>比例格式化——入参是比例（0.983），输出百分比（+98.3%）。
    /// 负数自带符号，不再额外加正负号（避免出现 "+-1.0%"）。</summary>
    public static string Pct(double fraction)
        => $"{(fraction >= 0 ? "+" : "")}{fraction * 100:N1}%";
}
