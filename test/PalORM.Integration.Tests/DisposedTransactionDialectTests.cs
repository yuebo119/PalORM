using Npgsql;
using PalORM;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>T7/step7（P1-33）：已释放事务的跨驱动一致失败形态。
/// <para><b>背景</b>：Npgsql 已释放事务的 <c>Connection</c> 取值抛 <see cref="ObjectDisposedException"/>
/// （Microsoft.Data.Sqlite 返回 null 不抛）。<see cref="QueryBuilder{T}.WithTransaction"/> 与
/// <c>GetActiveTransaction</c> 原先直接读 <c>.Connection</c>，PG 上用户拿到的是裸 ODE 而非
/// 设计好的 ArgumentException / InvalidOperationException——同一错误代码在两方言给出两种形态，
/// 排查方向被误导（ITM-637 注释已记录该发散，本组把它钉住）。</para>
/// <para>PG 不可用时（无连接串）跳过——与其余 ExternalDatabase 组同口径。</para></summary>
[Property("Category", "ExternalDatabase")]
[NotInParallel("ExtBulkTable")]
public sealed class DisposedTransactionDialectTests
{
    private static DbOptions PgOptions() => new()
    {
        ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString(),
    };

    [Test]
    public async Task WithTransaction_DisposedTran_ThrowsArgumentException_NotODE()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOptions());
        await db.ExecuteAsync($"DROP TABLE IF EXISTS disposed_tran_probe");
        await db.MigrateAsync();

        var tran = (NpgsqlTransaction)await db.GetRawConnection().BeginTransactionAsync();
        await tran.DisposeAsync();

        // 设计语义：ArgumentException（"it has been disposed"），不是裸 ObjectDisposedException
        await Assert.That(() => db.From<MergeEntity>().WithTransaction(tran))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ExecutingWithLateDisposedTran_ThrowsInvalidOperation_NotODE()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOptions());
        await db.ExecuteAsync($"DROP TABLE IF EXISTS disposed_tran_probe");
        await db.MigrateAsync();

        var tran = (NpgsqlTransaction)await db.GetRawConnection().BeginTransactionAsync();
        var query = db.From<MergeEntity>().WithTransaction(tran);
        await tran.DisposeAsync();  // 绑定后释放——执行期经 GetActiveTransaction 检出

        await Assert.That(async () => await query.ToListAsync())
            .Throws<InvalidOperationException>();
    }
}
