using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>SessionBatch 的行为契约（B1）——SQLite 档（回退路径：无批量 API）。
/// PG/MySQL 真 DbBatch 路径的契约在 PalORM.Integration.Tests.SessionBatchDialectTests。
/// <para>钉住：① 语句真实生效；② 事务原子性（整批回滚）；③ 空批 no-op；
/// ④ 空白语句 Append 期拒绝（ITM-745 同口径）；⑤ 全无参语句合并单次多语句往返（L38，
/// 2026-09-25）与带参/混合形态的逐条复用路径（L37）结果逐位等价。</para></summary>
internal sealed class SessionBatchTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=batch_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE batch_rows (id INTEGER PRIMARY KEY, v INTEGER NOT NULL, tag TEXT NOT NULL)");
        return session;
    }

    [Test]
    public async Task Append_Parameterized_ExecutesAllAndIsReadable()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        using SessionBatch<SqliteProvider> batch = session.CreateBatch();
        int affected = await batch
            .Append($"INSERT INTO batch_rows (id, v, tag) VALUES ({1}, {10}, {"a"})")
            .Append($"INSERT INTO batch_rows (id, v, tag) VALUES ({2}, {20}, {"b"})")
            .Append($"UPDATE batch_rows SET v = {11} WHERE id = {1}")
            .ExecuteNonQueryAsync();

        await Assert.That(affected).IsEqualTo(3);
        var rows = (await session.From<BatchRow>().ToListAsync()).OrderBy(static r => r.Id).ToList();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].V).IsEqualTo(11); // UPDATE 生效
        await Assert.That(rows[1].Tag).IsEqualTo("b");
    }

    [Test]
    public async Task BatchInsideTransaction_RollsBackAllOnFailure()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        await Assert.That(async () => await session.WithTransaction(async ct =>
        {
            using SessionBatch<SqliteProvider> batch = session.CreateBatch();
            await batch
                .Append($"INSERT INTO batch_rows (id, v, tag) VALUES ({1}, {1}, {"ok"})")
                .Append($"INSERT INTO nonexistent_table (x) VALUES ({1})") // 第二条失败 → 整批回滚
                .ExecuteNonQueryAsync(ct);
            return true;
        })).Throws<Exception>();

        await Assert.That(await session.CountAsync<BatchRow>()).IsEqualTo(0); // 第一条也回滚
    }

    [Test]
    public async Task EmptyBatch_IsNoOp()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        using SessionBatch<SqliteProvider> batch = session.CreateBatch();
        await Assert.That(await batch.ExecuteNonQueryAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task BlankStatement_RejectedAtAppend()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        using SessionBatch<SqliteProvider> batch = session.CreateBatch();
        await Assert.That(() => batch.Append($"   ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Append_AfterDispose_ThrowsObjectDisposed()
    {
        // OPS-002（2026-09-22）：Dispose 后 Append 此前会拿已释放的 _scratch 建参数、语句静默累积
        // （下一次 Execute 才在驱动侧炸）。补零成本守卫，把失败点前移到调用处。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        SessionBatch<SqliteProvider> batch = session.CreateBatch();
        batch.Dispose();

        await Assert.That(() => batch.Append($"INSERT INTO batch_rows (v, tag) VALUES (1, 'x')"))
            .Throws<ObjectDisposedException>();
        await Assert.That(() => batch.AppendRaw("CREATE TABLE batch_probe (id INTEGER)"))
            .Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task AppendRaw_AllParameterless_MergedExecution_SumsAffected()
    {
        // L38（2026-09-25）：全无参语句合并为单个多语句命令一次往返——效果与逐条等价：
        // 全部生效、受影响行数跨语句累计（驱动 RecordsAffected 语义）。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        using SessionBatch<SqliteProvider> batch = session.CreateBatch();
        int affected = await batch
            .AppendRaw("INSERT INTO batch_rows (id, v, tag) VALUES (1, 10, 'a')")
            .AppendRaw("INSERT INTO batch_rows (id, v, tag) VALUES (2, 20, 'b')")
            .AppendRaw("UPDATE batch_rows SET v = 11 WHERE id = 1")
            .ExecuteNonQueryAsync();

        await Assert.That(affected).IsEqualTo(3);
        var rows = (await session.From<BatchRow>().ToListAsync()).OrderBy(static r => r.Id).ToList();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].V).IsEqualTo(11);
        await Assert.That(rows[1].Tag).IsEqualTo("b");
    }

    [Test]
    public async Task Mixed_ParameterizedAndRaw_ExecutesAllInOrder()
    {
        // L37/L38 混合形态：带参语句阻断合并 → 逐条复用路径，语句顺序 = 追加顺序。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        using SessionBatch<SqliteProvider> batch = session.CreateBatch();
        int affected = await batch
            .AppendRaw("INSERT INTO batch_rows (id, v, tag) VALUES (1, 10, 'a')")
            .Append($"INSERT INTO batch_rows (id, v, tag) VALUES ({2}, {20}, {"b"})")
            .AppendRaw("UPDATE batch_rows SET v = 21 WHERE id = 2")
            .ExecuteNonQueryAsync();

        await Assert.That(affected).IsEqualTo(3);
        var rows = (await session.From<BatchRow>().ToListAsync()).OrderBy(static r => r.Id).ToList();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].V).IsEqualTo(10);
        await Assert.That(rows[1].V).IsEqualTo(21);
    }

    [Test]
    public async Task MergedBatchInsideTransaction_RollsBackAllOnFailure()
    {
        // 合并路径与逐条路径同契约：事务内任一语句失败 → 整批回滚。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        await Assert.That(async () => await session.WithTransaction(async ct =>
        {
            using SessionBatch<SqliteProvider> batch = session.CreateBatch();
            await batch
                .AppendRaw("INSERT INTO batch_rows (id, v, tag) VALUES (1, 1, 'ok')")
                .AppendRaw("INSERT INTO nonexistent_table (x) VALUES (1)") // 第二条失败 → 整批回滚
                .ExecuteNonQueryAsync(ct);
            return true;
        })).Throws<Exception>();

        await Assert.That(await session.CountAsync<BatchRow>()).IsEqualTo(0);
    }
}

[Table("batch_rows")]
internal sealed partial class BatchRow
{
    [Key] public long Id { get; set; }
    [Column("v")] public int V { get; set; }
    [Column("tag")] public string Tag { get; set; } = "";
}
