namespace PalORM.Core.Tests;

/// <summary>R2-#9(2026-10-13 微项):owner 重入快路径(Enter 免锁只读判定)的并发守卫锁定。
/// <para>快路径安全性依赖三条 volatile 只读判定:_isActive + owner 匹配 + 事务流不冲突
/// (异流事务显式挡下落锁路径)。SessionOperationState 是 internal 且测试程序集可见,
/// 直接驱动状态机锁定三面:① 异流事务下 owner 重入仍响亮拒绝(快路径不得绕过锁路径);
/// ② 同流 owner 重入返回 default 租约(快/锁路径行为逐位一致);③ 非 owner 进入仍拒绝
/// (快路径不削弱门禁基础语义)。</para></summary>
public sealed class OwnerReentryFastPathTests
{
    [Test]
    public async Task OwnerReentry_UnderForeignTransactionFlow_StillThrows()
    {
        SessionOperationState state = new();
        // v4.6:_activeOperationOwner 恒为状态机实例——重入 owner 的真实值就是 state 本身
        using SessionOperationState.SessionOperationLease leaseA = state.Enter(state);
        object flowOwner = await Task.Run(async () =>
        {
            // 异流开启事务流:EC 隔离使本流的 _currentTransactionOwner 恒 null
            object owner = state.EnterTransactionFlow();
            await Task.Yield();
            return owner;
        });

        // 本流 owner 重入:快路径第三条件不满足(_transactionOwner 非 null 且与本流
        // AsyncLocal 不匹配)→ 落锁路径 → 锁路径抛异流事务归属
        InvalidOperationException? thrown = null;
        try
        {
            _ = state.Enter(state);
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }
        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message.Contains(
            "belongs to another asynchronous flow", StringComparison.Ordinal)).IsTrue();

        // 清场:异流退出事务流,租约释放
        await Task.Run(() => state.ExitTransactionFlow(flowOwner));
    }

    [Test]
    public async Task OwnerReentry_SameFlow_ReturnsDefaultLease()
    {
        SessionOperationState state = new();
        using SessionOperationState.SessionOperationLease leaseA = state.Enter(state);
        // 同流重入(事务编排循环形态):default 租约 = Dispose 无操作,不与外层租约冲突
        using SessionOperationState.SessionOperationLease reentered = state.Enter(state);
        await Assert.That(reentered.Owner).IsNull();
        // 外层仍持租约:门禁保持
        InvalidOperationException? thrown = null;
        try
        {
            _ = state.Enter(new object());
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }
        await Assert.That(thrown).IsNotNull();
    }

    [Test]
    public async Task NonOwnerEntry_WhileOwnerActive_StillThrows()
    {
        SessionOperationState state = new();
        using SessionOperationState.SessionOperationLease leaseA = state.Enter(new object());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = state.Enter(new object());
            await Task.CompletedTask;
        });
    }
}
