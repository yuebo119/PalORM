using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// 单行入口契约（2026-10-06，10-05 审计 P2×2 收口的锁定测试）：
/// ① null 守卫——Insert/Update/Save 传 null 抛 ArgumentNullException（Bulk 家族既有行为，
///   修复前 NRE）；Get/Delete 的 object key 同口径。
/// ② UPSERT 二级唯一索引异常包装对称——SaveAsync 冲突更新段撞**二级唯一索引**时
///   抛 UniqueConstraintViolationException（与 InsertAsync 同型；修复前裸 MySqlException/
///   SqliteException）。主键冲突被 ON CONFLICT 吸收不报错（UPSERT 本义），只有二级索引
///   冲突才会到包装面。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "连接生命周期移交 DataSession（同 CountSqlAllocationTests 口径）；keeper 故意超出测试方法存续（Cache=Shared 内存库）。")]
internal sealed class SingleRowContractTests
{
    [Test]
    public async Task InsertAsync_NullEntity_ThrowsArgumentNull()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await session.InsertAsync<UniqueGuardRow>(null!));
    }

    [Test]
    public async Task UpdateAsync_NullEntity_ThrowsArgumentNull()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await session.UpdateAsync<UniqueGuardRow>(null!));
    }

    [Test]
    public async Task SaveAsync_NullEntity_ThrowsArgumentNull()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await session.SaveAsync<UniqueGuardRow>(null!));
    }

    [Test]
    public async Task DeleteAsync_NullKey_ThrowsArgumentNull()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await session.DeleteAsync<UniqueGuardRow>(null!));
    }

    [Test]
    public async Task GetAsync_NullKey_ThrowsArgumentNull()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await session.GetAsync<UniqueGuardRow>(null!));
    }

    [Test]
    public async Task SaveAsync_SecondaryUniqueViolation_WrapsAsUniqueConstraintViolation()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        // 两行不同主键、同 code（二级唯一索引 [Unique]）——SaveAsync 第三行（新主键、
        // 既有 code）走 INSERT 段撞二级索引：修复前裸 SqliteException，修复后与
        // InsertAsync 同型包装
        await session.InsertAsync(new UniqueGuardRow { Id = 1, Code = "X1", Value = 1 });
        await session.InsertAsync(new UniqueGuardRow { Id = 2, Code = "X2", Value = 2 });

        UniqueConstraintViolationException? wrapped = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            async () => await session.SaveAsync(new UniqueGuardRow { Id = 3, Code = "X1", Value = 3 }));
        await Assert.That(wrapped).IsNotNull();
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        string tag = Guid.NewGuid().ToString("N");
        var keeper = new SqliteConnection($"Data Source=single_row_contract_{tag};Mode=Memory;Cache=Shared");
        try
        {
            await keeper.OpenAsync();
            await using SqliteCommand cmd = keeper.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE unique_guard_rows (id INTEGER PRIMARY KEY, code TEXT NOT NULL, value INTEGER NOT NULL);" +
                "CREATE UNIQUE INDEX ux_unique_guard_code ON unique_guard_rows (code)";
            await cmd.ExecuteNonQueryAsync();
            return await DataSession<SqliteProvider>.CreateAsync(new DbOptions
            {
                ConnectionString = keeper.ConnectionString,
                MaxRetries = 0,
                CircuitBreakerThreshold = 0,
            });
        }
        catch
        {
            await keeper.DisposeAsync();
            throw;
        }
    }
}

#region Test Entities
[Table("unique_guard_rows")]
internal sealed partial class UniqueGuardRow
{
    [Key] public long Id { get; set; }
    [Unique] [Column("code")] public string Code { get; set; } = "";
    [Column("value")] public long Value { get; set; }
}
#endregion
