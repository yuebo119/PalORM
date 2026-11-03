using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>local_infile 探测缓存连接串键控化（EPSL 2026-10-11 审计 B 批 #2）的行为守卫：
/// 两个顺序 per-op 会话（同一连接串）各自 BulkInsert，第二个会话走缓存命中路径执行——
/// 本测试锁定命中路径下端到端行为正确（两批行数完整，探测→缓存→BulkCopy 全链路通过）。
/// 探测次数断言（per-op 会话 40 次操作探测 40→1）由审计期一次性探针承载
/// （SHOW GLOBAL STATUS 计数，独占数据库运行）；全局状态计数断言受 B57/B74 并行污染
/// 禁令约束不入库（本套件并行执行 MySQL 批量测试会污染进程级/实例级计数）。
/// <para><b>2026-11-03 串行键</b>：本类自建自清 <c>epsl_probe_cache_guard</c>，而
/// <c>GuardRow</c> 已注册进 Registry——任何并行类的 <c>MigrateAsync</c>（建全 Registry 表）
/// 都会建同表，与本类的 <c>CREATE TABLE</c> 撞「already exists」（实测约 1/6 概率的
/// 单用例失败）。挂 ExtBulkTable 与真库批量类同组串行；同时它读写 local_infile 探测缓存，
/// 一并挂 LocalInfileGlobal 与 <c>SET GLOBAL local_infile=OFF</c> 的用例互斥。</para>
/// <para><b>2026-11-04 路径确定化</b>：本类文档声明要锁「探测→缓存→BulkCopy 全链路」，
/// 但原实现依赖服务端 ambient <c>local_infile</c>——CI 的 <c>mysql:8.4</c> 默认 OFF 时
/// 两次 BulkInsert 都走多值回退，BulkCopy 路径一行不覆盖（实测：CI mysql 行覆盖
/// 66.85% vs 本地 68.80%，差的 7 行即此路径）。改为方法内显式置 ON + 断言
/// <c>@@local_infile=1</c> + finally 还原并清探测缓存，读数不再由调度决定。</para></summary>
[NotInParallel(new[] { "ExtBulkTable", "LocalInfileGlobal" })]
public sealed class LocalInfileProbeCacheTests
{
    [Test]
    public async Task MySql_BulkInsert_SequentialPerOpSessions_BothInsertCompletely()
    {
        await using var setup = await TestDb.MySqlAsync();
        await LocalInfileTestGate.Gate.WaitAsync();
        long savedLocalInfile = 1;
        try
        {
        savedLocalInfile = await setup.ScalarAsync<long>($"SELECT @@local_infile");
        await setup.ExecuteAsync($"SET GLOBAL local_infile=ON");
        MySqlProvider.ResetLocalInfileProbeCache();
        await Assert.That(await setup.ScalarAsync<long>($"SELECT @@local_infile")).IsEqualTo(1L);
#pragma warning disable S2077, CA2100 // 探针自建自清（B171 惯例）：DDL 硬编码，表名/列名无用户输入
        await setup.ExecuteAsync($"DROP TABLE IF EXISTS epsl_probe_cache_guard");
        await setup.ExecuteAsync(
            $"CREATE TABLE epsl_probe_cache_guard (id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(32) NOT NULL)");
#pragma warning restore S2077, CA2100
        try
        {
            // 两个独立 per-op 会话：第一个探测入缓存，第二个命中缓存（修复前两包装实例各探各的）
            await using (DataSession<MySqlProvider> first = await TestDb.MySqlAsync())
            {
                await first.BulkInsertAsync([new GuardRow { Name = "a" }, new GuardRow { Name = "b" }]);
            }

            await using (DataSession<MySqlProvider> second = await TestDb.MySqlAsync())
            {
                await second.BulkInsertAsync([new GuardRow { Name = "c" }, new GuardRow { Name = "d" }]);
            }

            await using DataSession<MySqlProvider> verify = await TestDb.MySqlAsync();
            List<GuardRow> rows = await verify.GetAllAsync<GuardRow>();
            List<string> names = [.. rows.Select(r => r.Name)];
            names.Sort(StringComparer.Ordinal);
            await Assert.That(rows.Count).IsEqualTo(4);
            await Assert.That(names).IsEquivalentTo(["a", "b", "c", "d"]);
        }
        finally
        {
            await setup.ExecuteAsync($"DROP TABLE IF EXISTS epsl_probe_cache_guard");
        }
        }
        finally
        {
            await setup.ExecuteAsync($"SET GLOBAL local_infile={(savedLocalInfile == 0 ? "OFF" : "ON")}");
            MySqlProvider.ResetLocalInfileProbeCache();
            LocalInfileTestGate.Gate.Release();
        }
    }
}

/// <summary>守卫测试实体——自增 long 主键 + 单字符串列（与 Itm892Row 同形态，已验证组合）。</summary>
[Table("epsl_probe_cache_guard")]
public sealed partial class GuardRow
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}
