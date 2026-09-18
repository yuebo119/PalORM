using PalORM;

namespace PalORM.Benchmarks;

using System.Globalization;
using System.Runtime.CompilerServices;
using PalORM.Sqlite;

// ─────────────────────────────────────────────────────────────────────────────
// 标准数据形状——全项目基准/探针的唯一权威定义（规范见 docs/性能基准规范.md §2）。
//
// 纪律：
//  · 种子完全确定（值由行号 i 派生，无 Random）——任何一次生成的库内容逐位相同，
//    这是"两次测量可比"的前提；
//  · DDL 一律经 MigrateAsync（源生成产物），禁止手写 CREATE TABLE——手写会绕过方言真源，
//    且使"基准测的库"与"迁移产出的库"不一致；
//  · 行数档位全项目统一：100 / 1_000 / 10_000 / 100_000。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>S1 Narrow——点查/热路径/负载测试的基线形状（主键 + 4 数据列）。
/// 主键非自增：负载测试需要稳定主键做点查与定点更新，自增会让写路径依赖回填顺序。</summary>
[Table("bench_s1_narrow")]
public sealed partial class BenchNarrow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
    [Column("price")] public decimal Price { get; set; }
    [Column("marker")] public long Marker { get; set; }

    /// <summary>按行号 i 确定性构造（i 从 0 起）。</summary>
    public static BenchNarrow Seed(long i) => new()
    {
        Id = i + 1, // 主键从 1 起，便于"行号↔主键"互推
        Name = $"row-{i}",
        Qty = (int)(i % 1000),
        Price = i * 0.25m,
        Marker = i
    };
}

/// <summary>S2 Wide——列宽放大验证（主键 + 19 数据列，覆盖类型白名单的主力类型）。
/// 列表对齐既有宽实体口径（RemoteWideProbe / AotTest 宽表），保证历史数字可延续比较。</summary>
[Table("bench_s2_wide")]
public sealed partial class BenchWide
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
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
    [Column("c19")] public DateTimeOffset C19 { get; set; }

    public static BenchWide Seed(long i) => new()
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
        C19 = DateTimeOffset.UnixEpoch.AddMinutes(i)
    };
}

/// <summary>S3 Sparse——null 密度敏感性（S1 同构、全部可空、确定性 70% null）。</summary>
[Table("bench_s3_sparse")]
public sealed partial class BenchSparse
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string? Name { get; set; }
    [Column("qty")] public int? Qty { get; set; }
    [Column("price")] public decimal? Price { get; set; }
    [Column("marker")] public long? Marker { get; set; }

    public static BenchSparse Seed(long i) => new()
    {
        Id = i + 1,
        // 确定性 70% null：i % 10 < 7 → null，其余按 S1 同式取值
        Name = i % 10 < 7 ? null : $"row-{i}",
        Qty = i % 10 < 7 ? null : (int)(i % 1000),
        Price = i % 10 < 7 ? null : i * 0.25m,
        Marker = i % 10 < 7 ? null : i
    };
}

/// <summary>S4 Blob——二进制列（32 B / 1 KB / 64 KB 三档，内容由行号确定性生成）。</summary>
[Table("bench_s4_blob")]
public sealed partial class BenchBlob
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    // CA1819 误报：ORM 实体列需要可变数组读写
#pragma warning disable CA1819
    [Column("blob_small")] public byte[] BlobSmall { get; set; } = [];
    [Column("blob_medium")] public byte[] BlobMedium { get; set; } = [];
    [Column("blob_large")] public byte[] BlobLarge { get; set; } = [];
#pragma warning restore CA1819

    public static BenchBlob Seed(long i) => new()
    {
        Id = i + 1,
        Name = $"blob-{i}",
        BlobSmall = MakeBytes(i, 32),
        BlobMedium = MakeBytes(i, 1024),
        BlobLarge = MakeBytes(i, 64 * 1024)
    };

    private static byte[] MakeBytes(long seed, int length)
    {
        var bytes = new byte[length];
        // LCG：确定性且覆盖全字节域（含 0x00——SQLite/PG 二进制路径的历史盲区）
        int state = (int)(seed * 2654435761 % int.MaxValue) + 1;
        for (int j = 0; j < length; j++)
        {
            state = (state * 1103515245) + 12345;
            bytes[j] = (byte)(state >> 16);
        }
        return bytes;
    }
}

/// <summary>S5 LongText——长文本（64 字符摘要 + 2000 字符正文，确定性内容）。</summary>
[Table("bench_s5_text")]
public sealed partial class BenchText
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("summary")] public string Summary { get; set; } = "";
    [Column("body")] public string Body { get; set; } = "";

    public static BenchText Seed(long i)
    {
        var body = new System.Text.StringBuilder(2000);
        for (int chunk = 0; chunk < 2000 / 50; chunk++)
        {
            body.Append($"doc-{i}-seg-{chunk:0000} ");
        }
        return new BenchText
        {
            Id = i + 1,
            Summary = $"doc-{i}",
            Body = body.ToString()
        };
    }
}

/// <summary>标准形状的建库与种子辅助。</summary>
public static class StandardShapes
{
    /// <summary>行数档位（全项目统一，报告必须标注所用档位）。</summary>
    public static readonly long[] RowTiers = [100, 1_000, 10_000, 100_000];

    /// <summary>确定性种子 N 行并落库（单次 BulkInsert——内部自带 batchSize 节流与方言分派；
    /// 10 万行档位约 60 MB 实体内存，如需更低峰值再引入分批，不得在循环里逐条插——PALORM005）。
    /// 先清表再种，保证任何一次调用后库内容逐位相同。</summary>
    public static async Task SeedAsync<T>(DataSession<SqliteProvider> session, long rows, CancellationToken ct = default)
        where T : class, new()
    {
        await session.MigrateAsync(ct).ConfigureAwait(false);
        // 表名非参数：ExecuteAsync 的插值洞会变成绑定参数（DELETE FROM @p0 非法），
        // 故经 FormattableStringFactory 以零参数形式注入纯文本 SQL
        string deleteSql = "DELETE FROM " + TableName<T>();
        await session.ExecuteAsync(
            FormattableStringFactory.Create(deleteSql), ct).ConfigureAwait(false);

        List<T> seedRows = Enumerate<T>(0, rows);
        await session.BulkInsertAsync(seedRows, ct: ct).ConfigureAwait(false);
    }

    private static List<T> Enumerate<T>(long start, long count)
        where T : class, new()
    {
        var list = new List<T>((int)Math.Min(count, int.MaxValue));
        for (long i = start; i < start + count; i++)
        {
            list.Add((T)SeedOf<T>(i));
        }
        return list;
    }

    private static object SeedOf<T>(long i) => typeof(T).Name switch
    {
        nameof(BenchNarrow) => BenchNarrow.Seed(i),
        nameof(BenchWide) => BenchWide.Seed(i),
        nameof(BenchSparse) => BenchSparse.Seed(i),
        nameof(BenchBlob) => BenchBlob.Seed(i),
        nameof(BenchText) => BenchText.Seed(i),
        _ => throw new NotSupportedException($"StandardShapes 未登记类型 {typeof(T).Name}")
    };

    private static string TableName<T>() => typeof(T).Name switch
    {
        nameof(BenchNarrow) => "bench_s1_narrow",
        nameof(BenchWide) => "bench_s2_wide",
        nameof(BenchSparse) => "bench_s3_sparse",
        nameof(BenchBlob) => "bench_s4_blob",
        nameof(BenchText) => "bench_s5_text",
        _ => throw new NotSupportedException($"StandardShapes 未登记类型 {typeof(T).Name}")
    };
}
