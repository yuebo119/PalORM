using System.Diagnostics.CodeAnalysis;

namespace PalORM.Core.Tests;

/// <summary>会话级弹性配置器（<see cref="DataSession{TProvider}.WithRetry"/> /
/// <see cref="DataSession{TProvider}.WithCircuitBreaker"/> /
/// <see cref="DataSession{TProvider}.WithTimeout"/>）与 <see cref="QueryBuilder{T}.TagWithCaller"/> 的覆盖。
/// <para><b>为什么需要它</b>：这三个会话方法此前**没有任何测试经过**——既有弹性测试全部走
/// <c>DbOptions</c>（会话构造期配置），而这三个方法是运行期经
/// <c>UpdateResilience</c> 热替换执行器的另一条入口；若热替换回归（例如换错字段、没发布快照），
/// 既有测试全绿而生产配置路径失效。<c>TagWithCaller</c> 则是文档声明的 API 里唯一
/// 从未在任何测试中出现过的构建器方法。</para>
/// <para>断言口径：证明"方法调用 → 行为差异真实发生"——WithRetry 用「DbOptions 零重试 +
/// 会话方法开启重试后重试确实发生」证明热替换生效；WithTimeout 断言秒数真的到达命令对象。</para></summary>
internal sealed class SessionResilienceConfiguratorTests
{
    private static DbOptions ZeroResilienceOptions() => new()
    {
        ConnectionString = "flaky",
        MaxRetries = 0,
        CircuitBreakerThreshold = 0,
        // 排除退避等待对测试时长的影响
        RetryBackoff = static _ => TimeSpan.Zero
    };

    private static (FlakyConnection Connection, DataSession<FlakyProvider> Session) CreateSession(
        DbOptions options)
    {
        var connection = new FlakyConnection();
        return (connection, new DataSession<FlakyProvider>(connection, options, []));
    }

    [Test]
    public async Task WithRetry_OnZeroConfigSession_ActuallySwapsExecutor()
    {
        var (connection, session) = CreateSession(ZeroResilienceOptions());
        await using var _ = session;
        connection.FailFirstNReads = 1;

        // DbOptions 是零重试直通配置——若 WithRetry 没有热替换执行器，
        // 这次读取会直接抛瞬时异常且 ReaderAttempts 停在 1
        session.WithRetry(3);

        List<FlakyEntity> rows = await session.From<FlakyEntity>().ToListAsync();

        await Assert.That(connection.ReaderAttempts).IsEqualTo(2);
        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task WithCircuitBreaker_OnZeroConfigSession_OpensAfterConsecutiveFailures()
    {
        var (connection, session) = CreateSession(ZeroResilienceOptions());
        await using var _ = session;
        connection.FailFirstNReads = int.MaxValue; // 每次读取都瞬时失败

        // resetAfter 必须大于零：Zero 意味着开闸即半开，第三次请求作为探针放行，
        // 永远看不到 CircuitBreakerOpenException（本用例第一版就栽在这里）
        session.WithCircuitBreaker(2, TimeSpan.FromSeconds(30));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Assert.That(async () => await session.From<FlakyEntity>().ToListAsync())
                .Throws<FlakyTransientException>();
        }

        // 两次最终失败后电路打开——后续读取快速失败，不再触达连接
        int attemptsBeforeOpen = connection.ReaderAttempts;
        await Assert.That(async () => await session.From<FlakyEntity>().ToListAsync())
            .Throws<CircuitBreakerOpenException>();
        await Assert.That(connection.ReaderAttempts).IsEqualTo(attemptsBeforeOpen);
    }

    [Test]
    public async Task WithTimeout_ReachesCommand_AndResetsPriorResilienceSwap()
    {
        var (connection, session) = CreateSession(ZeroResilienceOptions());
        await using var _ = session;
        connection.FailFirstNReads = 1;

        // 先经会话方法开启重试……
        session.WithRetry(3);
        // ……再 WithTimeout。**实测钉住的语义是"叠加"而非"清空"**：
        // UpdateResilience 会把合并后的配置写回 _options（DataSession.cs:352），所以 WithTimeout
        // 之后重试仍然生效（本用例第一版按"重置=清空"断言 attempts==1，被当场否掉——
        // 实际 attempts==2：首次瞬时失败、重试后成功）。文档里"重置当前弹性策略状态"的
        // 准确含义是"以合并后的配置**重建执行器实例**"——既有 builder 因快照语义不受影响；
        // 该句 XML 文档已同步改写（原措辞确实让人写出了我第一版这样的错误断言）。
        session.WithTimeout(TimeSpan.FromSeconds(7));

        List<FlakyEntity> rows = await session.From<FlakyEntity>().ToListAsync();
        await Assert.That(connection.ReaderAttempts).IsEqualTo(2); // 叠加语义：重试仍生效
        await Assert.That(rows).IsEmpty();
        await Assert.That(connection.LastCreatedCommand).IsNotNull();
        await Assert.That(connection.LastCreatedCommand!.CommandTimeout)
            .IsEqualTo(DbOptions.ToCommandTimeoutSeconds(TimeSpan.FromSeconds(7)));
    }

    [Test]
    public async Task TagWithCaller_EmbedsFileLineAndMemberIntoSqlComment()
    {
        DataSession<FlakyProvider> session = CreateSession(ZeroResilienceOptions()).Session;
        await using var _ = session;

        // TagWithCaller 依赖 [CallerMemberName]/[CallerFilePath]/[CallerLineNumber]：
        // 编译器在**本调用点**填入，因此断言的标签内容就是这个方法名与本文件名。
        string sql = session.From<FlakyEntity>().TagWithCaller().AsDryRun().Sql;

        await Assert.That(sql).Contains("SessionResilienceConfiguratorTests.cs");
        await Assert.That(sql).Contains("TagWithCaller_EmbedsFileLineAndMemberIntoSqlComment");
    }
}
