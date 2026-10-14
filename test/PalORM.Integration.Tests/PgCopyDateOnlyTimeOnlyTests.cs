using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>P2-2（2026-10-12 全量重读）：copyWriter 扩面探针 + 真库往返锁定——
/// DateOnly/TimeOnly 列经 PG Binary COPY 定型直写（Write&lt;DateOnly&gt;(NpgsqlDbType.Date) /
/// Write&lt;TimeOnly&gt;(NpgsqlDbType.Time)），含 nullable 的 null 分支。此形态此前整体回退
/// 参数池路径（装箱），扩面后定型写入。本测试即探针：Npgsql 若拒绝该定型形态会在此转红。
/// 串行槽：ExtBulkTable（建表竞态，B63 家族）。</summary>
[NotInParallel("ExtBulkTable")]
public sealed class PgCopyDateOnlyTimeOnlyTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task Pg_BulkInsert_DateOnlyTimeOnlyColumns_RoundTripViaCopy()
    {
        DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS copy_dot_rt");
            await db.ExecuteAsync(
                $"CREATE TABLE copy_dot_rt (\"Id\" bigint PRIMARY KEY, \"D\" date NOT NULL, \"T\" time NOT NULL, \"Nd\" date, \"Nt\" time)");

            var rows = new List<CopyDotRtEntity>
            {
                new() { Id = 1, D = new DateOnly(2024, 2, 29), T = new TimeOnly(23, 59, 59, 999), Nd = new DateOnly(1970, 1, 1), Nt = new TimeOnly(0, 0, 0) },
                new() { Id = 2, D = new DateOnly(1, 1, 1), T = new TimeOnly(0, 0, 1), Nd = null, Nt = null },
                new() { Id = 3, D = new DateOnly(9999, 12, 31), T = new TimeOnly(13, 30, 45), Nd = new DateOnly(2026, 10, 12), Nt = new TimeOnly(6, 5, 4) },
            };
            long affected = await db.BulkInsertAsync(rows);
            await Assert.That(affected).IsEqualTo(3L);

            List<CopyDotRtEntity> back = await db.From<CopyDotRtEntity>().OrderBy(x => x.Id).ToListAsync();
            await Assert.That(back.Count).IsEqualTo(3);
            await Assert.That(back[0].D).IsEqualTo(new DateOnly(2024, 2, 29));
            await Assert.That(back[0].T).IsEqualTo(new TimeOnly(23, 59, 59, 999));
            await Assert.That(back[0].Nd).IsEqualTo(new DateOnly(1970, 1, 1));
            await Assert.That(back[0].Nt).IsEqualTo(new TimeOnly(0, 0, 0));
            await Assert.That(back[1].Nd).IsNull();
            await Assert.That(back[1].Nt).IsNull();
            await Assert.That(back[2].D).IsEqualTo(new DateOnly(9999, 12, 31));
            await Assert.That(back[2].T).IsEqualTo(new TimeOnly(13, 30, 45));
            await Assert.That(back[2].Nd).IsEqualTo(new DateOnly(2026, 10, 12));
            await Assert.That(back[2].Nt).IsEqualTo(new TimeOnly(6, 5, 4));
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS copy_dot_rt");
            await db.DisposeAsync();
        }
    }
}

[Table("copy_dot_rt")]
internal sealed partial class CopyDotRtEntity
{
    [Key(AutoIncrement = false)] [Column("Id")] public long Id { get; set; }
    [Column("D")] public DateOnly D { get; set; }
    [Column("T")] public TimeOnly T { get; set; }
    [Column("Nd")] public DateOnly? Nd { get; set; }
    [Column("Nt")] public TimeOnly? Nt { get; set; }
}
