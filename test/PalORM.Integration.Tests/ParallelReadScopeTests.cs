using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// 并行读作用域全读家族收口（2026-10-06，10-05 审计 P1 项"并行读作用域只接线一半"的锁定测试）。
/// 作用域内已接线的 GetAsync 与修复对象四家族（GetAllAsync/CountAsync/聚合/ScalarAsync）
/// 同场并发：修复前未接线家族在主连接上开第二个 reader，Npgsql 抛
/// "already open DataReader"（修复前红）；修复后各只读操作从作用域池取独立连接（修复后绿）。
/// SQLite 同连接读-读不报错，缺陷现象面在 PG，故落真库。
/// </summary>
public sealed class ParallelReadScopeTests
{
    [Test]
    public async Task ParallelReadScope_AllReadFamilies_RunConcurrently()
    {
        await using var db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        // 幂等播种：迁移只建不清，上一轮（含修复前红轮）残留行会污染计数断言
        await db.ExecuteAsync($"DELETE FROM parallel_scope_probe");

        List<ScopeProbeEntity> seed = [.. Enumerable.Range(1, 20).Select(i => new ScopeProbeEntity { Value = i })];
        await db.BulkInsertAsync(seed);

        await using (db.ForParallelReads())
        {
            // 已接线形态（GetAsync 见 ParallelReadLeaseTests）与修复对象四家族同场并发：
            // 修复前未接线家族全在主连接开第二个 reader，Npgsql 抛 "already open
            // DataReader"（WhenAll 必败）；两个 GetAllAsync 同场加重复计验证
#pragma warning disable S5034 // ValueTask 经 .AsTask() 显式转 Task 后多次 await Task 是合法的，非双消费
            Task<List<ScopeProbeEntity>> allTask = db.GetAllAsync<ScopeProbeEntity>().AsTask();
            Task<List<ScopeProbeEntity>> allAgainTask = db.GetAllAsync<ScopeProbeEntity>().AsTask();
            Task<long> countTask = db.CountAsync<ScopeProbeEntity>().AsTask();
            Task<decimal> sumTask = db.SumAsync<ScopeProbeEntity>($"Value").AsTask();
            Task<int> scalarTask = db.ScalarAsync<int>($"SELECT COUNT(*) FROM parallel_scope_probe").AsTask();
#pragma warning restore S5034
            List<ScopeProbeEntity>[] both = await Task.WhenAll(allTask, allAgainTask);
            long count = await countTask;
            decimal sum = await sumTask;
            int scalar = await scalarTask;

            await Assert.That(both[0].Count).IsEqualTo(20);
            await Assert.That(both[1].Count).IsEqualTo(20);
            await Assert.That(count).IsEqualTo(20);
            await Assert.That(sum).IsEqualTo(210m);
            await Assert.That(scalar).IsEqualTo(20);
        }
    }
}

#region Test Entities
[Table("parallel_scope_probe")]
public partial class ScopeProbeEntity
{
    [Key] public long Id { get; set; }
    [Column("value")] public int Value { get; set; }
}
#endregion
