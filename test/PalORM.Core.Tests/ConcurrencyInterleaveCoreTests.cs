namespace PalORM.Core.Tests;

/// <summary>L0 确定性交错（Core 通道，修订版方案 §5 CS-02 与 M0 并发化夹具）。
/// <para>放 Core.Tests 是为了复用现成的 <c>ConcurrencyResources</c>（TCS 精确编排），
/// 不重复造 DbConnection 样板。Integration 通道的 CS-01/03/05 见
/// <c>PalORM.Integration.Tests.Conformance.ConcurrencyInterleaveTests</c>。</para></summary>
public sealed class ConcurrencyInterleaveCoreTests
{
    /// <summary>CS-02：重叠操作被响亮拒绝，精确异常消息。
    /// <para>评审修正：不读 _state/_disposing 私有标志（T2）。产品注释
    /// SessionOperationState.cs:769 已把「放行 iff _state==0」修正为 _disposing 镜像，
    /// 内部状态判据本就不稳，改锁外部可观察行为。</para></summary>
    [Test]
    public async Task CS02_OverlappingOperations_RejectWithExactMessage()
    {
        await using var resources = new ConcurrencyResources();
        Task<long> first = resources.Session.ScalarAsync<long>($"SELECT controlled").AsTask();
        await resources.Connection.Started;

        Exception? overlap = await CaptureAsync(async () =>
            await resources.Session.ExecuteAsync($"SELECT 1")).ConfigureAwait(false);
        await Assert.That(overlap).IsNotNull();
        await Assert.That(overlap!.GetType().Name).IsEqualTo("InvalidOperationException");
        await Assert.That(overlap.Message)
            .IsEqualTo("DataSession already has an active database operation.");

        resources.Connection.Release();
        long value = await first;
        await Assert.That(value).IsGreaterThan(0L);
    }

    /// <summary>并发化夹具能失败验证：多个 worker 并发登记不同序号的计划，全部生效且不互相覆盖。
    /// 若 _plans 仍是普通 List，并发 Add 会丢项或抛异常，本例必红。</summary>
    [Test]
    public async Task FaultyConnection_ConcurrentPlanRegistration_AllPlansSurvive()
    {
        using var inner = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await inner.OpenAsync();
        await using var faulty = new TestSupport.FaultInjection.FaultyConnection(inner);

        const int Workers = 16;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] tasks = Enumerable.Range(1, Workers)
            .Select(ordinal => Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                faulty.FailOnCommand(ordinal, new InvalidOperationException($"boom-{ordinal}"));
            }))
            .ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await Assert.That(faulty.Plans.Count).IsEqualTo(Workers);
        await Assert.That(faulty.Plans.Select(p => p.CommandOrdinal).OrderBy(x => x).ToArray())
            .IsEquivalentTo(Enumerable.Range(1, Workers).ToArray());
    }

    /// <summary>并发化夹具能失败验证：同一序号重复登记必须被拒，而不是静默覆盖。
    /// 若 TryAdd 换成索引赋值，本例必红。</summary>
    [Test]
    public async Task FaultyConnection_DuplicateOrdinal_IsRejected()
    {
        using var inner = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await inner.OpenAsync();
        await using var faulty = new TestSupport.FaultInjection.FaultyConnection(inner);
        faulty.FailOnCommand(1, new InvalidOperationException("first"));

        Exception? duplicate = await CaptureAsync(() =>
        {
            faulty.FailOnCommand(1, new InvalidOperationException("second"));
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await Assert.That(duplicate).IsNotNull();
        await Assert.That(duplicate!.Message).Contains("已登记故障计划");
        await Assert.That(faulty.Plans.Count).IsEqualTo(1);
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
