using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>2026-10-22 终审 P1-3：OrderBy/GroupBy/WhereIn 在 With(CTE) 之前调用时，
/// 列引用会固化为实体表名；构建时必须按当前 FROM 源（CTE 名）重限定，否则 SQL 引用
/// 不在 FROM 中的表。用户手写 Where/Raw/Join 不参与重限定。</summary>
public sealed class CteColumnQualificationTests
{
    [Test]
    public async Task OrderByAndWhereInBeforeWith_UseCteQualification()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:"
        });
        await db.ExecuteAsync(
            $"CREATE TABLE where_or_probe (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Qty INTEGER NOT NULL)");
        await db.ExecuteAsync($"INSERT INTO where_or_probe (Id, Name, Qty) VALUES (1, 'a', 5)");
        await db.ExecuteAsync($"INSERT INTO where_or_probe (Id, Name, Qty) VALUES (2, 'b', 50)");
        await db.ExecuteAsync($"INSERT INTO where_or_probe (Id, Name, Qty) VALUES (3, 'c', 500)");

#pragma warning disable PALORM005 // ToSql 纯构建不打数据库
        string sql = db.From<WhereOrProbeRow>()
            .WhereIn(x => x.Id, new long[] { 1, 2 })
            .OrderBy(x => x.Id)
            .With("c", $"SELECT * FROM where_or_probe")
            .ToSql();
#pragma warning restore PALORM005

        await Assert.That(sql).Contains("ORDER BY \"c\".\"Id\"");
        await Assert.That(sql).Contains("\"c\".\"Id\" IN");
        await Assert.That(sql).DoesNotContain("ORDER BY \"where_or_probe\"");

        var rows = await db.From<WhereOrProbeRow>()
            .WhereIn(x => x.Id, new long[] { 1, 2 })
            .OrderBy(x => x.Id)
            .With("c", $"SELECT * FROM where_or_probe")
            .ToListAsync();

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].Id).IsEqualTo(1L);
        await Assert.That(rows[1].Id).IsEqualTo(2L);

#pragma warning disable PALORM005
        string groupSql = db.From<WhereOrProbeRow>()
            .GroupBy(x => x.Qty)
            .With("c", $"SELECT * FROM where_or_probe")
            .ToSql();
#pragma warning restore PALORM005
        await Assert.That(groupSql).Contains("GROUP BY \"c\".\"Qty\"");
    }
}
