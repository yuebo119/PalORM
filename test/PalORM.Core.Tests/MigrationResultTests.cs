using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>迁移结果对象与兼容入口回归（2026-10-31）。</summary>
public sealed class MigrationResultTests
{
    [Test]
    public async Task MigrateWithResult_ReturnsStructuredResult_AndLegacyEntryPointStillWorks()
    {
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions
            {
                ConnectionString = $"Data Source=migresult_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            });

        MigrationResult result = await session.MigrateWithResultAsync();

        await Assert.That(result.Transactional).IsTrue();
        await Assert.That(result.RolledBack).IsFalse();
        await Assert.That(result.Tables.Any(static table => table == "raw_state_probe")).IsTrue();
        await Assert.That(ReferenceEquals(session.LastMigrationResult, result)).IsTrue();

        // 兼容入口保留：重复迁移幂等。
        await session.MigrateAsync();
    }
}
