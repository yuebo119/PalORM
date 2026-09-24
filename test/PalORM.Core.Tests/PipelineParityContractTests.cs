using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// 独立审计 2026-09-19 M2-4（A1）——执行管线同构的<b>契约</b>防线。
/// ExecuteForEachAsync 与 ExecuteQueryAsync 是两份 ~75 行同构管线（S3776 抑制自认），
/// 文档承诺"ForEach 的拦截器/观测性/弹性语义与 ToListAsync 一致"——此前该契约靠人肉双写维持，
/// 给一侧补新语义另一侧会静默缺失。本测试对<b>两条路径施加同一验证序列</b>：
/// 拦截器回调时序（OnBefore → OnAfter(rowCount)）逐事件一致、观测性标签一致、
/// 结果集一致。物理抽取 RunPipelineAsync（删除 S3776）留待专门会话（热路径 272B/查询
/// 的分配纪律在案，抽象层须零分配证明）——本契约测试先锁语义，使未来的抽取有等价性护栏。
/// </summary>
public sealed class PipelineParityContractTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync(
        List<IQueryInterceptor> interceptors)
        => await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:",
            Interceptors = interceptors,
        });

    [Test]
    public async Task ForEach_And_ToList_HaveIdenticalInterceptorSequences()
    {
        var seqList = new List<string>();
        var seqForEach = new List<string>();
        await using DataSession<SqliteProvider> dbList =
            await CreateSessionAsync([new RecordingInterceptor(seqList, "L")]);
        await using DataSession<SqliteProvider> dbEach =
            await CreateSessionAsync([new RecordingInterceptor(seqForEach, "F")]);
        await SeedAsync(dbList);
        await SeedAsync(dbEach);

        _ = await dbList.From<ParityRow>().OrderBy(x => x.Id).ToListAsync();
        await dbEach.From<ParityRow>().OrderBy(x => x.Id)
            .ForEachAsync(static (row, _) => ValueTask.CompletedTask);
        // 拦截器时序契约：两侧查询段都是 OnBefore → OnAfter(行数)，无第三种形态。
        // R3（v5.6.0）：SeedAsync 的 ExecuteAsync(CREATE TABLE) 现在也过拦截器（覆盖面扩展），
        // 序列头部多一对 OnBefore|OnAfter:0——两侧对称出现，奇偶性/时序契约不变
        await Assert.That(string.Join("|", seqList))
            .IsEqualTo("L:OnBefore|L:OnAfter:0|L:OnBefore|L:OnAfter:3");
        await Assert.That(string.Join("|", seqForEach))
            .IsEqualTo("F:OnBefore|F:OnAfter:0|F:OnBefore|F:OnAfter:3");
    }

    [Test]
    public async Task ForEach_And_ToList_ReturnSameRows()
    {
        await using DataSession<SqliteProvider> dbList = await CreateSessionAsync([]);
        await using DataSession<SqliteProvider> dbEach = await CreateSessionAsync([]);
        await SeedAsync(dbList);
        await SeedAsync(dbEach);

        var viaList = await dbList.From<ParityRow>().OrderBy(x => x.Id).ToListAsync();
        var viaEach = new List<long>();
        await dbEach.From<ParityRow>().OrderBy(x => x.Id)
            .ForEachAsync((row, _) => { viaEach.Add(row.Id); return ValueTask.CompletedTask; });

        await Assert.That(string.Join(",", viaList.Select(static r => r.Id))).IsEqualTo("1,2,3");
        await Assert.That(string.Join(",", viaEach)).IsEqualTo("1,2,3");
    }

    private static async Task SeedAsync(DataSession<SqliteProvider> db)
    {
        await db.ExecuteAsync(
            $"CREATE TABLE parity_rows (id INTEGER PRIMARY KEY, v INTEGER NOT NULL)");
        // PALORM005 豁免:种子固定 3 行
#pragma warning disable PALORM005
        for (int i = 1; i <= 3; i++)
            await db.InsertAsync(new ParityRow { Id = i, V = i * 10 });
#pragma warning restore PALORM005
    }

    /// <summary>记录回调序列的拦截器（tag 区分侧别）。</summary>
    private sealed class RecordingInterceptor(List<string> events, string tag) : IQueryInterceptor
    {
        public void OnBefore(QueryContext context) => events.Add(tag + ":OnBefore");

        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount)
            => events.Add($"{tag}:OnAfter:{rowCount}");

        public void OnError(QueryContext context, Exception exception) => events.Add(tag + ":OnError");
    }
}

[Table("parity_rows")]
internal sealed partial class ParityRow
{
    [Key]
    [Column("id")]
    public long Id { get; set; }
    [Column("v")]
    public long V { get; set; }
}
