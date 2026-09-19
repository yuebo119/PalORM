using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>悲观锁子句的**执行**验证。
/// <para><b>为什么需要它</b>：<c>ForUpdate</c>/<c>ForShare</c> 此前在整个测试套件里只被
/// DryRun 断言过 SQL 形态，**从未真正执行**——即"锁子句拼在语句的合法位置、目标方言能接受"
/// 这件事没有任何覆盖。锁子句错了只在运行时炸（语法错误或锁语义缺失），而 SQLite 又执行不了
/// （ITM-639：SQLite 上只支持预览形态），所以必须在 PG/MySQL 真库上验证。</para>
/// <para>断言口径：事务内执行并取回行（真跑通）；同时断言锁子句出现在 WHERE **之后**
/// （位置是子句拼接顺序的函数，顺序回归会让语句非法）。</para></summary>
internal sealed class PessimisticLockTests
{
    private static DbOptions PgOpts => new() { ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString() };
    private static DbOptions MySqlOpts => new() { ConnectionString = TestEnvironment.ResolveMySqlConnectionString() };

    [Test]
    [Property("Category", "ExternalDatabase")]
    // 编入 ExtBulkTable 组：本组所有 PG 用例都调 MigrateAsync（全实体建表），
    // PG 并发 DDL 在系统目录（pg_type/pg_class）上竞态 → 23505 偶发失败（E1 同类 flaky）
    [NotInParallel("ExtBulkTable")]
    public async Task PostgreSql_LockClauses_ExecuteInsideTransaction()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS lock_probe");
        await db.MigrateAsync();
        await db.InsertAsync(new LockProbe { Label = "locked", Weight = 1 });

        await db.WithTransaction(async ct =>
        {
            var forUpdate = db.From<LockProbe>().Where($"weight = {1L}").ForUpdate();
            var rows = await forUpdate.ToListAsync(ct);
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].Label).IsEqualTo("locked");
            return true;
        });

        await db.WithTransaction(async ct =>
        {
            var forShare = db.From<LockProbe>().Where($"weight = {1L}").ForShare();
            var rows = await forShare.ToListAsync(ct);
            await Assert.That(rows.Count).IsEqualTo(1);
            return true;
        });

        await db.WithTransaction(async ct =>
        {
            var skipLocked = db.From<LockProbe>().Where($"weight = {1L}").ForUpdate(skipLocked: true);
            await Assert.That((await skipLocked.ToListAsync(ct)).Count).IsEqualTo(1);
            return true;
        });

        // 位置契约：锁子句必须在 WHERE 之后（子句拼接顺序回归会让语句非法，但 DryRun 断言看不到）
        string sql = db.From<LockProbe>().Where($"weight = {1L}").ForUpdate().AsDryRun().Sql;
        await Assert.That(sql.IndexOf("WHERE", StringComparison.Ordinal))
            .IsLessThan(sql.IndexOf("FOR UPDATE", StringComparison.Ordinal));
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task MySql_LockClauses_ExecuteInsideTransaction()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(MySqlOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS lock_probe");
        await db.MigrateAsync();
        await db.InsertAsync(new LockProbe { Label = "locked", Weight = 1 });

        await db.WithTransaction(async ct =>
        {
            var rows = await db.From<LockProbe>().Where($"weight = {1L}").ForUpdate().ToListAsync(ct);
            await Assert.That(rows.Count).IsEqualTo(1);
            return true;
        });

        await db.WithTransaction(async ct =>
        {
            // FOR SHARE 需 MySQL 8.0+（目标 8.4）
            var rows = await db.From<LockProbe>().Where($"weight = {1L}").ForShare().ToListAsync(ct);
            await Assert.That(rows.Count).IsEqualTo(1);
            return true;
        });

        await db.WithTransaction(async ct =>
        {
            var rows = await db.From<LockProbe>().Where($"weight = {1L}")
                .ForUpdate(skipLocked: true).ToListAsync(ct);
            await Assert.That(rows.Count).IsEqualTo(1);
            return true;
        });
    }
}

[Table("lock_probe")]
internal sealed partial class LockProbe
{
    [Key] public long Id { get; set; }
    [Column("label")] public string Label { get; set; } = "";
    [Column("weight")] public long Weight { get; set; }
}
