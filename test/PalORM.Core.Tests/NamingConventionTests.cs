namespace PalORM.Core.Tests;

public sealed class NamingConventionTests
{
    [Test]
    public async Task Default_IsNone()
    {
        NamingConvention value = default;
        await Assert.That(value).IsEqualTo(NamingConvention.None);
    }
}
