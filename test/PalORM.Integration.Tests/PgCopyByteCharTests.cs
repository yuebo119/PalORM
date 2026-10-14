using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>R4（2026-10-12 copyWriter 扩面）：含 byte/char 列的实体经 PG Binary COPY 直写
/// 的真库往返锁定——byte→WriteInt16（SMALLINT）、char→WriteString（文本），含 nullable
/// char 的 null 分支。此形态此前整体回退参数池路径，扩面后走定型写入（装箱归零）。
/// 串行槽：与全库真库测试共享 "ExtBulkTable"（建表竞态，同 BulkUpdateTenantRoutingTests）。</summary>
[NotInParallel("ExtBulkTable")]
public sealed class PgCopyByteCharTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task Pg_BulkInsert_ByteCharColumns_RoundTripViaCopy()
    {
        DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS copy_widen_rt");
            await db.ExecuteAsync(
                $"CREATE TABLE copy_widen_rt (\"Id\" bigint PRIMARY KEY, \"B\" smallint NOT NULL, \"C\" text NOT NULL, \"N\" text, \"S\" text NOT NULL)");

            var rows = new List<CopyWidenRtEntity>
            {
                new() { Id = 1, B = 0, C = 'x', N = 'y', S = "a" },
                new() { Id = 2, B = 255, C = 'Z', N = null, S = "b" },
                new() { Id = 3, B = 128, C = '中', N = '文', S = "c" },
            };
            long affected = await db.BulkInsertAsync(rows);
            await Assert.That(affected).IsEqualTo(3L);

            List<CopyWidenRtEntity> back = await db.From<CopyWidenRtEntity>().OrderBy(x => x.Id).ToListAsync();
            await Assert.That(back.Count).IsEqualTo(3);
            await Assert.That(back[0].B).IsEqualTo((byte)0);
            await Assert.That(back[0].C).IsEqualTo('x');
            await Assert.That(back[0].N).IsEqualTo('y');
            await Assert.That(back[1].B).IsEqualTo((byte)255);
            await Assert.That(back[1].C).IsEqualTo('Z');
            await Assert.That(back[1].N).IsNull();
            await Assert.That(back[2].B).IsEqualTo((byte)128);
            await Assert.That(back[2].C).IsEqualTo('中');
            await Assert.That(back[2].N).IsEqualTo('文');
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS copy_widen_rt");
            await db.DisposeAsync();
        }
    }
}

[Table("copy_widen_rt")]
internal sealed partial class CopyWidenRtEntity
{
    [Key(AutoIncrement = false)] [Column("Id")] public long Id { get; set; }
    [Column("B")] public byte B { get; set; }
    [Column("C")] public char C { get; set; }
    [Column("N")] public char? N { get; set; }
    [Column("S")] public string S { get; set; } = "";
}
