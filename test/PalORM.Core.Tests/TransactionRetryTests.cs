using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>API-003（2026-09-23）：整事务重放。序列化失败/死锁/SQLITE_BUSY 这类失败需要整事务
/// 从头再来，而弹性执行器与 WithRetry 只重试单条语句、且事务内默认关闭重试。
/// <para>判据用 SQLITE_BUSY（错误码 5）构造：真实锁竞争无法在单测里稳定复现，
/// 而重放判定按错误码（Provider 的 IsTransient）而非消息文本，构造的异常足以锁定语义。</para></summary>
internal sealed class TransactionRetryTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=retry_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE retry_rows (Id INTEGER PRIMARY KEY AUTOINCREMENT, v INTEGER NOT NULL)");
        return session;
    }

    [Test]
    public async Task WithTransactionRetry_ReplaysWholeTransactionOnBusy()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var entity = new RetryRow { V = 1 };
        int attempts = 0;

        await session.WithTransactionRetry(async ct =>
        {
            attempts++;
            if (attempts < 2)
                throw new SqliteException("database is locked", 5);  // SQLITE_BUSY
            await session.InsertAsync(entity, ct);
        });

        // 第二次尝试成功：整事务重放后 ID 照常回填（提交路径的延迟回放）
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(entity.Id).IsGreaterThan(0);
    }

    [Test]
    public async Task WithTransactionRetry_Exhausted_PropagatesOriginalFailure()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        int attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await session.WithTransactionRetry(_ =>
            {
                attempts++;
                return Task.FromException(new SqliteException("database is locked", 5));
            }, maxRetries: 2));

        await Assert.That(attempts).IsEqualTo(3);  // 1 次初始尝试 + 2 次重放
    }

    [Test]
    public async Task WithTransactionRetry_DeterministicFailure_NotReplayed()
    {
        // 确定性失败不重放——重放只会重复失败并放大延迟（与弹性执行器的判定同口径）
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        int attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.WithTransactionRetry(_ =>
            {
                attempts++;
                return Task.FromException(new InvalidOperationException("deterministic"));
            }));

        await Assert.That(attempts).IsEqualTo(1);
    }
}

[Table("retry_rows")]
internal sealed partial class RetryRow
{
    [Key] public long Id { get; set; }
    [Column("v")] public int V { get; set; }
}
