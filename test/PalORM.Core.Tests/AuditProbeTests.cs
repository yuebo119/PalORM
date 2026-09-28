using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>r12 全量审计探针（2026-09-28）——片 2/片 4 疑点的 B11 实证：
/// <para>① ForParallelReads 作用域内 ForEachAsync 是否结构性必抛（片 2 发现 1，P1 候选）；</para>
/// <para>② 并行读池在并发租借压力下是否触发非线程安全集合异常（片 4 发现 1，概率探针——
/// 零失败不构成"无竞态"证明，仅提供压力证据）；</para>
/// <para>③ MaxParallelReads 双层租约双计语义（片 2 发现 10）由代码推演确凿，修复时以行为断言锁定。</para></summary>
internal sealed class AuditProbeTests
{
    private static async Task<DataSession<SqliteProvider>> CreateProbeSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=probe_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync($"CREATE TABLE probe_rows (Id INTEGER PRIMARY KEY, v TEXT NOT NULL)");
        await session.ExecuteAsync($"INSERT INTO probe_rows (Id, v) VALUES (1, 'a')");
        return session;
    }

    [Test]
    public async Task Probe1_ForParallelReads_Scope_InnerForEachAsync_Succeeds()
    {
        // ITM-811 修复后的锁定形态（r23 反转）：作用域内 ForEachAsync 与 ToListAsync
        // 同型成功（内层租约复用外层读租约不双计）。修复前该路径结构性必抛——
        // 撤修复验红见 r23 报告（AuditProbeTests.Probe1 原断言必抛形态）。
        await using DataSession<SqliteProvider> session = await CreateProbeSessionAsync();
        long seen = 0;
        await using (session.ForParallelReads())
        {
            long count = await session.From<ProbeRow>()
                .ForEachAsync((row, _) => { seen++; return ValueTask.CompletedTask; });
            await Assert.That(count).IsEqualTo(1L);
            await Assert.That(seen).IsEqualTo(1L);

            // 对照组：同作用域内 ToListAsync 正常
            List<ProbeRow> rows = await session.From<ProbeRow>().ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Probe2_ParallelReadPool_ConcurrentAcquireStress_NoException()
    {
        // 概率探针：50 轮 × 8 路并发 ToListAsync（未配置读路由 → 每路经并行池开独立连接）。
        // 无锁 Stack/List 若有 Pop/Add 竞态，压力下应出现 "Pop on empty stack" / 索引错乱 / 连接复用异常。
        await using DataSession<SqliteProvider> session = await CreateProbeSessionAsync();
        int failures = 0;
        string? firstFailure = null;

        await using (session.ForParallelReads())
        {
            for (int round = 0; round < 50; round++)
            {
                try
                {
                    List<ProbeRow>[] all = await Task.WhenAll(Enumerable.Range(0, 8)
                        .Select(_ => session.From<ProbeRow>().ToListAsync().AsTask()));
                    int[] wrongCounts = [.. all.Select(static rows => rows.Count)
                        .Where(static count => count != 1)];
                    if (wrongCounts.Length > 0)
                    {
                        failures += wrongCounts.Length;
                        firstFailure ??= $"行数错乱: {wrongCounts[0]}";
                    }
                }
                catch (Exception exception)
                {
                    failures++;
                    firstFailure ??= $"{exception.GetType().Name}: {exception.Message}";
                }
            }
        }

        if (failures > 0)
        {
            Console.WriteLine($"PROBE2-FAIL: failures={failures} first={firstFailure}");
        }
        await Assert.That(failures).IsEqualTo(0);
    }

    [Test]
    public async Task Probe3_DbParameter_ReuseAcrossSequentialCommands_SucceedsOnSqlite()
    {
        // ITM-863（r23 探针）：重试路径把同一批 DbParameter 实例 Add 到新建命令——
        // SQLite 臂验证驱动无归属拒绝（同一参数先后进两个命令、各自执行成功）。
        // PG/MySQL 臂需真库注入探针（后续项），本测试不构成它们的无竞态证明。
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        using var first = connection.CreateCommand();
        first.CommandText = "SELECT @p0";
        var parameter = first.CreateParameter();
        parameter.ParameterName = "@p0";
        parameter.Value = 42L;
        first.Parameters.Add(parameter);
        _ = await first.ExecuteScalarAsync();
        await first.DisposeAsync();

        using var second = connection.CreateCommand();
        second.CommandText = "SELECT @p0";
        second.Parameters.Add(parameter);
        object? result = await second.ExecuteScalarAsync();
        await Assert.That(result).IsEqualTo(42L);
    }
}

[Table("probe_rows")]
internal sealed partial class ProbeRow
{
    [Key] public long Id { get; set; }
    [Column("v")] public string V { get; set; } = "";
}
