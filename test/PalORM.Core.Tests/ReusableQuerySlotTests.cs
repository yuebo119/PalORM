using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>A9（2026-10-01 全 API 逐项轮）：读查询命令复用槽（PL-2 泛化）的行为锁定——
/// 晋升（同 SQL 第 3 次）、复用命中的参数重挂正确性（改值后返回新结果）、
/// 交替形状安全性、并行读作用域禁用、会话 Dispose 释放。
/// <para>循环内的 PALORM005（N+1 检测）以局部 pragma 精确抑制：被测行为正是
/// "同会话循环执行同形状查询"的复用本身（非反模式；对齐 GetByKeyCommandReuseTests 先例）。</para></summary>
public sealed class ReusableQuerySlotTests
{
    private static DataSession<SqliteProvider> NewSession(SqliteConnection conn)
        => new(conn, new DbOptions { ConnectionString = conn.ConnectionString },
            [], null);

    private static async Task<SqliteConnection> NewConnWithRowsAsync()
    {
        var conn = new SqliteConnection($"Data Source=rqs_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        try
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE rqs_rows(id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL);
                INSERT INTO rqs_rows(id, name, qty) VALUES (1, 'a', 5), (2, 'b', 50), (3, 'c', 500);
                """;
            await cmd.ExecuteNonQueryAsync();
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    [Test]
    public async Task SameShape_ThreeRuns_PromotesCommandIntoSlot()
    {
        await using var conn = await NewConnWithRowsAsync();
        await using var session = NewSession(conn);

        for (int run = 0; run < 3; run++)
        {
#pragma warning disable PALORM005
            _ = await session.From<RqsProbeRow>().Where($"qty > {20}").ToListAsync();
#pragma warning restore PALORM005
        }

        await Assert.That(session._querySlot!.Command).IsNotNull();
        await Assert.That(session._querySlot!.ServedOps)
            .IsGreaterThanOrEqualTo(ReusableQuerySlot.PromotionThreshold);
    }

    [Test]
    public async Task SameShape_DifferentValues_ReusedCommandReturnsFilteredResults()
    {
        // 参数重挂正确性（最重要）：第 2/3 次走复用命令，参数值变化后结果必须随参数变——
        // 若复用时未清/未重挂参数，将返回首次的行集（静默错）。
        await using var conn = await NewConnWithRowsAsync();
        await using var session = NewSession(conn);

        List<RqsProbeRow> first = await session.From<RqsProbeRow>().Where($"qty > {20}").ToListAsync();
        await Assert.That(first.Count).IsEqualTo(2);
        List<RqsProbeRow> second = await session.From<RqsProbeRow>().Where($"qty > {20}").ToListAsync();
        await Assert.That(second.Count).IsEqualTo(2);
        List<RqsProbeRow> third = await session.From<RqsProbeRow>().Where($"qty > {100}").ToListAsync();
        await Assert.That(third.Count).IsEqualTo(1);
        await Assert.That(third[0].Name).IsEqualTo("c");
    }

    [Test]
    public async Task AlternatingShapes_ResultsStayCorrect()
    {
        await using var conn = await NewConnWithRowsAsync();
        await using var session = NewSession(conn);

        // A/B 形状交替：单槽抖动场景——每次都可能是"未晋升→新建+替换"，结果必须恒正确
        for (int round = 0; round < 4; round++)
        {
#pragma warning disable PALORM005
            List<RqsProbeRow> byQty = await session.From<RqsProbeRow>()
                .Where($"qty > {20}").OrderBy(r => r.Id).ToListAsync();
#pragma warning restore PALORM005

            await Assert.That(byQty.Count).IsEqualTo(2);

#pragma warning disable PALORM005
            RqsProbeRow? one = await session.From<RqsProbeRow>()
                .Where($"id = {1}").FirstOrDefaultAsync();
#pragma warning restore PALORM005
            await Assert.That(one).IsNotNull();
            await Assert.That(one!.Name).IsEqualTo("a");
        }
    }

    [Test]
    public async Task ParallelReadScope_DisablesPromotion()
    {
        await using var conn = await NewConnWithRowsAsync();
        await using var session = NewSession(conn);

        await using (session.ForParallelReads())
        {
            for (int run = 0; run < 3; run++)
            {
#pragma warning disable PALORM005
                _ = await session.From<RqsProbeRow>().Where($"qty > {20}").ToListAsync();
#pragma warning restore PALORM005
            }
            // 并行读作用域内并发 reader 不能共用命令 → 不晋升（槽保持空）
            await Assert.That(session._querySlot!.Command).IsNull();
        }

        // 作用域退出后恢复晋升
        for (int run = 0; run < 3; run++)
        {
#pragma warning disable PALORM005
            _ = await session.From<RqsProbeRow>().Where($"qty > {20}").ToListAsync();
#pragma warning restore PALORM005
        }
        await Assert.That(session._querySlot!.Command).IsNotNull();
    }

    [Test]
    public async Task SessionDispose_ReleasesPromotedCommand()
    {
        var conn = await NewConnWithRowsAsync();
        var session = NewSession(conn);

        for (int run = 0; run < 3; run++)
        {
#pragma warning disable PALORM005
            _ = await session.From<RqsProbeRow>().Where($"qty > {20}").ToListAsync();
#pragma warning restore PALORM005
        }
        await Assert.That(session._querySlot!.Command).IsNotNull();

        await session.DisposeAsync();
        // DisposeReusableQueryCommandAsync 释放命令并清槽引用（幂等）
        await Assert.That(session._querySlot!.Command).IsNull();
        await conn.DisposeAsync();
    }
}

[Table("rqs_rows")]
internal sealed partial class RqsProbeRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("qty")]
    public int Qty { get; set; }
}
