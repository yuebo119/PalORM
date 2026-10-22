namespace PalORM.Core.Tests;

/// <summary>P3-15：v4.6 之前生成器程序集的 4 参 <see cref="CrudBindings"/> ctor 兼容 overload。
/// <para>该代 ctor 的 bindInsert 无 paramOffset，且无 BindInsertValues。旧模型程序集按旧签名
/// 调用时若没有这个 overload 会 MissingMethodException（可选参数只是源码级语法，
/// 不参与二进制重载解析）。</para></summary>
internal sealed class CrudBindingsLegacyOverloadTests
{
    [Test]
    public async Task LegacyFourArgCtor_WrapsOffsetlessBinder_AndLeavesOptionalBindersNull()
    {
        int calls = 0;
        object? seen = null;
        var bindings = new CrudBindings(
            (cmd, entity) =>
            {
                _ = cmd;
                calls++;
                seen = entity;
            },
            (_, _) => { },
            (_, _) => { },
            new object());

        // 包装后的委托接受偏移参数（旧代生成器不发射批量偏移绑定，忽略偏移是正确语义）
        bindings.BindInsert(null!, "row", 7);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(seen).IsEqualTo("row");
        await Assert.That(bindings.BindInsertValues).IsNull();
        await Assert.That(bindings.BindUpsert).IsNotNull();
        await Assert.That(bindings.BindUpdate).IsNotNull();
    }
}
