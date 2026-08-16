namespace PalORM.Core.Tests;

public sealed class SqlDialectTests
{
    [Test]
    public async Task AllValues_Defined()
    {
        var values = Enum.GetValues<SqlDialect>();
        await Assert.That(values.Length).IsEqualTo(3);
        await Assert.That(values).Contains(SqlDialect.PostgreSql);
        await Assert.That(values).Contains(SqlDialect.MySql);
        await Assert.That(values).Contains(SqlDialect.Sqlite);
    }
}
