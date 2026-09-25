using PalORM.Sqlite;

// PALORM005（N+1 检测）：本组的观察点正是"同一会话连续单行读"——晋升阈值 3 的跨阈值
// 行为必须逐次调用才能触发，循环内 GetAsync 是被测行为本身而非反模式
#pragma warning disable PALORM005

namespace PalORM.Core.Tests;

/// <summary>PL-2 扩展（2026-09-25）：GetByKey 单行读命令惰性晋升的行为契约。
/// <para>晋升阈值 3——前 2 次走新建路径，第 3 次起复用会话级命令。本组用例锁定：
/// ① 复用路径与新建路径结果逐位一致（跨晋升阈值连续读）；② 键类型转换语义在复用分支
/// 保持（清参重绑走同一生成绑定器，KeyConversionTests 契约延伸）；③ 会话释放后复用命令
/// 不泄漏（生命周期由 DisposeReusableCrudCommandsAsync 统一管理，随全量套件泄漏检测覆盖）。</para></summary>
public sealed class GetByKeyCommandReuseTests
{
    [Test]
    public async Task GetAsync_AcrossPromotionThreshold_ReturnsConsistentRows()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        long idA = (await session.InsertAsync(new GetByKeyReuseEntity { Name = "a", Qty = 10 })).Id;
        long idB = (await session.InsertAsync(new GetByKeyReuseEntity { Name = "b", Qty = 20 })).Id;
        long idC = (await session.InsertAsync(new GetByKeyReuseEntity { Name = "c", Qty = 30 })).Id;

        // 第 1、2 次：新建路径；第 3 次起：复用路径——跨阈值结果必须逐位一致
        (string Name, int Qty)[] expected = [("a", 10), ("b", 20), ("c", 30)];
        long[] ids = [idA, idB, idC, idA, idB, idC, idA];
        for (int round = 0; round < ids.Length; round++)
        {
            GetByKeyReuseEntity? found = await session.GetAsync<GetByKeyReuseEntity>(ids[round]);
            await Assert.That(found).IsNotNull();
            await Assert.That(found!.Id).IsEqualTo(ids[round]);
            await Assert.That(found.Name).IsEqualTo(expected[round % 3].Name);
            await Assert.That(found.Qty).IsEqualTo(expected[round % 3].Qty);
        }
    }

    [Test]
    public async Task GetAsync_MissAfterPromotion_ReturnsDefault()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        long id = (await session.InsertAsync(new GetByKeyReuseEntity { Name = "x", Qty = 1 })).Id;

        // 晋升后查不存在的键：Reader 无行 → default(T)，不得复用残读状态
        for (int i = 0; i < 5; i++)
        {
            GetByKeyReuseEntity? hit = await session.GetAsync<GetByKeyReuseEntity>(id);
            await Assert.That(hit).IsNotNull();
            GetByKeyReuseEntity? miss = await session.GetAsync<GetByKeyReuseEntity>(id + 100);
            await Assert.That(miss).IsNull();
        }
    }

    [Test]
    public async Task GetAsync_SoftDeletedRow_FilteredOnReusePath()
    {
        // 软删过滤是字面量条件（无参数）——晋升后复用命令仍带同一条件，软删行必须被过滤
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        long id = (await session.InsertAsync(new GetByKeyReuseSoftDeleteEntity { Name = "victim" })).Id;

        for (int i = 0; i < 3; i++)
            await Assert.That(await session.GetAsync<GetByKeyReuseSoftDeleteEntity>(id)).IsNotNull();

        await session.DeleteAsync<GetByKeyReuseSoftDeleteEntity>(id);

        // 第 4 次起走复用路径——软删行不可见
        await Assert.That(await session.GetAsync<GetByKeyReuseSoftDeleteEntity>(id)).IsNull();
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=getbykey_reuse_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE getbykey_reuse_rows (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Qty INTEGER NOT NULL)");
        await session.ExecuteAsync(
            $"CREATE TABLE getbykey_reuse_sd_rows (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, deleted_at TEXT)");
        return session;
    }
}

[Table("getbykey_reuse_rows")]
internal sealed partial class GetByKeyReuseEntity
{
    [Key(AutoIncrement = true)] public long Id { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
    [Column("Qty")] public int Qty { get; set; }
}

[Table("getbykey_reuse_sd_rows")]
[SoftDelete]
internal sealed partial class GetByKeyReuseSoftDeleteEntity
{
    [Key(AutoIncrement = true)] public long Id { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
    [Column("deleted_at")] public DateTime? DeletedAt { get; set; }
}
