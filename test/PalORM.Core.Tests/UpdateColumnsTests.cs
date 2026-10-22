using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

[Table("partial_update_rows")]
internal sealed partial class PartialUpdateRow
{
    [Key(AutoIncrement = false)][Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("score")] public long score { get; set; }
    [ConcurrencyCheck][Column("version")] public long version { get; set; }
}

[TenantAware]
[Table("partial_update_tenant_rows")]
internal sealed partial class PartialUpdateTenantRow
{
    [Key(AutoIncrement = false)][Column("id")] public long id { get; set; }
    [Column("tenant_id")] public long tenant_id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("score")] public long score { get; set; }
}

/// <summary>实体级列子集更新：只 SET 选中列、未选列保持原值；版本列照常递增与冲突检测；
/// 租户会话追加租户谓词；主键/并发令牌等不可更新列在访问数据库前拒绝。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "连接生命周期移交 DataSession；keeper 由测试释放。")]
public sealed class UpdateColumnsTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeper;
        internal DataSession<SqliteProvider> Session { get; }

        private Fixture(SqliteConnection keeper, DataSession<SqliteProvider> session)
        {
            _keeper = keeper;
            Session = session;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            string cs = $"Data Source=updatecols_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keeper = new SqliteConnection(cs);
            await keeper.OpenAsync().ConfigureAwait(false);
            await using (DbCommand init = keeper.CreateCommand())
            {
                init.CommandText =
                    "CREATE TABLE partial_update_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER NOT NULL, version INTEGER NOT NULL);"
                    + "CREATE TABLE partial_update_tenant_rows (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, name TEXT NOT NULL, score INTEGER NOT NULL);";
                await init.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var options = new DbOptions { ConnectionString = cs };
            var session = await DataSession<SqliteProvider>.CreateAsync(options).ConfigureAwait(false);
            return new Fixture(keeper, session);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().ConfigureAwait(false);
            await _keeper.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task UpdateColumns_OnlySelectedColumn_AndIncrementsVersion()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DataSession<SqliteProvider> session = fixture.Session;
        await session.InsertAsync(new PartialUpdateRow { id = 1, name = "before", score = 42, version = 1 });

        var entity = new PartialUpdateRow { id = 1, name = "after", score = 999, version = 1 };
        int affected = await session.UpdateColumnsAsync(entity, x => x.name);
        await Assert.That(affected).IsEqualTo(1);
        await Assert.That(entity.version).IsEqualTo(2L);

        PartialUpdateRow? stored = await session.GetAsync<PartialUpdateRow>(1L);
        await Assert.That(stored?.name).IsEqualTo("after");
        await Assert.That(stored?.score).IsEqualTo(42L);
        await Assert.That(stored?.version).IsEqualTo(2L);
    }

    [Test]
    public async Task UpdateColumns_DuplicateSelection_IsDeduplicated()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DataSession<SqliteProvider> session = fixture.Session;
        await session.InsertAsync(new PartialUpdateRow { id = 1, name = "before", score = 42, version = 1 });
        var entity = new PartialUpdateRow { id = 1, name = "after", score = 0, version = 1 };
        int affected = await session.UpdateColumnsAsync(entity, [x => x.name, x => x.name]);
        await Assert.That(affected).IsEqualTo(1);
        PartialUpdateRow? stored = await session.GetAsync<PartialUpdateRow>(1L);
        await Assert.That(stored?.name).IsEqualTo("after");
    }

    [Test]
    public async Task UpdateColumns_StaleVersion_ThrowsConflict()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DataSession<SqliteProvider> session = fixture.Session;
        await session.InsertAsync(new PartialUpdateRow { id = 1, name = "before", score = 42, version = 1 });
        var first = new PartialUpdateRow { id = 1, name = "first", score = 0, version = 1 };
        await session.UpdateColumnsAsync(first, x => x.name);
        var stale = new PartialUpdateRow { id = 1, name = "stale", score = 0, version = 1 };
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            async () => await session.UpdateColumnsAsync(stale, x => x.name));
    }

    [Test]
    public async Task UpdateColumns_NonUpdatableColumnOrEmptySelection_Throws()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DataSession<SqliteProvider> session = fixture.Session;
        var entity = new PartialUpdateRow { id = 1, name = "x", score = 0, version = 1 };
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await session.UpdateColumnsAsync(entity, x => x.id));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await session.UpdateColumnsAsync(entity, []));
    }

    [Test]
    public async Task UpdateColumns_TenantSession_OnlyUpdatesOwnTenant()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DataSession<SqliteProvider> session = fixture.Session;
        await session.InsertAsync(new PartialUpdateTenantRow { id = 1, tenant_id = 1, name = "t1-before", score = 10 });
        await session.InsertAsync(new PartialUpdateTenantRow { id = 2, tenant_id = 2, name = "t2-before", score = 20 });

        var row = new PartialUpdateTenantRow { id = 1, tenant_id = 1, name = "t1-after", score = 99 };
        int affected = await session.WithTenant(1L).UpdateColumnsAsync(row, x => x.name);
        await Assert.That(affected).IsEqualTo(1);

        PartialUpdateTenantRow? tenant1 = await session.GetAsync<PartialUpdateTenantRow>(1L);
        await Assert.That(tenant1?.name).IsEqualTo("t1-after");
        await Assert.That(tenant1?.score).IsEqualTo(10L);

        session.WithTenant(2L);
        PartialUpdateTenantRow? tenant2 = await session.GetAsync<PartialUpdateTenantRow>(2L);
        await Assert.That(tenant2?.name).IsEqualTo("t2-before");
        await Assert.That(tenant2?.score).IsEqualTo(20L);
    }
}
