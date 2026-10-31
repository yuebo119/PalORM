using System.Globalization;
using System.Text;
using PalORM.Integration.Tests.Conformance;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

// 类型保真矩阵（方案 §6.2 / TYPE-1..6）。三方言跑同一批边界值：
// 每格要么精确往返，要么按文档登记的方言损失断言（docs/API参考.md「方言差异与注意事项」
// 的「类型保真矩阵」表）。期望值来自 2026-10-31 三方言实测（artifacts/type-fidelity-*.csv），
// 每格区分「引擎精度/驱动限制」与「PalORM 行为」。
// 外部库用例统一 [NotInParallel("ExtBulkTable")]，每格独立会话（见 RunAsync 注释）。

#region Entities

/// <summary>时间类型边界载体。</summary>
[Table("tf_temporal")]
public partial class TfTemporalEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("v_datetime")] public DateTime VDateTime { get; set; }
    [Column("v_dto")] public DateTimeOffset VDto { get; set; }
    [Column("v_dateonly")] public DateOnly VDateOnly { get; set; }
    [Column("v_timeonly")] public TimeOnly VTimeOnly { get; set; }
}

/// <summary>数值类型边界载体。</summary>
[Table("tf_numeric")]
public partial class TfNumericEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("v_decimal")] public decimal VDecimal { get; set; }
    [Column("v_double")] public double VDouble { get; set; }
    [Column("v_float")] public float VFloat { get; set; }
}

/// <summary>标量类型边界载体（Guid / 二进制 / 字符 / 字符串）。</summary>
[Table("tf_scalar")]
public partial class TfScalarEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("v_guid")] public Guid VGuid { get; set; }
#pragma warning disable CA1819 // ORM 列需要可变数组读写
    [Column("v_bytes")] public byte[]? VBytes { get; set; }
#pragma warning restore CA1819
    [Column("v_char")] public char VChar { get; set; }
    [Column("v_string")] public string? VString { get; set; }
    [Column("v_bool")] public bool VBool { get; set; }
    [Column("v_long")] public long VLong { get; set; }
}

/// <summary>枚举边界载体（Int32 / Int64 / Flags）。</summary>
[Table("tf_enum")]
public partial class TfEnumRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("v_i32", StoreAs = StoreAs.AsInt32)] public TfSignedValue VInt32 { get; set; }
    [Column("v_i64", StoreAs = StoreAs.AsInt64)] public TfLongValue VInt64 { get; set; }
    [Column("v_flags", StoreAs = StoreAs.AsInt32)] public TfFlagsValue VFlags { get; set; }
}

public enum TfSignedValue
{
    Negative = -7,
    Zero = 0,
    Positive = 3
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1028:Enum Storage should be Int32",
    Justification = "long 底层是矩阵的被测目标（StoreAs.AsInt64），不能改成 int")]
public enum TfLongValue : long
{
    None = 0,
    BelowZero = -1,
    Huge = 5_000_000_000
}

[Flags]
public enum TfFlagsValue
{
    None = 0,
    A = 1,
    B = 2,
    C = 4
}

#endregion

/// <summary>类型保真矩阵（TYPE-2..6）。
/// <para><b>判定口径</b>：每格两种合法结果——精确往返，或文档登记的方言损失（引擎精度、
/// 驱动拒绝、列型上限）。损失格断言的不是"当前行为"，而是 2026-10-31 实测后经判定为
/// 合理方言限制的行为，登记位置见 docs/API参考.md「类型保真矩阵」表。</para></summary>
[NotInParallel("ExtBulkTable")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
    "S1244:Do not check floating point equality with exact values, use a range instead",
    Justification = "类型保真矩阵的判据就是逐位精确往返；容差比较会掩盖精度损失")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
    "S107:Methods should not have too many parameters",
    Justification = "表驱动执行器把用例输入、构造、投影、期望与比较器一次传入；拆对象只是搬运参数")]
public sealed class TypeFidelityMatrixTests
{
    private const string SqliteDialect = "sqlite";
    private const string PgDialect = "postgresql";
    private const string MySqlDialect = "mysql";

    private static bool IsPg(string dialect) => string.Equals(dialect, PgDialect, StringComparison.Ordinal);

    private static bool IsMySql(string dialect) => string.Equals(dialect, MySqlDialect, StringComparison.Ordinal);

    [Test]
    public async Task Sqlite_TypeMatrix()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("type_fidelity");
        await RunAsync(SqliteDialect, async () => await shared.OpenSessionAsync());
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_TypeMatrix()
        => await RunAsync<PostgreSqlProvider>(PgDialect, static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_TypeMatrix()
        => await RunAsync<MySqlProvider>(MySqlDialect, static () => TestDb.MySqlAsync());

    /// <summary>每格一个独立会话：PG 在客户端绑定拒绝（如非 UTC 的 DateTimeOffset）后
    /// 连接进入 broken 态，复用会话时后续命令一律 "Connection is not open"。类型保真测的是
    /// 单值往返，不是会话恢复，故按格隔离——污染不跨格。</summary>
    private static async Task RunAsync<TProvider>(
        string dialect, Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        await using (DataSession<TProvider> migrate = await factory())
        {
            await migrate.MigrateAsync();
        }

        List<string> lines = [$"# dialect={dialect}", "case,kind,outcome,value"];
        List<string> failures = [];

        foreach (TemporalCase c in TemporalCases())
            await VerifyAsync(lines, failures, dialect, c.Name, "temporal", c, Create,
                ReadTemporal, TemporalExpectation(dialect, c), CompareTemporal, factory);

        foreach (NumericCase c in NumericCases())
            await VerifyAsync(lines, failures, dialect, c.Name, "numeric", c, Create,
                ReadNumeric, NumericExpectation(dialect, c), CompareNumeric, factory);

        foreach (ScalarCase c in ScalarCases())
            await VerifyAsync(lines, failures, dialect, c.Name, "scalar", c, Create,
                ReadScalar, ScalarExpectation(dialect, c), CompareScalar, factory);

        foreach (EnumCase c in EnumCases())
            await VerifyAsync(lines, failures, dialect, c.Name, "enum", c, Create,
                ReadEnum, EnumExpectation(c), CompareEnum, factory);

        WriteArtifact(dialect, lines);
        await Assert.That(string.Join("\n", failures)).IsEmpty();
    }

    // ── 执行与判定骨架 ─────────────────────────────────────────

    private sealed record Expect<T>(bool Throws, string Marker, T? Value);

    private static async Task VerifyAsync<TProvider, TEntity, TCase>(
        List<string> lines,
        List<string> failures,
        string dialect,
        string caseName,
        string kind,
        TCase input,
        Func<TCase, TEntity> create,
        Func<TEntity, string> read,
        Expect<TEntity> expected,
        Action<TEntity, TEntity, List<string>, string> compare,
        Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
        where TEntity : class, new()
    {
        TEntity? row = null;
        Exception? error = null;
        string raw;
        await using (DataSession<TProvider> db = await factory())
        {
            try
            {
                await DeleteAsync(db);
                await db.InsertAsync(create(input));
                row = await db.GetAsync<TEntity>(1L);
                raw = row is null ? "null" : read(row);
            }
            catch (Exception ex)
            {
                error = ex;
                raw = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        string outcome = error is null ? "ok" : "throw";
        lines.Add($"{caseName},{kind},{outcome},{Escape(raw)}");

        string? failure = Judge(expected, error, row, raw, $"{caseName}/{dialect}");
        if (failure is not null)
        {
            failures.Add(failure);
            return;
        }
        if (row is not null)
        {
            compare(expected.Value!, row, failures, $"{caseName}/{dialect}");
        }
    }

    private static string? Judge<T>(
        Expect<T> expected, Exception? error, T? row, string raw, string where)
    {
        if (expected.Throws)
        {
            if (error is null) return $"{where}: 期望方言拒绝，实际写入成功（{raw}）";
            return Matches(error, expected.Marker)
                ? null
                : $"{where}: 异常与登记不符，期望 [{expected.Marker}]，实际 {error.GetType().Name}: {error.Message}";
        }
        if (error is not null) return $"{where}: 期望成功，实际 {error.GetType().Name}: {error.Message}";
        return row is null ? $"{where}: 读回行为 null" : null;
    }

    private static bool Matches(Exception error, string marker)
        => marker.Split('|').Any(m =>
            error.GetType().Name.Contains(m, StringComparison.Ordinal)
            || error.Message.Contains(m, StringComparison.Ordinal));

    private static async Task DeleteAsync<TProvider>(DataSession<TProvider> db)
        where TProvider : IDbProvider
    {
        await db.ExecuteAsync($"DELETE FROM tf_temporal");
        await db.ExecuteAsync($"DELETE FROM tf_numeric");
        await db.ExecuteAsync($"DELETE FROM tf_scalar");
        await db.ExecuteAsync($"DELETE FROM tf_enum");
    }

    // ── 期望值：时间 ───────────────────────────────────────────

    private static Expect<TfTemporalEntity> TemporalExpectation(string dialect, TemporalCase c)
    {
        if (IsPg(dialect))
        {
            // Npgsql 契约：timestamptz 只接受 Utc / offset 0（客户端拒绝，ArgumentException）。
            if (c.Dt.Kind != DateTimeKind.Utc) return new(true, "ArgumentException", null);
            if (c.Dto.Offset != TimeSpan.Zero) return new(true, "ArgumentException", null);
            // Npgsql 默认把 DateTime.MinValue/MaxValue 映射为 PG 的 ±infinity，读回 Kind=Unspecified。
            DateTime dt = c.Dt == DateTime.MinValue || c.Dt == DateTime.MaxValue
                ? DateTime.SpecifyKind(c.Dt, DateTimeKind.Unspecified)
                : DateTime.SpecifyKind(TruncateToMicrosecond(c.Dt), DateTimeKind.Utc);
            return new(false, "", new TfTemporalEntity
            {
                Id = 1,
                VDateTime = dt,
                VDto = c.Dto,
                VDateOnly = c.Date,
                VTimeOnly = TruncateToMicrosecond(c.Time)
            });
        }

        if (IsMySql(dialect))
        {
            // DATETIME(6)/TIME(6)：微秒精度，Kind 与 offset 都不存储；
            // DateTimeOffset 由驱动先归一为 UTC 再写墙钟列（瞬时保留、offset 丢失）。
            return new(false, "", new TfTemporalEntity
            {
                Id = 1,
                VDateTime = DateTime.SpecifyKind(TruncateToMicrosecond(c.Dt), DateTimeKind.Unspecified),
                VDto = c.Dto.ToUniversalTime(),
                VDateOnly = c.Date,
                VTimeOnly = TruncateToMicrosecond(c.Time)
            });
        }

        // SQLite：TEXT 按 ISO 8601 存 7 位小数，时间全部精确；Kind 恒不保留，offset 保留。
        return new(false, "", new TfTemporalEntity
        {
            Id = 1,
            VDateTime = DateTime.SpecifyKind(c.Dt, DateTimeKind.Unspecified),
            VDto = c.Dto,
            VDateOnly = c.Date,
            VTimeOnly = c.Time
        });
    }

    private static DateTime TruncateToMicrosecond(DateTime value)
        => new(value.Ticks - (value.Ticks % 10), value.Kind);

    private static TimeOnly TruncateToMicrosecond(TimeOnly value)
        => new(value.Ticks - (value.Ticks % 10));

    private static void CompareTemporal(
        TfTemporalEntity expected, TfTemporalEntity actual, List<string> failures, string where)
    {
        CompareDateTime("dt", expected.VDateTime, actual.VDateTime, failures, where);
        CompareOffset("dto", expected.VDto, actual.VDto, failures, where);
        if (expected.VDateOnly.DayNumber != actual.VDateOnly.DayNumber)
            failures.Add($"{where}: date 期望 {expected.VDateOnly:O} 实际 {actual.VDateOnly:O}");
        if (expected.VTimeOnly.Ticks != actual.VTimeOnly.Ticks)
            failures.Add($"{where}: time 期望 {expected.VTimeOnly:O} 实际 {actual.VTimeOnly:O}");
    }

    private static void CompareDateTime(
        string column, DateTime expected, DateTime actual, List<string> failures, string where)
    {
        if (expected.Ticks != actual.Ticks || expected.Kind != actual.Kind)
        {
            failures.Add(
                $"{where}: {column} 期望 {expected:O}({expected.Kind}) 实际 {actual:O}({actual.Kind})");
        }
    }

    /// <summary>DateTimeOffset 的 <c>==</c> 只比瞬时；offset 丢失必须单独断言。</summary>
    private static void CompareOffset(
        string column, DateTimeOffset expected, DateTimeOffset actual, List<string> failures, string where)
    {
        if (expected.UtcDateTime.Ticks != actual.UtcDateTime.Ticks || expected.Offset != actual.Offset)
        {
            failures.Add(
                $"{where}: {column} 期望 {expected:O} 实际 {actual:O}（瞬时或 offset 不符）");
        }
    }

    // ── 期望值：数值 ───────────────────────────────────────────

    /// <summary>DECIMAL(18,6) 的容纳上限：|值| 必须严格小于 1e12。</summary>
    private const decimal Decimal18_6Limit = 1_000_000_000_000m;

    private static Expect<TfNumericEntity> NumericExpectation(string dialect, NumericCase c)
        => IsPg(dialect) || IsMySql(dialect)
            ? RemoteNumericExpectation(dialect, c)
            : SqliteNumericExpectation(c);

    /// <summary>PG/MySQL：DECIMAL(18,6) 列。超出 18 位总精度由服务端报错；scale 7 舍入到 6 位。</summary>
    private static Expect<TfNumericEntity> RemoteNumericExpectation(string dialect, NumericCase c)
    {
        if (c.Decimal >= Decimal18_6Limit || c.Decimal <= -Decimal18_6Limit)
        {
            return new(true, IsPg(dialect) ? "numeric field overflow" : "Out of range", null);
        }

        decimal dec = decimal.Round(c.Decimal, 6, MidpointRounding.AwayFromZero);
        double dbl = c.Double;
        float flt = c.Float;

        if (IsMySql(dialect))
        {
            // MySQL 的 DOUBLE 不支持 NaN/Infinity（驱动把字面量发成未加引号的 NaN/Infinity）。
            if (double.IsNaN(dbl) || double.IsInfinity(dbl))
                return new(true, "MySqlException", null);
            // REAL 是 4 字节 FLOAT：float.MaxValue 的库内表示略超上限，驱动拒绝回读。
            if (float.IsNaN(flt) || float.IsInfinity(flt) || MathF.Abs(flt) == float.MaxValue)
                return new(true, "MySqlException|InvalidCastException", null);
            // MySQL/InnoDB 的 DOUBLE 不保留负零符号。
            if (IsNegativeZero(dbl)) dbl = 0.0;
        }

        return new(false, "", new TfNumericEntity
        {
            Id = 1, VDecimal = dec, VDouble = dbl, VFloat = flt
        });
    }

    /// <summary>SQLite：无 DECIMAL，decimal 列按 REAL（double）存读；NaN 被驱动拒绝；-0.0 符号不保留。</summary>
    private static Expect<TfNumericEntity> SqliteNumericExpectation(NumericCase c)
    {
        decimal sqliteDec;
        try
        {
            // SQLite 的 decimal 列按 REAL 存读：读回路径经 double.ToString("R") 再解析，
            // 得到的是 double 的最短往返十进制表示（17 位内），不是二进制尾数展开。
            sqliteDec = decimal.Parse(
                ((double)c.Decimal).ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return new(true, "OverflowException", null);
        }

        if (double.IsNaN(c.Double) || float.IsNaN(c.Float))
            return new(true, "InvalidOperationException", null);

        double sqliteDbl = IsNegativeZero(c.Double) ? 0.0 : c.Double;

        return new(false, "", new TfNumericEntity
        {
            Id = 1, VDecimal = sqliteDec, VDouble = sqliteDbl, VFloat = c.Float
        });
    }

    private static bool IsNegativeZero(double value)
        => BitConverter.DoubleToInt64Bits(value) == BitConverter.DoubleToInt64Bits(-0.0);

    private static void CompareNumeric(
        TfNumericEntity expected, TfNumericEntity actual, List<string> failures, string where)
    {
        if (expected.VDecimal != actual.VDecimal)
            failures.Add($"{where}: dec 期望 {expected.VDecimal} 实际 {actual.VDecimal}");
        if (!DoubleEquals(expected.VDouble, actual.VDouble))
            failures.Add($"{where}: dbl 期望 {Format(expected.VDouble)} 实际 {Format(actual.VDouble)}");
        if (!FloatEquals(expected.VFloat, actual.VFloat))
            failures.Add($"{where}: flt 期望 {Format(expected.VFloat)} 实际 {Format(actual.VFloat)}");
    }

    /// <summary>位级比较：NaN 视为相等，且能识别 -0.0 与 +0.0 的差异（<c>==</c> 不能）。</summary>
    private static bool DoubleEquals(double expected, double actual)
        => double.IsNaN(expected) && double.IsNaN(actual)
            || BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual);

    private static bool FloatEquals(float expected, float actual)
        => float.IsNaN(expected) && float.IsNaN(actual)
            || BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual);

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    // ── 期望值：标量 ───────────────────────────────────────────

    private static Expect<TfScalarEntity> ScalarExpectation(string dialect, ScalarCase c)
    {
        if (c.Char == '\0' && IsPg(dialect))
        {
            // PG TEXT 不接受 0x00（UTF8 编码下 0x00 非法）。
            return new(true, "PostgresException|invalid byte sequence", null);
        }

        string? str = c.Str;
        if (str is not null)
        {
            if (HasLoneSurrogate(str))
            {
                // PG 编码器拒绝孤立代理项；SQLite/MySQL 驱动静默替换为 U+FFFD。
                if (IsPg(dialect)) return new(true, "EncoderFallbackException", null);
                str = ReplaceLoneSurrogates(str);
            }
            // MySQL 的 string 列是 TEXT，上限 65535 字节。
            if (IsMySql(dialect) && Encoding.UTF8.GetByteCount(str) > 65535)
                return new(true, "MySqlException|Data too long", null);
        }

        return new(false, "", new TfScalarEntity
        {
            Id = 1,
            VGuid = c.Guid,
            VBytes = c.Bytes,
            VChar = c.Char,
            VString = str,
            VBool = c.Bool,
            VLong = c.Long
        });
    }

    private static bool HasLoneSurrogate(string value)
    {
        int index = 0;
        while (index < value.Length)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    index += 2;
                    continue;
                }
                return true;
            }
            if (char.IsLowSurrogate(value[index])) return true;
            index++;
        }
        return false;
    }

    /// <summary>与 UTF-8 编码器的替换行为一致：孤立代理项写成 U+FFFD。</summary>
    private static string ReplaceLoneSurrogates(string value)
        => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value));

    private static void CompareScalar(
        TfScalarEntity expected, TfScalarEntity actual, List<string> failures, string where)
    {
        if (expected.VGuid != actual.VGuid)
            failures.Add($"{where}: guid 期望 {expected.VGuid} 实际 {actual.VGuid}");
        if (expected.VBytes is null != actual.VBytes is null
            || (expected.VBytes is not null && !expected.VBytes.AsSpan().SequenceEqual(actual.VBytes)))
        {
            failures.Add($"{where}: bytes 不一致（期望 {Describe(expected.VBytes)} 实际 {Describe(actual.VBytes)}）");
        }
        if (expected.VChar != actual.VChar)
            failures.Add($"{where}: char 期望 {(int)expected.VChar} 实际 {(int)actual.VChar}");
        if (!string.Equals(expected.VString, actual.VString, StringComparison.Ordinal))
            failures.Add($"{where}: string 期望 {Describe(expected.VString)} 实际 {Describe(actual.VString)}");
        if (expected.VBool != actual.VBool)
            failures.Add($"{where}: bool 期望 {expected.VBool} 实际 {actual.VBool}");
        if (expected.VLong != actual.VLong)
            failures.Add($"{where}: long 期望 {expected.VLong} 实际 {actual.VLong}");
    }

    private static string Describe(byte[]? value) => value is null ? "null" : $"byte[{value.Length}]";

    private static string Describe(string? value)
        => value is null ? "null" : $"\"{value}\"(len={value.Length})";

    // ── 期望值：枚举 ───────────────────────────────────────────

    /// <summary>枚举三方言都按声明形态存储（AsInt32/AsInt64 整数列），负值与 Flags 组合无损失。</summary>
    private static Expect<TfEnumRow> EnumExpectation(EnumCase c)
        => new(false, "", new TfEnumRow { Id = 1, VInt32 = c.Int32, VInt64 = c.Int64, VFlags = c.Flags });

    private static void CompareEnum(TfEnumRow expected, TfEnumRow actual, List<string> failures, string where)
    {
        if (expected.VInt32 != actual.VInt32)
            failures.Add($"{where}: i32 期望 {expected.VInt32} 实际 {actual.VInt32}");
        if (expected.VInt64 != actual.VInt64)
            failures.Add($"{where}: i64 期望 {expected.VInt64} 实际 {actual.VInt64}");
        if (expected.VFlags != actual.VFlags)
            failures.Add($"{where}: flags 期望 {expected.VFlags} 实际 {actual.VFlags}");
    }

    // ── 观测记录（CSV 落 artifacts/，TimeKindMatrixTests 同口径） ──────────

    private static string Escape(string value)
        => value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal);

    private static string ReadTemporal(TfTemporalEntity e)
        => $"dt={e.VDateTime:O}|kind={e.VDateTime.Kind}|dto={e.VDto:O}|date={e.VDateOnly:O}|time={e.VTimeOnly:O}";

    private static string ReadNumeric(TfNumericEntity e)
        => $"dec={e.VDecimal.ToString(CultureInfo.InvariantCulture)}|dbl={Format(e.VDouble)}|flt={Format(e.VFloat)}";

    private static string ReadScalar(TfScalarEntity e)
        => $"guid={e.VGuid}|bytes={(e.VBytes is null ? "null" : Convert.ToHexString(e.VBytes))}|char={(int)e.VChar}"
            + $"|str={(e.VString is null ? "null" : Escape(e.VString))}|bool={e.VBool}|long={e.VLong}";

    private static string ReadEnum(TfEnumRow e)
        => $"i32={e.VInt32}|i64={e.VInt64}|flags={e.VFlags}";

    private static void WriteArtifact(string dialect, List<string> lines)
    {
        string? marker = TestEnvironment.FindFileUpwards("Directory.Build.props");
        string directory = marker is null
            ? Path.Combine(Path.GetTempPath(), "palorm-conformance")
            : Path.Combine(Path.GetDirectoryName(marker)!, "artifacts");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, $"type-fidelity-{dialect}.csv"), lines);
    }

    // ── TYPE-5：PG 全类型单行与批量，MySQL 单行对照 ──────────────

    private static AllTypesEntity AllTypesSample(DateTime dt, DateTimeOffset dto, byte bytesSeed) => new()
    {
        VInt = 42,
        VShort = 7,
        VByte = 255,
        VString = "端到端",
        VChar = 'Z',
        VBool = true,
        VDecimal = 12.34m,
        VDouble = 3.14159,
        VFloat = 2.5f,
        VDateTime = dt,
        VGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        VDto = dto,
        VDateOnly = new DateOnly(2026, 7, 18),
        VTimeOnly = new TimeOnly(10, 30, 45),
        VBytes = [bytesSeed, 0x01, 0xFF, 0x00, 0x41],
        VNullableInt = 99,
        VNullableTimeOnly = new TimeOnly(23, 59, 59),
        VNullableBytes = [9, 8, 7],
    };

    /// <summary>TYPE-5：PG 全类型单行往返（AllTypesEntity 覆盖 PALORM016 白名单 16 种类型）。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_AllTypesEntity_SingleRow_RoundTrip()
    {
        DateTime utc = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc);
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        // 历史库的 all_types_entities 可能是旧 DDL（timestamp 而非 timestamptz）：
        // CREATE TABLE IF NOT EXISTS 不改列型，先删表再迁移才能验证当前 DDL。
        await db.ExecuteAsync($"DROP TABLE IF EXISTS all_types_entities");
        await db.MigrateAsync();
        try
        {
            await db.ExecuteAsync($"DELETE FROM all_types_entities");
            await db.InsertAsync(AllTypesSample(utc, new DateTimeOffset(utc), 0x10));
            List<AllTypesEntity> rows = await db.GetAllAsync<AllTypesEntity>();
            await Assert.That(rows.Count).IsEqualTo(1);
            AssertAllTypes(rows[0], utc, new DateTimeOffset(utc), 0x10, pgSemantics: true);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM all_types_entities");
        }
    }

    /// <summary>TYPE-5：PG 全类型批量往返（BulkInsert 路径与逐行参数绑定是两套生成物）。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_AllTypesEntity_Bulk_RoundTrip()
    {
        DateTime utc = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc);
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        await db.ExecuteAsync($"DROP TABLE IF EXISTS all_types_entities");
        await db.MigrateAsync();
        try
        {
            await db.ExecuteAsync($"DELETE FROM all_types_entities");
            await db.BulkInsertAsync(
            [
                AllTypesSample(utc, new DateTimeOffset(utc), 0x10),
                AllTypesSample(utc.AddTicks(1), new DateTimeOffset(utc.AddTicks(1)), 0x20)
            ]);
            List<AllTypesEntity> rows = [.. (await db.GetAllAsync<AllTypesEntity>()).OrderBy(e => e.Id)];
            await Assert.That(rows.Count).IsEqualTo(2);
            // PG 的 TIMESTAMPTZ 是微秒精度：AddTicks(1) 截断到整微秒（TypeMatrix dt_ticks1 同口径）。
            AssertAllTypes(rows[0], utc, new DateTimeOffset(utc), 0x10, pgSemantics: true);
            AssertAllTypes(
                rows[1], TruncateToMicrosecond(utc.AddTicks(1)), new DateTimeOffset(TruncateToMicrosecond(utc.AddTicks(1))),
                0x20, pgSemantics: true);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM all_types_entities");
        }
    }

    /// <summary>TYPE-5：MySQL 单行对照（DATETIME(6) 微秒精度、offset 归一为 UTC）。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_AllTypesEntity_SingleRow_RoundTrip()
    {
        DateTime utc = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc);
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        await db.MigrateAsync();
        // 同 PG：历史 DDL 先删表，确保验证当前列型（DATETIME(6)）。
        await db.ExecuteAsync($"DROP TABLE IF EXISTS all_types_entities");
        await db.MigrateAsync();
        try
        {
            await db.ExecuteAsync($"DELETE FROM all_types_entities");
            await db.InsertAsync(AllTypesSample(utc, new DateTimeOffset(utc), 0x10));
            List<AllTypesEntity> rows = await db.GetAllAsync<AllTypesEntity>();
            await Assert.That(rows.Count).IsEqualTo(1);
            AssertAllTypes(rows[0], utc, new DateTimeOffset(utc), 0x10, pgSemantics: false);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM all_types_entities");
        }
    }

    private static void AssertAllTypes(
        AllTypesEntity read, DateTime expectedDt, DateTimeOffset expectedDto, byte bytesSeed, bool pgSemantics)
    {
        StringBuilder problems = new();
        void Check(bool condition, string message)
        {
            if (!condition) problems.Append(message).Append("; ");
        }

        Check(read.VInt == 42, $"VInt={read.VInt}");
        Check(read.VShort == 7, $"VShort={read.VShort}");
        Check(read.VByte == 255, $"VByte={read.VByte}");
        Check(string.Equals(read.VString, "端到端", StringComparison.Ordinal), $"VString={read.VString}");
        Check(read.VChar == 'Z', $"VChar={read.VChar}");
        Check(read.VBool, $"VBool={read.VBool}");
        Check(read.VDecimal == 12.34m, $"VDecimal={read.VDecimal}");
        Check(read.VDouble == 3.14159, $"VDouble={read.VDouble}");
        Check(read.VFloat == 2.5f, $"VFloat={read.VFloat}");
        Check(read.VDateTime.Ticks == expectedDt.Ticks, $"VDateTime={read.VDateTime:O}");
        Check(
            read.VDateTime.Kind == (pgSemantics ? DateTimeKind.Utc : DateTimeKind.Unspecified),
            $"VDateTime.Kind={read.VDateTime.Kind}");
        Check(read.VGuid == Guid.Parse("11111111-2222-3333-4444-555555555555"), $"VGuid={read.VGuid}");
        Check(read.VDto.UtcDateTime.Ticks == expectedDto.UtcDateTime.Ticks, $"VDto={read.VDto:O}");
        Check(read.VDto.Offset == TimeSpan.Zero, $"VDto.Offset={read.VDto.Offset}");
        Check(read.VDateOnly == new DateOnly(2026, 7, 18), $"VDateOnly={read.VDateOnly:O}");
        Check(read.VTimeOnly == new TimeOnly(10, 30, 45), $"VTimeOnly={read.VTimeOnly:O}");
        Check(read.VBytes.AsSpan().SequenceEqual((byte[])[bytesSeed, 0x01, 0xFF, 0x00, 0x41]), "VBytes");
        Check(read.VNullableInt == 99, $"VNullableInt={read.VNullableInt}");
        Check(read.VNullableTimeOnly == new TimeOnly(23, 59, 59), $"VNullableTimeOnly={read.VNullableTimeOnly:O}");
        Check(read.VNullableBytes!.AsSpan().SequenceEqual((byte[])[9, 8, 7]), "VNullableBytes");
        if (problems.Length > 0)
        {
            throw new InvalidOperationException($"AllTypes 往返不符：{problems}");
        }
    }

    // ── 用例输入 ─────────────────────────────────────────────

    private sealed record TemporalCase(
        string Name,
        DateTime Dt,
        DateTimeOffset Dto,
        DateOnly Date,
        TimeOnly Time);

    /// <summary>非目标列一律填合法基准值，保证一次只测一个边界。</summary>
    private static readonly DateTime BaseUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset BaseDto = new(BaseUtc);
    private static readonly DateOnly BaseDate = new(2026, 1, 1);
    private static readonly TimeOnly BaseTime = new(0, 0, 0);

    private static TemporalCase Temporal(string name, DateTime? dt = null, DateTimeOffset? dto = null,
        DateOnly? date = null, TimeOnly? time = null)
        => new(name, dt ?? BaseUtc, dto ?? BaseDto, date ?? BaseDate, time ?? BaseTime);

    private static IEnumerable<TemporalCase> TemporalCases()
    {
        yield return Temporal("dt_ticks1", dt: BaseUtc.AddTicks(1));
        yield return Temporal("dt_ticks1234567", dt: BaseUtc.AddTicks(1_234_567));
        yield return Temporal("dt_minvalue", dt: DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
        yield return Temporal("dt_maxvalue", dt: DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
        yield return Temporal("dt_local", dt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local));
        yield return Temporal("dt_unspecified", dt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        yield return Temporal("dt_dst_before", dt: DstBoundary(beforeTransition: true));
        yield return Temporal("dt_dst_after", dt: DstBoundary(beforeTransition: false));
        yield return Temporal("dto_plus14", dto: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(14)));
        yield return Temporal("dto_minus8", dto: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-8)));
        yield return Temporal("dto_dst_before", dto: ToDto(DstBoundary(beforeTransition: true)));
        yield return Temporal("dto_dst_after", dto: ToDto(DstBoundary(beforeTransition: false)));
        yield return Temporal("dto_cross_day", dto: new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.FromHours(-8)));
        yield return Temporal("date_min", date: new DateOnly(1, 1, 1));
        yield return Temporal("date_max", date: new DateOnly(9999, 12, 31));
        yield return Temporal("time_min", time: new TimeOnly(0, 0, 0));
        yield return Temporal("time_max", time: new TimeOnly(23, 59, 59, 999).Add(TimeSpan.FromTicks(9_999)));
        yield return Temporal("time_millis", time: new TimeOnly(12, 34, 56, 789));
        yield return Temporal("time_micros", time: new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(1234)));
    }

    /// <summary>固定自定义时区的 DST 边界（不依赖主机时区）：UTC-5，3 月第二个周日 02:00 跳到 03:00。</summary>
    private static DateTime DstBoundary(bool beforeTransition)
    {
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone(
            "TfFixedZone", TimeSpan.FromHours(-5), "TfFixedZone", "TfFixedZone", "TfFixedZone",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0, DateTimeKind.Unspecified), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0, DateTimeKind.Unspecified), 11, 1, DayOfWeek.Sunday))]);
        DateTime wall = new(2026, 3, 8, 1, 30, 0, DateTimeKind.Unspecified);
        if (!beforeTransition) wall = wall.AddHours(2);
        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }

    private static DateTimeOffset ToDto(DateTime utc) => new(utc, TimeSpan.Zero);

    private sealed record NumericCase(string Name, decimal Decimal, double Double, float Float);

    private static NumericCase Numeric(string name, decimal? dec = null, double? dbl = null, float? flt = null)
        => new(name, dec ?? 1.5m, dbl ?? 1.5, flt ?? 1.5f);

    private static IEnumerable<NumericCase> NumericCases()
    {
        yield return Numeric("dec_scale6", dec: 123456789012.123456m);
        yield return Numeric("dec_scale7", dec: 0.1234567m);
        yield return Numeric("dec_18digits", dec: 999999999999.999999m);
        yield return Numeric("dec_28digits", dec: 1234567890123456789012.123456m);
        yield return Numeric("dec_max", dec: decimal.MaxValue);
        yield return Numeric("dec_min", dec: decimal.MinValue);
        yield return Numeric("dec_neg_round", dec: -0.1234565m);
        yield return Numeric("dbl_nan", dbl: double.NaN);
        yield return Numeric("dbl_posinf", dbl: double.PositiveInfinity);
        yield return Numeric("dbl_neginf", dbl: double.NegativeInfinity);
        yield return Numeric("dbl_negzero", dbl: -0.0);
        yield return Numeric("dbl_epsilon", dbl: double.Epsilon);
        yield return Numeric("dbl_max", dbl: double.MaxValue);
        yield return Numeric("dbl_min", dbl: double.MinValue);
        yield return Numeric("flt_nan", flt: float.NaN);
        yield return Numeric("flt_epsilon", flt: float.Epsilon);
        yield return Numeric("flt_max", flt: float.MaxValue);
        yield return Numeric("flt_min", flt: float.MinValue);
    }

    private sealed record ScalarCase(
        string Name, Guid Guid, byte[]? Bytes, char Char, string? Str, bool Bool, long Long);

    /// <summary>默认参数无法区分"未指定"与"显式 null"，空值用例走 <c>nullBytes</c>/<c>nullStr</c> 开关。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
        "S107:Methods should not have too many parameters",
        Justification = "表驱动用例构造器，参数与实体列一一对应；拆成 builder 只会增加噪声")]
    private static ScalarCase Scalar(string name, Guid? guid = null, byte[]? bytes = null, char? ch = null,
        string? str = null, bool? flag = null, long? l = null,
        bool nullBytes = false, bool nullStr = false)
        => new(name, guid ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
            nullBytes ? null : bytes ?? [1, 2, 3],
            ch ?? 'A', nullStr ? null : str ?? "base", flag ?? true, l ?? 42);

    private static IEnumerable<ScalarCase> ScalarCases()
    {
        yield return Scalar("guid_empty", guid: Guid.Empty);
        yield return Scalar("guid_ff", guid: Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"));
        yield return Scalar("guid_v4", guid: Guid.Parse("9f1c2d3e-4a5b-4c6d-8e7f-001122334455"));
        yield return Scalar("bytes_empty", bytes: []);
        yield return Scalar("bytes_null", nullBytes: true);
        yield return Scalar("bytes_zero", bytes: [0x00, 0x00, 0x00]);
        yield return Scalar("bytes_1mb", bytes: Enumerable.Repeat((byte)0xA5, 1024 * 1024).ToArray());
        yield return Scalar("char_nul", ch: '\0');
        yield return Scalar("char_cjk", ch: '中');
        yield return Scalar("str_null", nullStr: true);
        yield return Scalar("str_empty", str: "");
        yield return Scalar("str_emoji", str: "\U0001F600\U0001F642\u200D\u2194\uFE0F");
        yield return Scalar("str_zwj", str: "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466");
        yield return Scalar("str_cjk", str: "中文字符测试");
        yield return Scalar("str_combining", str: "e\u0301a\u0308");
        yield return Scalar("str_lone_surrogate", str: "\uD800");
        yield return Scalar("str_trailing_spaces", str: "abc   ");
        yield return Scalar("str_long100k", str: new string('x', 100_000));
        yield return Scalar("long_min", l: long.MinValue);
        yield return Scalar("long_max", l: long.MaxValue);
    }

    private sealed record EnumCase(string Name, TfSignedValue Int32, TfLongValue Int64, TfFlagsValue Flags);

    private static EnumCase Enum(string name, TfSignedValue? i32 = null, TfLongValue? i64 = null,
        TfFlagsValue? flags = null)
        => new(name, i32 ?? TfSignedValue.Zero, i64 ?? TfLongValue.BelowZero, flags ?? TfFlagsValue.None);

    private static IEnumerable<EnumCase> EnumCases()
    {
        yield return Enum("enum_negative_i32", i32: TfSignedValue.Negative);
        yield return Enum("enum_long_huge", i64: TfLongValue.Huge);
        yield return Enum("enum_flags_combo", flags: TfFlagsValue.A | TfFlagsValue.C);
    }

    private static TfTemporalEntity Create(TemporalCase c) => new()
    {
        Id = 1, VDateTime = c.Dt, VDto = c.Dto, VDateOnly = c.Date, VTimeOnly = c.Time
    };

    private static TfNumericEntity Create(NumericCase c) => new()
    {
        Id = 1, VDecimal = c.Decimal, VDouble = c.Double, VFloat = c.Float
    };

    private static TfScalarEntity Create(ScalarCase c) => new()
    {
        Id = 1, VGuid = c.Guid, VBytes = c.Bytes, VChar = c.Char, VString = c.Str, VBool = c.Bool, VLong = c.Long
    };

    private static TfEnumRow Create(EnumCase c) => new()
    {
        Id = 1, VInt32 = c.Int32, VInt64 = c.Int64, VFlags = c.Flags
    };
}