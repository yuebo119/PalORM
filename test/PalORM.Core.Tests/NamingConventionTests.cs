namespace PalORM.Core.Tests;

public sealed class NamingConventionTests
{
    [Test]
    public async Task None_LeavesNamesUnchanged()
    {
        // r19/T-P3-17：原 default==None 是常量间恒真比较——改行为断言（ApplyNaming 契约）
        var opts = new DbOptions { ConnectionString = "x", NamingConvention = NamingConvention.None };

        await Assert.That(opts.ApplyNaming("OrderId")).IsEqualTo("OrderId");
        await Assert.That(opts.ApplyNaming("created_at")).IsEqualTo("created_at");
    }
}
