using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>A+B（2026-10-25）：SQLite 批量插入/UPSERT 行预编译路径的锁定测试。
/// <para>路径切换后现有批量族测试（<see cref="BulkBatchLimitTests"/>、
/// <see cref="UpsertTenantGuardTests"/>、BulkMergeDefaultKeyBatchTests）全部经由新路径
/// 执行，行为等价由它们锁定；本文件锁定新路径的特有行为：整批原子性、外部事务
/// 尊重、batchSize 兼容语义、批内重复主键的宽容语义（相对多值批的已知边界差异）。</para></summary>
internal sealed class RowPreparedBulkTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=rowprepared_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE row_prepared_rows (Id TEXT PRIMARY KEY, v INTEGER NOT NULL, tag TEXT NOT NULL)");
        return session;
    }

    private static List<RowPreparedRow> BuildRows(int count, string tag)
    {
        var entities = new List<RowPreparedRow>(count);
        for (int i = 1; i <= count; i++)
            entities.Add(new RowPreparedRow { Id = $"k{i}", V = i, Tag = tag });
        return entities;
    }

    [Test]
    public async Task BulkInsert_LargeList_RoundTripsAllRows()
    {
        // 2500 行 > 旧多值形态的任何单语句批宽——行预编译无单语句参数约束，逐行入同一事务
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        List<RowPreparedRow> entities = BuildRows(2500, "t");

        long inserted = await session.BulkInsertAsync(entities);

        await Assert.That(inserted).IsEqualTo(2500);
        List<RowPreparedRow> rows = await session.GetAllAsync<RowPreparedRow>();
        await Assert.That(rows.Count).IsEqualTo(2500);
        await Assert.That(rows[0].Tag).IsEqualTo("t");
        await Assert.That(rows[2499].Id).IsEqualTo("k2500");
        await Assert.That(rows[2499].V).IsEqualTo(2500);
    }

    [Test]
    public async Task BulkInsert_MidBatchConstraintFailure_RollsBackWholeBatch()
    {
        // 行 N 主键冲突 → 已执行的前 N-1 行必须随事务回滚（全有或全无）
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var entities = BuildRows(10, "a");
        entities.Add(new RowPreparedRow { Id = "k1", V = 999, Tag = "dup" });

        bool threw = false;
        try
        {
            await session.BulkInsertAsync(entities);
        }
        catch
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That((await session.GetAllAsync<RowPreparedRow>()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task BulkInsert_ExternalTransactionRollback_LeavesNoRows()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        List<RowPreparedRow> entities = BuildRows(50, "tx");

        Exception? actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.WithTransaction<object?>(async token =>
            {
                await session.BulkInsertAsync(entities, batchSize: 1000, ct: token);
                throw new InvalidOperationException("deliberate rollback");
            }));

        await Assert.That(actual!.Message).IsEqualTo("deliberate rollback");
        await Assert.That((await session.GetAllAsync<RowPreparedRow>()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task BulkInsert_ExplicitBatchSize_AcceptedAndIgnoresStatementShape()
    {
        // batchSize 语义变更（A 方案）：不再决定语句形状，≤0 仍拒绝，正值全兼容
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        List<RowPreparedRow> entities = BuildRows(30, "bs");

        await Assert.That(await session.BulkInsertAsync(entities, batchSize: 1)).IsEqualTo(30);
        var second = new List<RowPreparedRow>(10);
        for (int i = 1; i <= 10; i++)
            second.Add(new RowPreparedRow { Id = $"s{i}", V = i, Tag = "bs2" });
        await Assert.That(await session.BulkInsertAsync(second, batchSize: 100_000)).IsEqualTo(10);
        await Assert.That((await session.GetAllAsync<RowPreparedRow>()).Count).IsEqualTo(40);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await session.BulkInsertAsync(BuildRows(1, "x"), batchSize: 0));
    }

    [Test]
    public async Task BulkMerge_InsertThenConflictUpdate_RoundTrips()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        List<RowPreparedRow> entities = BuildRows(300, "first");

        await session.BulkMergeAsync(entities);
        await Assert.That((await session.GetAllAsync<RowPreparedRow>()).Count).IsEqualTo(300);

        foreach (RowPreparedRow entity in entities)
            entity.Tag = "second";
        await session.BulkMergeAsync(entities);

        List<RowPreparedRow> rows = await session.GetAllAsync<RowPreparedRow>();
        await Assert.That(rows.Count).IsEqualTo(300);
        await Assert.That(rows.All(static row => row.Tag == "second")).IsTrue();
    }

    [Test]
    public async Task BulkMerge_DuplicateKeysWithinSingleCall_LastRowWins()
    {
        // 已知边界（B 方案登记）：多值批对同批内同一行更新两次报错（ON CONFLICT 同语句限制），
        // 行预编译逐行执行与 SaveCore 逐条语义一致——后行覆盖前行，不报错
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var entities = new List<RowPreparedRow>
        {
            new() { Id = "dup", V = 1, Tag = "first" },
            new() { Id = "dup", V = 2, Tag = "last" },
        };

        await session.BulkMergeAsync(entities);

        List<RowPreparedRow> rows = await session.GetAllAsync<RowPreparedRow>();
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Tag).IsEqualTo("last");
        await Assert.That(rows[0].V).IsEqualTo(2);
    }
}

[Table("row_prepared_rows")]
internal sealed partial class RowPreparedRow
{
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("v")] public int V { get; set; }
    [Column("tag")] public string Tag { get; set; } = "";
}
