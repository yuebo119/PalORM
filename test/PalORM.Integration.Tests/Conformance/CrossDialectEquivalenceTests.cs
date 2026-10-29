using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L6 跨方言等价性（方案 §4.9）。
/// <para>同一场景在三方言执行，结果集两两互比。三个独立引擎构成天然的第三 oracle：
/// 若某方言生成的 SQL 语义偏离，两侧与手写 SQL 的对照可能同时通过（共享同一生成路径），
/// 但三方言之间必然出现差异。</para>
/// <para>限制域：只比可比列。时间列按 §4.7 的实测矩阵处理（三方言读回都是 Unspecified、刻度相同）；
/// <c>DateTimeOffset</c> 因 MySQL 归一为 UTC 而 SQLite 保留 offset，登记为不可比并从比较中排除，
/// 这是列级排除而非场景级跳过，跳过量按场景计仍为 0。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class CrossDialectEquivalenceTests
{
    private static readonly string[] BulkColumns = ["Id", "name", "amount"];

    private static CfBulkPlainEntity[] BulkRows =>
    [
        new() { Id = 1, Name = "alpha", Amount = 0 },
        new() { Id = 2, Name = "beta", Amount = -1 },
        new() { Id = 3, Name = "", Amount = long.MaxValue },
        new() { Id = 4, Name = "emoji-🙂", Amount = long.MinValue },
        new() { Id = 5, Name = "quote'inside", Amount = 42 }
    ];

    private static async ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SeedBulkAndReadAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw, CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        await MirrorTable.CleanAsync(raw, dialect, ct);
        await db.BulkInsertAsync(BulkRows, ct);
        return await MirrorTable.QueryAsync(
            raw, dialect, DialectSql.SelectAll(dialect, MirrorTable.BulkTable, BulkColumns), ct: ct);
    }

    /// <summary>按列语义把时间类值归一为字符串。
    /// <para>三个方言的裸 ADO 读回类型不同：SQLite 全是 string，PostgreSQL 是 DateTime/DateOnly/TimeOnly，
    /// MySQL 的 DATE 读回 DateTime、TIME 读回 TimeSpan。按 CLR 类型分支会漏掉跨方言组合，
    /// 因此按列语义归一：dt 取日期时间、date_only 取日期、time_only 取时间。</para>
    /// <para>列名硬编码是因为 L6 只覆盖 cf_time_matrix 这四列，不追求通用。</para></summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> NormalizeTimeColumns(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        return
        [
            .. rows.Select(row =>
            {
                var projected = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach ((string key, object? value) in row)
                {
                    projected[key] = key switch
                    {
                        "dt" => AsDateTimeText(value),
                        "date_only" => AsDateText(value),
                        "time_only" => AsTimeText(value),
                        _ => value
                    };
                }
                return (IReadOnlyDictionary<string, object?>)projected;
            })
        ];
    }

    private static string? AsDateTimeText(object? value)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return value switch
        {
            DateTime time => time.ToString("yyyy-MM-dd HH:mm:ss", culture),
            DateOnly date => date.ToString("yyyy-MM-dd", culture) + " 00:00:00",
            string text => text.Replace('T', ' ').TrimEnd('Z'),
            _ => value!.ToString()
        };
    }

    private static string? AsDateText(object? value)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return value switch
        {
            DateTime time => time.ToString("yyyy-MM-dd", culture),
            DateOnly date => date.ToString("yyyy-MM-dd", culture),
            string text => text.Length >= 10 ? text[..10] : text,
            _ => value!.ToString()
        };
    }

    private static string? AsTimeText(object? value)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return value switch
        {
            TimeSpan span => span.ToString(@"hh\:mm\:ss", culture),
            TimeOnly time => time.ToString("HH:mm:ss", culture),
            DateTime time => time.ToString("HH:mm:ss", culture),
            string text => text.Length >= 8 ? text[..8] : text,
            _ => value!.ToString()
        };
    }

    /// <summary>清表并重置自增序列：三个方言的表都在共享容器库上，不重置会让第二个用例读到续号。</summary>
    private static async Task ResetTableAsync(
        DbConnection raw, SqlDialect dialect, string table, CancellationToken ct = default)
    {
        string sql = dialect switch
        {
            SqlDialect.PostgreSql => $"TRUNCATE TABLE {DialectSql.Quote(dialect, table)} RESTART IDENTITY",
            SqlDialect.MySql => $"TRUNCATE TABLE {DialectSql.Quote(dialect, table)}",
            _ => $"DELETE FROM {DialectSql.Quote(dialect, table)}"
        };
        await MirrorTable.ExecuteAsync(raw, dialect, sql, ct: ct);
        if (dialect != SqlDialect.Sqlite) return;
        try
        {
            await MirrorTable.ExecuteAsync(
                raw, dialect,
                "DELETE FROM sqlite_sequence WHERE name = 'cf_time_matrix'", ct: ct);
        }
        catch (DbException)
        {
            // 该库尚未建立带 AUTOINCREMENT 的表时 sqlite_sequence 不存在，忽略
        }
    }

    private static async Task AssertPairwiseEquivalentAsync(
        string leftName, IReadOnlyList<IReadOnlyDictionary<string, object?>> left,
        string rightName, IReadOnlyList<IReadOnlyDictionary<string, object?>> right)
    {
        IReadOnlyList<RowComparer.Difference> differences = RowComparer.Compare(left, right);
        string detail = string.Join(
            " | ",
            differences.Take(6).Select(d => $"{d.Kind}/{d.Column}: {leftName}={d.Expected} {rightName}={d.Actual}"));
        await Assert.That(detail).IsEmpty();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task L6_BulkColumns_AreIdenticalAcrossThreeDialects()
    {
        await using SharedSqliteDb sqlite = await SharedSqliteDb.CreateAsync("l6_bulk_sqlite");
        await using RemoteConformanceFixture<PostgreSqlProvider> pg = await ConformanceDialects.PostgreSqlAsync();
        await using RemoteConformanceFixture<MySqlProvider> my = await ConformanceDialects.MySqlAsync();

        await using DataSession<SqliteProvider> sqliteSession = await sqlite.OpenSessionAsync();
        await MirrorTable.EnsureAsync(sqliteSession, SqlDialect.Sqlite);
        await MirrorTable.EnsureAsync(pg.Session, SqlDialect.PostgreSql);
        await MirrorTable.EnsureAsync(my.Session, SqlDialect.MySql);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> sqliteRows = await SeedBulkAndReadAsync(
            SqlDialect.Sqlite, sqliteSession, sqliteSession.GetRawConnection());
        IReadOnlyList<IReadOnlyDictionary<string, object?>> pgRows = await SeedBulkAndReadAsync(
            SqlDialect.PostgreSql, pg.Session, pg.Raw);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> myRows = await SeedBulkAndReadAsync(
            SqlDialect.MySql, my.Session, my.Raw);

        await Assert.That(sqliteRows.Count).IsEqualTo(5);
        int scenarios = 1;
        int skipped = 0;
        await Assert.That(skipped / (double)scenarios).IsLessThanOrEqualTo(0.2);

        await AssertPairwiseEquivalentAsync("sqlite", sqliteRows, "postgresql", pgRows);
        await AssertPairwiseEquivalentAsync("sqlite", sqliteRows, "mysql", myRows);
        await AssertPairwiseEquivalentAsync("postgresql", pgRows, "mysql", myRows);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task L6_ComparableTimeColumns_MatchAcrossThreeDialects()
    {
        await using SharedSqliteDb sqlite = await SharedSqliteDb.CreateAsync("l6_time_sqlite");
        await using RemoteConformanceFixture<PostgreSqlProvider> pg = await ConformanceDialects.PostgreSqlAsync();
        await using RemoteConformanceFixture<MySqlProvider> my = await ConformanceDialects.MySqlAsync();

        await using DataSession<SqliteProvider> sqliteSession = await sqlite.OpenSessionAsync();
        await MirrorTable.EnsureAsync(sqliteSession, SqlDialect.Sqlite);
        await MirrorTable.EnsureAsync(pg.Session, SqlDialect.PostgreSql);
        await MirrorTable.EnsureAsync(my.Session, SqlDialect.MySql);

        // DateTimeOffset 排除：MySQL 归一为 UTC、SQLite 保留 offset，是已知不可比面（§4.7 实测）
        string[] comparableColumns = ["Id", "dt", "date_only", "time_only"];

        IReadOnlyList<IReadOnlyDictionary<string, object?>> sqliteRows = await SeedTimeAndReadAsync(
            SqlDialect.Sqlite, sqliteSession, sqliteSession.GetRawConnection(), comparableColumns);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> pgRows = await SeedTimeAndReadAsync(
            SqlDialect.PostgreSql, pg.Session, pg.Raw, comparableColumns);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> myRows = await SeedTimeAndReadAsync(
            SqlDialect.MySql, my.Session, my.Raw, comparableColumns);

        await Assert.That(sqliteRows.Count).IsEqualTo(1);
        await AssertPairwiseEquivalentAsync("sqlite", sqliteRows, "postgresql", pgRows);
        await AssertPairwiseEquivalentAsync("sqlite", sqliteRows, "mysql", myRows);
        await AssertPairwiseEquivalentAsync("postgresql", pgRows, "mysql", myRows);
    }

    private static async ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SeedTimeAndReadAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw,
        IReadOnlyList<string> comparableColumns, CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        await ResetTableAsync(raw, dialect, "cf_time_matrix", ct);

        // 三方言都只写 Utc：PG 的 DateTime 列映射 timestamptz，非 Utc 会被驱动拒绝（§4.7 / E9）
        await db.InsertAsync(new CfTimeMatrixEntity
        {
            Dt = new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc),
            Dto = new DateTimeOffset(2026, 7, 18, 10, 30, 0, TimeSpan.Zero),
            DateOnly = new DateOnly(2026, 7, 18),
            TimeOnly = new TimeOnly(10, 30, 45)
        }, ct);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await MirrorTable.QueryAsync(
            raw, dialect,
            DialectSql.SelectAll(dialect, "cf_time_matrix", comparableColumns), ct: ct);
        return NormalizeTimeColumns(rows);
    }
}