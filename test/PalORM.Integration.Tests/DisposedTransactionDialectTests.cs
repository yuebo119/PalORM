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
/// <para><b>为什么无建表</b>：两个用例的失败都发生在 SQL 执行<b>之前</b>（绑定期/执行管线
/// 入口），不需要任何表。刻意不调 <c>MigrateAsync</c>——全实体建表会给 PG 系统目录加并发压，
/// 触发 B63 记载的 pg_type/pg_class 23505 竞态连累同组夹具。</para></summary>
[Property("Category", "ExternalDatabase")]
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

        var tran = (NpgsqlTransaction)await db.GetRawConnection().BeginTransactionAsync();
        var query = db.From<MergeEntity>().WithTransaction(tran);
        await tran.DisposeAsync();  // 绑定后释放——执行期经 GetActiveTransaction 检出

        await Assert.That(async () => await query.ToListAsync())
            .Throws<InvalidOperationException>();
    }
}
