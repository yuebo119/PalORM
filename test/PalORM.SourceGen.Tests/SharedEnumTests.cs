namespace PalORM.SourceGen.Tests;

/// <summary>Core 与 SourceGen 共享枚举的取值一致性（2026-10-31）。
/// 两个程序集各自编译同一份 <c>src/Shared/PalORMAnnotations.cs</c>：Core 暴露 public
/// <c>PalORM.DeleteAction</c>，SourceGen 内部用 <c>PalORM.SourceGenDeleteAction</c> 避免
/// CS0433 类型歧义。共享文件保证同源，本测试锁定两侧数值契约不被单独改动。</summary>
internal sealed class SharedEnumTests
{
    [Test]
    public async Task DeleteAction_Values_MatchBetweenCoreAndSourceGen()
    {
        int noAction = (int)SourceGenDeleteAction.NoAction;
        int cascade = (int)SourceGenDeleteAction.Cascade;
        int setNull = (int)SourceGenDeleteAction.SetNull;
        int restrict = (int)SourceGenDeleteAction.Restrict;

        await Assert.That(noAction).IsEqualTo((int)PalORM.DeleteAction.NoAction);
        await Assert.That(cascade).IsEqualTo((int)PalORM.DeleteAction.Cascade);
        await Assert.That(setNull).IsEqualTo((int)PalORM.DeleteAction.SetNull);
        await Assert.That(restrict).IsEqualTo((int)PalORM.DeleteAction.Restrict);
    }
}
