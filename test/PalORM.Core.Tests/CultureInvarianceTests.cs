using System.Globalization;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

// P1-C（方案 §6.6 / COMPAT-2）：文化切片。
// de-DE / tr-TR / fr-FR 下跑 SQL 生成、decimal 参数、日期解析、字符串比较。
// [NotInParallel]（无键）= 不与任何测试并行，避免 CurrentCulture 全局态污染；
// finally 恢复原文化并断言恢复成功。

[Table("culture_entity")]
public partial class CultureEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("amount")] public decimal Amount { get; set; }
    [Column("when_utc")] public DateTime WhenUtc { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

[NotInParallel]
public sealed class CultureInvarianceTests
{
    private static readonly string[] Cultures = ["de-DE", "tr-TR", "fr-FR"];

    [Test]
    public async Task SqlGeneration_Decimal_Date_String_AreCultureInvariant()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        List<string> failures = [];

        try
        {
            foreach (string name in Cultures)
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                try
                {
                    await RunSliceAsync(name, failures);
                }
                finally
                {
                    CultureInfo.CurrentCulture = original;
                    CultureInfo.CurrentUICulture = originalUi;
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }

        await Assert.That(string.Join("\n", failures)).IsEmpty();
        // 恢复断言：文化是国内进程级状态，恢复失败会污染同进程后续用例
        await Assert.That(CultureInfo.CurrentCulture.Name).IsEqualTo(original.Name);
        await Assert.That(CultureInfo.CurrentUICulture.Name).IsEqualTo(originalUi.Name);
    }

    private static async Task RunSliceAsync(string cultureName, List<string> failures)
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        await db.MigrateAsync();
        await db.ExecuteAsync($"DELETE FROM culture_entity");

        // ── 1. SQL 生成：数值进参数位，不随文化出现逗号小数点 / 千分位 ──
        string sql = db.From<CultureEntity>()
            .Where($"amount > {1234.56m}")
            .ToSql();
        if (sql.Contains("1234,56", StringComparison.Ordinal)
            || sql.Contains("1.234,56", StringComparison.Ordinal)
            || sql.Contains("1,234.56", StringComparison.Ordinal))
        {
            failures.Add($"{cultureName}: SQL 含文化格式化数值（{sql}）");
        }
        if (!sql.Contains("@p0", StringComparison.Ordinal))
        {
            failures.Add($"{cultureName}: 数值未进入参数位（{sql}）");
        }

        // ── 2. decimal 参数往返 ──
        var row = new CultureEntity
        {
            Id = 1,
            Amount = 1234.56m,
            WhenUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Name = "Istanbul"
        };
        await db.InsertAsync(row);
        CultureEntity read = (await db.GetAsync<CultureEntity>(1L))!;
        if (read.Amount != 1234.56m)
        {
            failures.Add($"{cultureName}: decimal 往返 1234.56 → {read.Amount}");
        }

        // ── 3. 日期解析：保守往返到 ticks，不受日历/时区文化影响 ──
        if (read.WhenUtc.Ticks != row.WhenUtc.Ticks)
        {
            failures.Add($"{cultureName}: 日期往返 ticks {row.WhenUtc.Ticks} → {read.WhenUtc.Ticks}");
        }

        // ── 4. 字符串比较：tr-TR 的无点 i 不得导致大小写折叠 / 等值混淆 ──
        await db.InsertAsync(new CultureEntity
        {
            Id = 2, Amount = 0m, WhenUtc = row.WhenUtc, Name = "istanbul"
        });
        await db.InsertAsync(new CultureEntity
        {
            Id = 3, Amount = 0m, WhenUtc = row.WhenUtc, Name = "İstanbul"
        });
        List<CultureEntity> matches = await db.From<CultureEntity>()
            .Where($"name = {"Istanbul"}").ToListAsync();
        if (matches.Count != 1 || matches[0].Id != 1L)
        {
            failures.Add(
                $"{cultureName}: 字符串等值受文化折叠影响，命中 {matches.Count} 行（应仅 Id=1）");
        }
        if (!string.Equals((await db.GetAsync<CultureEntity>(3L))!.Name, "İstanbul", StringComparison.Ordinal))
        {
            failures.Add($"{cultureName}: 非 ASCII 名称未按序数往返");
        }
    }
}