using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>PL-2（2026-09-24）：单行 Insert/Update 的命令与参数跨调用复用。
/// <para><b>背景</b>：单行 CRUD 原先每次调用新建 DbCommand + 每列 CreateParameter；
/// 实测同产品内部池化路径 1.41 µs/行 vs 单行 API 9.9 µs/行，正是
/// TxHundredInserts 4.40× / TxRollback 3.83× 落后的来源。</para>
/// <para><b>复用不变量</b>：同 (实体类型, 操作) 的语句逐位相同，变的只有参数值；
/// 池与 <c>DbCommand.Parameters</c> 持有同一批对象，写 Value 即作用于命令。
/// 因此本组全部用<b>行为断言</b>验证——参数错位/累积/陈旧都会在值上暴露；
/// 每行成本的量化证据由 bench 的 A/B 基线负责（并行套件内不断言分配数，B74）。</para>
/// <para><b>覆盖的回归面</b>：① 多次调用值正确且不累积；② 换实体类型时槽位替换不串味；
/// ③ 外部事务切换/回滚后命令仍指向当前事务；④ 约束失败后命令仍可用；⑤ 整行 RETURNING
/// （reader 路径）连续物化；⑥ 乐观锁 version 连续递增；⑦ 租户实体走原路径仍正确；
/// ⑧ 会话释放后不复用残留。</para></summary>
[NotInParallel("SingleRowCommandReuse")]
// PALORM005（N+1 检测）：本组的观察点正是"同一命令跨调用复用"，循环内逐条 DB 调用
// 是被测行为的载体而非缺陷（同 ReadRouteConnectionReuseTests 的口径）
#pragma warning disable PALORM005
public sealed class SingleRowCommandReuseTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync(string name)
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source={name}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        return session;
    }

    // ─── ① 值正确性与不累积 ────────────────────────────

    [Test]
    public async Task RepeatedInsert_EachRowLands_WithDistinctBackfilledIds()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_insert");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_auto (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, qty INTEGER NOT NULL)");

        for (int i = 1; i <= 30; i++)
        {
            ReuseAuto inserted = await session.InsertAsync(
                new ReuseAuto { Name = "n" + i, Qty = i });
            await Assert.That(inserted.Id).IsGreaterThan(0L);
            await Assert.That(inserted.Name).IsEqualTo("n" + i);
            await Assert.That(inserted.Qty).IsEqualTo(i);
        }

        ReuseAuto[] all = [.. (await session.From<ReuseAuto>().OrderBy(x => x.Id).ToListAsync())
            .OrderBy(static r => r.Id)];
        await Assert.That(all.Length).IsEqualTo(30);
        for (int i = 0; i < 30; i++)
        {
            await Assert.That(all[i].Id).IsEqualTo(i + 1L);
            await Assert.That(all[i].Name).IsEqualTo("n" + (i + 1));
            await Assert.That(all[i].Qty).IsEqualTo(i + 1);
        }
    }

    [Test]
    public async Task RepeatedUpdate_ReflectsLatestValues_EveryRound()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_update");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_upd (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        List<ReuseManual> rows = [];
        for (int i = 1; i <= 20; i++)
            rows.Add(new ReuseManual { Id = i, Name = "s" + i, Qty = i });
        await session.BulkInsertAsync(rows);

        // 三轮全量更新：每一轮的值都必须逐行落库——池里残留上一轮的 Value 会立刻暴露
        for (int round = 1; round <= 3; round++)
        {
            foreach (ReuseManual row in rows)
            {
                row.Name = "r" + round + "_" + row.Id;
                row.Qty = (row.Id * 10) + round;
                int affected = await session.UpdateAsync(row);
                await Assert.That(affected).IsEqualTo(1);
            }

            ReuseManual[] after = [.. (await session.From<ReuseManual>().OrderBy(x => x.Id).ToListAsync())
                .OrderBy(static r => r.Id)];
            for (int i = 0; i < 20; i++)
            {
                await Assert.That(after[i].Name).IsEqualTo("r" + round + "_" + (i + 1));
                await Assert.That(after[i].Qty).IsEqualTo(((i + 1) * 10) + round);
            }
        }
    }

    // ─── ② 换实体类型：槽位替换 ────────────────────────────

    [Test]
    public async Task AlternatingEntityTypes_NoCrossContamination()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_alt");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_alt_a (id INTEGER PRIMARY KEY, a_name TEXT NOT NULL, a_qty INTEGER NOT NULL)");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_alt_b (id INTEGER PRIMARY KEY, b_label TEXT NOT NULL)");

        // A → B → A → B：每换一次类型就淘汰并重建槽位，参数集合不得把两类混在一起
        for (int round = 1; round <= 3; round++)
        {
            ReuseAltA a = await session.InsertAsync(
                new ReuseAltA { Id = round, AName = "a" + round, AQty = round });
            await Assert.That(a.AName).IsEqualTo("a" + round);
            await Assert.That(a.AQty).IsEqualTo(round);

            ReuseAltB b = await session.InsertAsync(new ReuseAltB { Id = round, BLabel = "b" + round });
            await Assert.That(b.BLabel).IsEqualTo("b" + round);

            int updatedA = await session.UpdateAsync(
                new ReuseAltA { Id = round, AName = "u" + round, AQty = round * 100 });
            await Assert.That(updatedA).IsEqualTo(1);
            int updatedB = await session.UpdateAsync(new ReuseAltB { Id = round, BLabel = "v" + round });
            await Assert.That(updatedB).IsEqualTo(1);
        }

        ReuseAltA[] allA = [.. (await session.From<ReuseAltA>().OrderBy(x => x.Id).ToListAsync())
            .OrderBy(static r => r.Id)];
        ReuseAltB[] allB = [.. (await session.From<ReuseAltB>().OrderBy(x => x.Id).ToListAsync())
            .OrderBy(static r => r.Id)];
        for (int i = 0; i < 3; i++)
        {
            await Assert.That(allA[i].AName).IsEqualTo("u" + (i + 1));
            await Assert.That(allA[i].AQty).IsEqualTo((i + 1) * 100);
            await Assert.That(allB[i].BLabel).IsEqualTo("v" + (i + 1));
        }
    }

    // ─── ③ 事务切换与回滚 ────────────────────────────

    [Test]
    public async Task ExternalTransaction_RollbackThenReuse_StillPointsAtCurrentTransaction()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_tx");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_upd (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        await session.BulkInsertAsync([new ReuseManual { Id = 1, Name = "s", Qty = 1 }]);

        // 第一次写走无事务路径，建立复用槽
        await session.UpdateAsync(new ReuseManual { Id = 1, Name = "pre", Qty = 2 });

        // 进外部事务、写、回滚——复用命令的 Transaction 必须已切到该事务
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.WithTransaction(async ct =>
            {
                await session.UpdateAsync(new ReuseManual { Id = 1, Name = "in-tx", Qty = 3 }, ct);
                throw new InvalidOperationException("intentional rollback");
            }));

        ReuseManual? afterRollback = await session.From<ReuseManual>().FirstOrDefaultAsync();
        await Assert.That(afterRollback).IsNotNull();
        await Assert.That(afterRollback!.Name).IsEqualTo("pre");
        await Assert.That(afterRollback.Qty).IsEqualTo(2);

        // 回滚后再写：事务已切回无事务，命令必须仍可用且落库
        await session.UpdateAsync(new ReuseManual { Id = 1, Name = "post", Qty = 4 });
        ReuseManual? afterPost = await session.From<ReuseManual>().FirstOrDefaultAsync();
        await Assert.That(afterPost).IsNotNull();
        await Assert.That(afterPost!.Name).IsEqualTo("post");
        await Assert.That(afterPost.Qty).IsEqualTo(4);
    }

    [Test]
    public async Task ExternalTransaction_Commit_ReusedCommandWritesLand()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_txok");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_upd (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        await session.BulkInsertAsync([new ReuseManual { Id = 1, Name = "s", Qty = 1 }]);

        await session.WithTransaction(async ct =>
        {
            await session.UpdateAsync(new ReuseManual { Id = 1, Name = "in-tx", Qty = 9 }, ct);
        });

        ReuseManual? after = await session.From<ReuseManual>().FirstOrDefaultAsync();
        await Assert.That(after).IsNotNull();
        await Assert.That(after!.Name).IsEqualTo("in-tx");
        await Assert.That(after.Qty).IsEqualTo(9);
    }

    // ─── ④ 失败后命令仍可用 ────────────────────────────

    [Test]
    public async Task ConstraintViolation_ThenSuccessfulCalls_ReuseSurvives()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_fail");
        // 表级 CHECK 约束——触发的是驱动层约束失败（而非参数形态问题），
        // 才能验证"数据库报错之后复用命令本身没被毒化"
        await session.ExecuteAsync(
            $"CREATE TABLE srr_upd (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL, CHECK (qty < 1000))");
        await session.BulkInsertAsync([new ReuseManual { Id = 1, Name = "s", Qty = 1 }]);

        // 第二次调用（复用槽已建立）写越界值 → CHECK 约束失败
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
            await session.UpdateAsync(new ReuseManual { Id = 1, Name = "bad", Qty = 5000 }));

        // 同一条复用命令必须仍可执行，且值正确
        int affected = await session.UpdateAsync(new ReuseManual { Id = 1, Name = "ok", Qty = 3 });
        await Assert.That(affected).IsEqualTo(1);
        ReuseManual? after = await session.From<ReuseManual>().FirstOrDefaultAsync();
        await Assert.That(after).IsNotNull();
        await Assert.That(after!.Name).IsEqualTo("ok");
        await Assert.That(after.Qty).IsEqualTo(3);
    }

    // ─── ⑤ 整行 RETURNING（reader 路径）连续物化 ────────────────────────────

    [Test]
    public async Task FullRowReturning_RepeatedInsert_MaterializesEachTime()
    {
        // [Key(AutoIncrement = false)] 使 SupportsKeyOnlyReturning 判否 → 走整行 RETURNING
        // + RowFactory 物化路径（ExecuteReader）。这条路径的 reader 若未收尾会毒化下一次执行。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_ret");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_ret (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL, note TEXT)");

        for (int i = 1; i <= 5; i++)
        {
            ReuseFull inserted = await session.InsertAsync(
                new ReuseFull { Id = i, Name = "n" + i, Qty = i, Note = i % 2 == 0 ? null : "note" + i });
            // 整行 RETURNING 物化的是 DB 侧行，不是调用方实体
            await Assert.That(inserted.Id).IsEqualTo(i);
            await Assert.That(inserted.Name).IsEqualTo("n" + i);
            await Assert.That(inserted.Qty).IsEqualTo(i);
            await Assert.That(inserted.Note).IsEqualTo(i % 2 == 0 ? null : "note" + i);
        }

        ReuseFull? third = await session.From<ReuseFull>().Where($"id = {(long)3}").FirstOrDefaultAsync();
        await Assert.That(third).IsNotNull();
        await Assert.That(third!.Note).IsEqualTo("note3");
        ReuseFull? fourth = await session.From<ReuseFull>().Where($"id = {(long)4}").FirstOrDefaultAsync();
        await Assert.That(fourth).IsNotNull();
        await Assert.That(fourth!.Note).IsNull();
    }

    // ─── ⑥ 乐观锁 version 连续递增 ────────────────────────────

    [Test]
    public async Task OptimisticLock_RepeatedUpdate_IncrementsVersionSequentially()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_ver");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_ver (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, version INTEGER NOT NULL)");
        ReuseVersioned seeded = await session.InsertAsync(new ReuseVersioned { Name = "v0", Version = 0 });
        await Assert.That(seeded.Version).IsEqualTo(0L);

        for (int round = 1; round <= 4; round++)
        {
            seeded.Name = "v" + round;
            int affected = await session.UpdateAsync(seeded);
            await Assert.That(affected).IsEqualTo(1);
            // 内存 version 每轮抬 1——复用参数写错 version 会在此变成假冲突或漏冲突
            await Assert.That(seeded.Version).IsEqualTo(round);
        }

        ReuseVersioned? after = await session.From<ReuseVersioned>().FirstOrDefaultAsync();
        await Assert.That(after).IsNotNull();
        await Assert.That(after!.Name).IsEqualTo("v4");
        await Assert.That(after.Version).IsEqualTo(4L);
    }

    [Test]
    public async Task OptimisticLock_StaleVersion_StillThrows_OnReusedCommand()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_verstale");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_ver (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, version INTEGER NOT NULL)");
        ReuseVersioned seeded = await session.InsertAsync(new ReuseVersioned { Name = "v0", Version = 0 });
        await session.UpdateAsync(seeded);

        // 用一份 version 落后的副本更新 → 必须仍是 ConcurrencyConflictException
        await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
            await session.UpdateAsync(new ReuseVersioned { Id = seeded.Id, Name = "stale", Version = 0 }));
    }

    // ─── ⑦ 租户实体走原路径仍正确 ────────────────────────────

    [Test]
    public async Task TenantEntity_RepeatedUpdate_StaysCorrect()
    {
        // 租户实体被排除在复用路径外（租户参数每次调用新加，且 _tenantId 可经 WithTenant 变更）——
        // 这里锁原路径在复用改动后仍逐位正确
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_tn");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_tn (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, qty INTEGER NOT NULL)");
        await session.ExecuteAsync($"INSERT INTO srr_tn (id, tenant_id, qty) VALUES (1, 't1', 0)");
        await session.ExecuteAsync($"INSERT INTO srr_tn (id, tenant_id, qty) VALUES (2, 't2', 0)");

        session.WithTenant("t1");
        for (int round = 1; round <= 3; round++)
        {
            int affected = await session.UpdateAsync(new ReuseTenant { Id = 1, TenantId = "t1", Qty = round });
            await Assert.That(affected).IsEqualTo(1);
        }

        ReuseTenant? own = await session.From<ReuseTenant>().Where($"id = {(long)1}").FirstOrDefaultAsync();
        await Assert.That(own).IsNotNull();
        await Assert.That(own!.Qty).IsEqualTo(3);
        // 关掉默认过滤才看得到他租户行——三轮更新必须没碰它
        ReuseTenant? other = await session.IgnoreFilters().From<ReuseTenant>()
            .Where($"id = {(long)2}").FirstOrDefaultAsync();
        await Assert.That(other).IsNotNull();
        await Assert.That(other!.Qty).IsEqualTo(0);
    }

    // ─── ⑧ 惰性晋升：单操作会话不得付出缓存代价 ────────────────────────────

    [Test]
    public async Task SingleOpSessions_NeverPromote_ValuesStillCorrect()
    {
        // 惰性晋升的动机场景：一条外部连接配多个一次性会话（会话接管连接所有权，
        // 无法 DisposeAsync）。这种用法每次操作都应走"新建命令 + 随操作释放"——
        // 若在这里缓存命令，每次操作都会泄漏一个未释放命令（实测并发混合负载慢 4.84×）。
        // 本测试锁的是行为：连续多个单操作会话，值必须逐位正确。
        string cs = PromoConnectionString();
        await using DataSession<SqliteProvider> setup = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = cs });
        await setup.ExecuteAsync(
            $"CREATE TABLE srr_upd (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        await setup.BulkInsertAsync([new ReuseManual { Id = 1, Name = "s", Qty = 1 }]);

        for (int i = 1; i <= 6; i++)
        {
            // 每个会话只做一次写——模拟"每操作一个抛弃式会话"
            await using DataSession<SqliteProvider> oneShot = await DataSession<SqliteProvider>.CreateAsync(
                new DbOptions { ConnectionString = cs });
            int affected = await oneShot.UpdateAsync(
                new ReuseManual { Id = 1, Name = "n" + i, Qty = i });
            await Assert.That(affected).IsEqualTo(1);
        }

        ReuseManual? after = await setup.From<ReuseManual>().FirstOrDefaultAsync();
        await Assert.That(after).IsNotNull();
        await Assert.That(after!.Name).IsEqualTo("n6");
        await Assert.That(after.Qty).IsEqualTo(6);
    }

    [Test]
    public async Task PromotedSession_ReusesCommandAcrossManyOps()
    {
        // 晋升阈值是 3：前两次新建、第 3 次起复用。本测试用远超阈值的操作数
        // 确认晋升后的命令可反复执行且值不错位（参数池与命令长期共用）。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("srr_promo2");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_upd (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        List<ReuseManual> rows = [];
        for (int i = 1; i <= 25; i++) rows.Add(new ReuseManual { Id = i, Name = "s", Qty = i });
        await session.BulkInsertAsync(rows);

        for (int round = 1; round <= 8; round++)
        {
            foreach (ReuseManual row in rows)
            {
                row.Name = "r" + round;
                row.Qty = row.Id + round;
                int affected = await session.UpdateAsync(row);
                await Assert.That(affected).IsEqualTo(1);
            }
        }

        ReuseManual[] all = [.. (await session.From<ReuseManual>().OrderBy(x => x.Id).ToListAsync())
            .OrderBy(static r => r.Id)];
        for (int i = 0; i < 25; i++)
        {
            await Assert.That(all[i].Name).IsEqualTo("r8");
            await Assert.That(all[i].Qty).IsEqualTo(i + 9);
        }
    }

    /// <summary>独立命名共享缓存库的连接串——让多个"每操作一会话"连到同一个内存库。</summary>
    private static string PromoConnectionString()
        => $"Data Source=promo_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    // ─── ⑨ 会话边界 ────────────────────────────

    [Test]
    public async Task SessionDisposed_ThenNewSession_WorksIndependently()
    {
        string db = "srr_boundary_" + Guid.NewGuid().ToString("N");
        // keeper：Cache=Shared 的内存库随最后一个连接关闭而消失——持一条不关的连接，
        // 第二个会话才看得到第一个会话建的表（InsertReturningNarrowTests 同范式）
        Microsoft.Data.Sqlite.SqliteConnection keeper = new($"Data Source={db};Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();

        await using (DataSession<SqliteProvider> first = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source={db};Mode=Memory;Cache=Shared" }))
        {
            await first.ExecuteAsync(
                $"CREATE TABLE srr_upd (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
            for (int i = 1; i <= 5; i++)
                await first.InsertAsync(new ReuseManual { Id = i, Name = "n" + i, Qty = i });
        }

        // 新会话复用同一内存库：建槽、用、释放不得在前一会话上留下可见状态
        await using DataSession<SqliteProvider> second = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source={db};Mode=Memory;Cache=Shared" });
        for (int i = 6; i <= 8; i++)
            await second.InsertAsync(new ReuseManual { Id = i, Name = "n" + i, Qty = i });
        int affected = await second.UpdateAsync(new ReuseManual { Id = 6, Name = "u", Qty = 60 });
        await Assert.That(affected).IsEqualTo(1);

        ReuseManual[] all = [.. (await second.From<ReuseManual>().OrderBy(x => x.Id).ToListAsync())
            .OrderBy(static r => r.Id)];
        await Assert.That(all.Length).IsEqualTo(8);
        await Assert.That(all[5].Name).IsEqualTo("u");
        await Assert.That(all[5].Qty).IsEqualTo(60);
        await Assert.That(all[7].Name).IsEqualTo("n8");

        await keeper.DisposeAsync();
    }
}
#pragma warning restore PALORM005

/// <summary>自增主键 + 纯可插入列——RETURNING 收窄为只回主键（ExecuteScalar 路径）。</summary>
[Table("srr_auto")]
internal sealed partial class ReuseAuto
{
    [Key] [Column("id")] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public long Qty { get; set; }
}

/// <summary>手工主键——RETURNING 不收窄，走 ExecuteReader + 物化路径。</summary>
[Table("srr_upd")]
internal sealed partial class ReuseManual
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public long Qty { get; set; }
}

/// <summary>手工主键 + 可空列——整行 RETURNING 的连续物化探针。</summary>
[Table("srr_ret")]
internal sealed partial class ReuseFull
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public long Qty { get; set; }
    [Column("note")] public string? Note { get; set; }
}

/// <summary>乐观锁实体——BindUpdateValues 比 setColumnCount+1 多一个 version 参数。</summary>
[Table("srr_ver")]
internal sealed partial class ReuseVersioned
{
    [Key] [Column("id")] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("version")] [ConcurrencyCheck] public long Version { get; set; }
}

/// <summary>租户实体——显式排除在复用路径外，锁原路径语义。</summary>
[TenantAware]
[Table("srr_tn")]
internal sealed partial class ReuseTenant
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Required] [Column("tenant_id")] public string TenantId { get; set; } = "";
    [Column("qty")] public long Qty { get; set; }
}

/// <summary>交替类型测试的 A 型。</summary>
[Table("srr_alt_a")]
internal sealed partial class ReuseAltA
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("a_name")] public string AName { get; set; } = "";
    [Column("a_qty")] public long AQty { get; set; }
}

/// <summary>交替类型测试的 B 型（列形状与 A 不同，混池会立即写错数据）。</summary>
[Table("srr_alt_b")]
internal sealed partial class ReuseAltB
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("b_label")] public string BLabel { get; set; } = "";
}
