using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// P1-V6-01 诊断探针（2026-10-06，临时不入库——诊断轮产物定案后本文件删除或转正式锁定测试）。
/// 双假设定性：
///   H-A（原登记归因）：22001 后连接归 Npgsql 池带协议残留 → 跨会话租到该连接收到
///       "unexpected backend message DataRow"。
///   H-B（CI 日志新证据）：DataRow 与并发迁移竞态同窗（pg_type duplicate/42P07 同秒），
///       归因可能是迁移竞态而非池残留。
/// 方法：H-A 单线程压力循环（毒化→受害者×50 轮，无并发迁移干扰）——复现即 H-A 实锤，
/// 不复现则 H-A 在单线程形态下不成立，归因转向 H-B。
/// </summary>
public sealed class V601DiagnosisProbe
{
    [Test]
    public async Task H_A_PoisonThenVictim_StressLoop()
    {
        int victimErrors = 0;
        string? lastError = null;
#pragma warning disable PALORM005 // 诊断探针：循环=实验设计本身（毒化→受害者逐轮压力）
        for (int round = 0; round < 50; round++)
        {
            // 毒化：独立会话插入超长值（22001 被捕获），会话释放 → 连接回池
            await using (var poison = await TestDb.PostgreSqlAsync())
            {
                await poison.MigrateAsync();
                try
                {
                    await poison.InsertAsync(new V601ProbeEntity { Name = new string('x', 65), Amount = 1m });
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == "22001")
                {
                    // 预期：值超长被数据库拒绝
                }
            }

            // 受害者：新会话租池内连接执行读与标量（原登记的触发面=ExecuteScalar/GetAsync）
            try
            {
                await using var victim = await TestDb.PostgreSqlAsync();
                long count = await victim.ScalarAsync<long>($"SELECT COUNT(*) FROM v601_probe");
                _ = await victim.GetAsync<V601ProbeEntity>(1);
                if (count < 0) throw new InvalidOperationException("unreachable");
            }
            catch (Exception ex)
            {
                victimErrors++;
                lastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        await Assert.That(victimErrors).IsEqualTo(0)
            .Because($"H-A 复现：50 轮中毒化后受害者失败 {victimErrors} 次，末次={lastError}");
#pragma warning restore PALORM005
    }

    [Test]
    public async Task H_B_ConcurrentMigrations_RaceEvidence()
    {
        // H-B：并发 MigrateAsync（模拟 CI 跨类并行）——收集全部异常形态。
        // 若出现 42P07/pg_type duplicate/DataRow 族，则 DataRow 的归因=迁移竞态而非池残留。
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = new List<Task>();
        for (int w = 0; w < 4; w++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await using var db = await TestDb.PostgreSqlAsync();
                    await db.MigrateAsync();
                    await db.ScalarAsync<long>($"SELECT COUNT(*) FROM v601_probe");
                }
                catch (Exception ex)
                {
                    errors.Add($"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                }
            }));
        }
        await Task.WhenAll(tasks);

        await Assert.That(errors).IsEmpty()
            .Because($"H-B 复现：4 路并发迁移异常 {errors.Count} 项：{string.Join(" | ", errors.Take(4))}");
    }
}

#region Test Entities
[Table("v601_probe")]
internal sealed partial class V601ProbeEntity
{
    [Key] public long Id { get; set; }
    [Column("name", Length = 64)] public string Name { get; set; } = "";
    [Column("amount", Precision = 10, Scale = 2)] public decimal Amount { get; set; }
}
#endregion
