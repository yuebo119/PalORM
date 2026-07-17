using PalORM.Sqlite;

namespace PalORM.Core.Tests;

public sealed class ScalarConversionTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

    [Test]
    public async Task ScalarAsync_ExactTypeMatch_ReturnsValue()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        long count = await session.ScalarAsync<long>($"SELECT 42");

        await Assert.That(count).IsEqualTo(42L);
    }

    [Test]
    public async Task ScalarAsync_TypeMismatch_ConvertsInsteadOfSilentDefault()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        // SQLite COUNT/整型字面量返回 long；请求 int 应转换为 42 而非静默 0
        int count = await session.ScalarAsync<int>($"SELECT 42");

        await Assert.That(count).IsEqualTo(42);
    }

    [Test]
    public async Task ScalarAsync_DecimalRequested_ConvertsFromDouble()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        decimal value = await session.ScalarAsync<decimal>($"SELECT 1.5");

        await Assert.That(value).IsEqualTo(1.5m);
    }

    [Test]
    public async Task ScalarAsync_NullResult_ReturnsDefault()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        long? value = await session.ScalarAsync<long?>($"SELECT NULL");

        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task ScalarAsync_InconvertibleType_ThrowsInsteadOfSilentDefault()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        // 文本无法转换为 int：必须明确失败，不得静默返回 0
        await Assert.That(async () => await session.ScalarAsync<int>($"SELECT 'not-a-number'"))
            .Throws<FormatException>();
    }
}
