namespace PalORM.Integration.Tests;

public partial class SqlFileTests
{
    [SqlFile("Queries/TestQuery.sql")]
    public static partial string TestQuery();

    [SqlFile("Queries/ProviderQuery.sql", Provider = "Sqlite")]
    public static partial string ProviderQuery();

    [SqlFile("Queries/ProviderQuery.sql", Provider = "pg")]
    public static partial string ProviderQueryPg();

    [Test]
    public async Task SqlFile_EmbedsQueryAtCompileTime()
    {
        string query = TestQuery();
        await Assert.That(query).IsNotNullOrEmpty();
        await Assert.That(query).Contains("SELECT COUNT(*)");
        await Assert.That(query).Contains("sqlite_master");
    }

    [Test]
    public async Task SqlFile_ProviderBranch_ResolvesCorrectProvider()
    {
        // [SqlFile(Provider="Sqlite")] 只提取 -- @sqlite 段
        string sqliteQuery = ProviderQuery();
        await Assert.That(sqliteQuery).Contains("SELECT 'sqlite'");
        await Assert.That(sqliteQuery).Contains("FROM dual");
        await Assert.That(sqliteQuery).DoesNotContain("SELECT current_database");
        await Assert.That(sqliteQuery).DoesNotContain("SELECT DATABASE()");
    }

    [Test]
    public async Task SqlFile_ProviderBranch_PgResolvesPgSection()
    {
        // [SqlFile(Provider="pg")] 只提取 -- @pg 段
        string pgQuery = ProviderQueryPg();
        await Assert.That(pgQuery).Contains("SELECT current_database()");
        await Assert.That(pgQuery).Contains("FROM dual");
        await Assert.That(pgQuery).DoesNotContain("SELECT 'sqlite'");
        await Assert.That(pgQuery).DoesNotContain("SELECT DATABASE()");
    }
}
