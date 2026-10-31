using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>原始 SQL 入口的会话连接态标脏回归（P1-3）。
/// <para><c>QueryBuilder.Raw</c> 与 <c>QueryMultipleAsync</c> 此前只调用 Provider 钩子，
/// 而 PG/MySQL 的默认钩子是空操作，池化会话不会在下一租期复位 GUC/角色/会话变量。</para></summary>
public sealed class RawSqlConnectionStateTests
{
    [Test]
    public async Task Raw_MarksSessionConnectionStateDirty()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        await Assert.That(session.ConnectionStateMayBeDirtyForTests).IsFalse();

        _ = session.From<RawStateProbe>().Raw("WHERE 1=1");

        await Assert.That(session.ConnectionStateMayBeDirtyForTests).IsTrue();
    }

    [Test]
    public async Task QueryMultiple_MarksSessionConnectionStateDirty()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        await using GridReader grid = await session
            .From<RawStateProbe>()
            .QueryMultipleAsync($"SELECT 1");

        await Assert.That(session.ConnectionStateMayBeDirtyForTests).IsTrue();
    }

    private static Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = $"Data Source=rawdirty_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
        });
}

[Table("raw_state_probe")]
internal sealed class RawStateProbe
{
    [Key] public long Id { get; set; }

    [Column("name")] public string Name { get; set; } = "";
}
