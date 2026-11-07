using System.Collections.Concurrent;
using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>CS-11（并发故障注入子场景，修订版方案 §5）。
/// <para><b>补的缺口</b>：原方案要求「32 在途操作同时取消 + 按命令序号注入连接断开/超时/取消，
/// 叠加池租还」。池侧无法注入自定义连接工厂（产品无该接口），但<b>并发故障注入</b>本身可以
/// 用「每 worker 一个包装会话」实现：8 个会话同时执行，各自按自己的命令序号注入异常。</para>
/// <para><b>判据</b>：① 每个 worker 只看到自己的注入消息（故障不串会话）；② 注入后同一会话
/// 仍可用（下一条命令成功写入）；③ 每库恰有自己那一行（无跨库污染）。</para>
/// <para><b>未覆盖</b>：池租还与故障注入的组合仍 deferred（需产品提供连接工厂注入点），
/// 池租还的取消形态已由 CS-11 主用例与 <c>CancellationFaultTests</c> 覆盖。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class ConcurrentFaultInjectionTests
{
    [Test]
    public async Task CS11_ConcurrentFaultInjection_SessionsAreIsolated_AndRemainUsable()
    {
        const int Workers = 8;
        var observed = new ConcurrentDictionary<int, string>();
        var failures = new ConcurrentBag<string>();
        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Task[] tasks = [.. Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            var created = await FaultInjectionFixtures.CreateAsync(
                keeper => new FaultyConnection(keeper).FailOnCommand(
                    1, new InvalidOperationException($"boom-{worker}")),
                name: $"cs11_{worker}");
            await using DataSession<SqliteProvider> session = created.Session;
            try
            {
                try
                {
                    await session.ExecuteAsync($"SELECT 1");
                    failures.Add($"worker={worker}: 注入异常未触发");
                }
                catch (InvalidOperationException exception)
                {
                    observed[worker] = exception.Message;
                }
                catch (Exception exception)
                {
                    failures.Add($"worker={worker}: 异常类型 {exception.GetType().Name}");
                }

                // 故障之后会话仍可用：写入自己那行并读回
                await session.InsertAsync(FaultInjectionFixtures.Row($"w{worker}", worker));
                long count = await session.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM fault_row WHERE Name = {"w" + worker}");
                if (count != 1L)
                    failures.Add($"worker={worker}: 行数 {count}，期望 1");
            }
            catch (Exception exception)
            {
                failures.Add($"worker={worker}: 后续操作失败 {exception.GetType().Name}: {exception.Message}");
            }
        }, window.Token))];

        await Task.WhenAll(tasks).WaitAsync(window.Token);

        await Assert.That(string.Join(" | ", failures.Take(5))).IsEmpty();
        await Assert.That(observed.Count).IsEqualTo(Workers);
        for (int worker = 0; worker < Workers; worker++)
            await Assert.That(observed[worker]).IsEqualTo($"boom-{worker}");
    }
}
