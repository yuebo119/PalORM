using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>R3（2026-10-12）：单行物理删除的命令复用槽（PL-2 第四槽，GetByKey 同型）。
/// 行为锁定：跨晋升阈值的连续删除值正确（键值就地写不串味）、软删实体不进槽走软删路径、
/// 会话释放清理槽。删除密集负载的分配收益由 bench A/B 负责（B74：套件内不断言分配数）。</summary>
[NotInParallel("SingleRowCommandReuse")]
#pragma warning disable PALORM005 // 循环内逐条删除是被测复用行为的载体
public sealed class DeleteCommandReuseTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSeededAsync(int rows)
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=delreuse_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE delreuse_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        for (int i = 1; i <= rows; i++)
            await session.ExecuteAsync($"INSERT INTO delreuse_rows (id, name) VALUES ({(long)i}, {"n" + i})");
        return session;
    }

    [Test]
    public async Task RepeatedDelete_AcrossPromotionThreshold_DeletesExactRows()
    {
        await using DataSession<SqliteProvider> session = await CreateSeededAsync(40);

        for (int i = 1; i <= 30; i++)
        {
            int affected = await session.DeleteAsync<DelReuseRow>((long)i);
            await Assert.That(affected).IsEqualTo(1);
        }

        // 剩余 10 行且恰为 31..40（键值串味会删错行或漏删）
        List<DelReuseRow> rest = await session.From<DelReuseRow>().OrderBy(x => x.Id).ToListAsync();
        await Assert.That(rest.Count).IsEqualTo(10);
        await Assert.That(rest[0].Id).IsEqualTo(31L);
        await Assert.That(rest[^1].Id).IsEqualTo(40L);

        // 复删除已删键：0 行（复用命令不复活旧键）
        await Assert.That(await session.DeleteAsync<DelReuseRow>(1L)).IsEqualTo(0);
    }

    [Test]
    public async Task SoftDeleteEntity_PhysicalSlotNotUsed_SoftDeletePathUnchanged()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=delsd_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        try
        {
            await session.ExecuteAsync(
                $"CREATE TABLE delsd_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL, deleted_at TEXT)");
            await session.ExecuteAsync($"INSERT INTO delsd_rows (id, name) VALUES ({1L}, {"a"})");

            int affected = await session.DeleteAsync<DelSoftRow>(1L);
            await Assert.That(affected).IsEqualTo(1);

            // 软删：行仍在（deleted_at 已置），默认过滤查询不可见
            List<DelSoftRow> visible = await session.From<DelSoftRow>().ToListAsync();
            await Assert.That(visible.Count).IsEqualTo(0);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}

[Table("delreuse_rows")]
internal sealed partial class DelReuseRow
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

[Table("delsd_rows")]
[SoftDelete]
internal sealed partial class DelSoftRow
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}
