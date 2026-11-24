using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>StoredProcBuilder 执行时取会话现值连接（FLT-5 同族，E9-01 / 2026-11-24）。
/// builder 在 StoredProc() 时构造、执行可能跨多个操作；其间会话若因判死重建整体替换了连接，
/// 构造期快照已释放。这里锁定执行前调用 connectionProvider 取现值，而非使用构造期快照。</summary>
public sealed class StoredProcConnectionProviderTests
{
    [Test]
    public async Task ExecuteAsync_UsesCurrentConnectionProvider()
    {
        await using SqliteConnection snapshot = new("Data Source=:memory:");
        await snapshot.OpenAsync();
        await using SqliteConnection current = new("Data Source=:memory:");
        await current.OpenAsync();

        int providerCalls = 0;
        var builder = new StoredProcBuilder(
            snapshot, "cf_noop", TimeSpan.FromSeconds(5),
            static (name, value) => new SqliteParameter(name, value ?? DBNull.Value),
            new SessionOperationState(),
            connectionProvider: () => { providerCalls++; return current; });

        try { await builder.ExecuteAsync(); }
        catch (Exception) { /* SQLite 无存储过程：驱动拒绝属预期，这里只关心取值路径 */ }

        await Assert.That(providerCalls).IsGreaterThan(0)
            .Because("执行前必须调用 connectionProvider 取现值连接，不能用构造期快照");
    }

    // SessionOperationState 为 internal，测试程序集经 IVT 直接构造。
}
