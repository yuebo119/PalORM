using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>PL-3（2026-09-24）：InsertNoReturning 实体的 INSERT 不读返回。
/// <para><b>判定边界</b>（SupportsInsertWithoutReturning 保守条件）：恰一个非自增主键 +
/// 全部列 IsInsertable 且无转换器/OwnedJson → 插入值即行值，纯 INSERT 即完成语义，
/// 省 RETURNING/LAST_INSERT_ID 的读返回与物化（拆账 .ai/perf-probe/TxPathDiag.cs：
/// 该段约 3.1 µs/条 + 432 B/条）。任一条件不满足 → 保持读返回契约。</para>
/// <para><b>覆盖的回归面</b>：① 判定值（正/负用例钉死）；② 生成 SQL 形态（无 RETURNING）；
/// ③ 值正确性与返回同一引用（PL-3 契约：不产生物化副本）；④ 复用路径晋升后值不累积；
/// ⑤ 事务回滚语义不变；⑥ 判否实体仍走读返回（DB 默认值列读回真源，返回新实例）。</para></summary>
// PALORM005（N+1 检测）：本组的观察点正是"逐条插入路径"，循环内逐条 DB 调用
// 是被测行为的载体而非缺陷（同 SingleRowCommandReuseTests 的口径）
#pragma warning disable PALORM005
public sealed class InsertNoReturningTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync(string name)
    {
        return await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source={name}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
    }

    // ─── ① 判定值：正/负用例钉死 ────────────────────────────

    [Test]
    public async Task InsertNoReturningFlag_MatchesConservativeConditions()
    {
        // 显式主键 + 纯可插入列 → true（PL-3 形态）
        await Assert.That(PalORM_Runtime.CrudMetadatas[typeof(ReuseManual)].InsertNoReturning).IsTrue();
        // 自增主键 → 需回填 ID → false
        await Assert.That(PalORM_Runtime.CrudMetadatas[typeof(ReuseAuto)].InsertNoReturning).IsFalse();
        // [IgnoreOnInsert] DB 默认值列 → 行值由 DB 决定 → false
        await Assert.That(PalORM_Runtime.CrudMetadatas[typeof(ReuseReturning)].InsertNoReturning).IsFalse();
    }

    // ─── ② 生成 SQL 形态 ────────────────────────────

    [Test]
    public async Task GeneratedInsertSql_HasNoReturning_ForPlainForm()
    {
        // PL-3 形态的纯 INSERT（T13：断言的是已读过的生成逻辑 BuildInsertSql——无 RETURNING 后缀）
        string insert = PalORM_Runtime.CommandSqlsByDialect[typeof(ReuseManual)].Sqlite.Insert;
        await Assert.That(insert).IsNotEmpty();
        await Assert.That(insert.Contains("RETURNING", StringComparison.OrdinalIgnoreCase)).IsFalse();

        // 判否实体的读返回变体必须保持 RETURNING（对照，防"把 RETURNING 从生成器里删掉"式误改）
        string returning = PalORM_Runtime.CommandSqlsByDialect[typeof(ReuseReturning)].Sqlite.InsertReturning;
        await Assert.That(returning.Contains("RETURNING", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    // ─── ③ 值正确性与返回同一引用 + ④ 复用晋升后不累积 ────────────────────────────

    [Test]
    public async Task RepeatedInsert_ReturnsSameReference_EachRowLandsExactly()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("inr_insert");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_upd (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");

        for (int i = 1; i <= 30; i++)
        {
            var entity = new ReuseManual { Id = i, Name = "n" + i, Qty = i };
            ReuseManual inserted = await session.InsertAsync(entity);
            // PL-3 契约：插入值即行值，返回传入实体本身（同一引用，无物化副本）
            bool sameReference = ReferenceEquals(inserted, entity);
            await Assert.That(sameReference).IsTrue();
        }

        // 全部行逐值落库——参数池残留/错位会让任意一行值漂移
        ReuseManual[] all = [.. await session.From<ReuseManual>().OrderBy(x => x.Id).ToListAsync()];
        await Assert.That(all.Length).IsEqualTo(30);
        for (int i = 0; i < 30; i++)
        {
            await Assert.That(all[i].Id).IsEqualTo(i + 1L);
            await Assert.That(all[i].Name).IsEqualTo("n" + (i + 1));
            await Assert.That(all[i].Qty).IsEqualTo(i + 1);
        }
    }

    // ─── ⑤ 事务回滚语义不变 ────────────────────────────

    [Test]
    public async Task TransactionRollback_UndoPlainInserts()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("inr_tx");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_upd (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.WithTransaction(async _ =>
            {
                for (int i = 1; i <= 5; i++)
                    await session.InsertAsync(new ReuseManual { Id = i, Name = "n" + i, Qty = i }, ct: default);
                throw new InvalidOperationException("rollback");
            }, ct: default));

        long count = await session.CountAsync<ReuseManual>();
        await Assert.That(count).IsEqualTo(0L);
    }

    // ─── ⑥ 判否实体仍走读返回 ────────────────────────────

    [Test]
    public async Task IgnoredOnInsertColumn_ReadsBackDbSource_OnReturningPath()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync("inr_ret");
        await session.ExecuteAsync(
            $"CREATE TABLE srr_ret (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL, note TEXT DEFAULT 'db')");

        var entity = new ReuseReturning { Id = 1, Name = "n1", Qty = 1, Note = null };
        ReuseReturning inserted = await session.InsertAsync(entity);
        // 读返回路径物化的是 DB 侧行——返回新实例，IgnoreOnInsert 列是 DB 真源（DEFAULT 'db'）
        await Assert.That(ReferenceEquals(inserted, entity)).IsFalse();
        await Assert.That(inserted.Note).IsEqualTo("db");

        // 连续第二次（PL-2 晋升后的复用路径）行为一致。
        // Note 传了实体值但 [IgnoreOnInsert] 列不进 INSERT——这正是该列使判定判否的原因：
        // 行值由 DB 决定（DEFAULT），读返回才是真源
        var entity2 = new ReuseReturning { Id = 2, Name = "n2", Qty = 2, Note = "given" };
        ReuseReturning inserted2 = await session.InsertAsync(entity2);
        await Assert.That(ReferenceEquals(inserted2, entity2)).IsFalse();
        await Assert.That(inserted2.Note).IsEqualTo("db");
    }
}
#pragma warning restore PALORM005
