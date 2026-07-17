using PalORM.Sqlite;

namespace PalORM.Core.Tests;

[Table("colorder")]
internal sealed partial class ColumnOrderEntity
{
    [Key]
    public long Id { get; set; }
    [Column("first_name")]
    public string FirstName { get; set; } = "";
    [Column("last_name")]
    public string LastName { get; set; } = "";
}

public sealed class QueryColumnOrderValidationTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync(bool validate = true)
        => await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:",
            ValidateQueryColumnOrder = validate
        });

    private static async Task SeedAsync(DataSession<SqliteProvider> session)
    {
        // 不用 MigrateAsync：本测试程序集含其他测试注册的假 fragment（CreateTableSql="DDL"）
        await session.ExecuteAsync(
            $"CREATE TABLE colorder (Id INTEGER PRIMARY KEY AUTOINCREMENT, first_name TEXT NOT NULL, last_name TEXT NOT NULL)");
        await session.InsertAsync(new ColumnOrderEntity { FirstName = "Ada", LastName = "Lovelace" });
    }

    [Test]
    public async Task QueryAsync_DeclarationOrder_Passes()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await SeedAsync(session);

        var rows = await session.QueryAsync<ColumnOrderEntity>(
            $"SELECT Id, first_name, last_name FROM colorder");

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].FirstName).IsEqualTo("Ada");
    }

    [Test]
    public async Task QueryAsync_SwappedSameTypeColumns_ThrowsInsteadOfSilentSwap()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await SeedAsync(session);

        // first_name/last_name 同为 TEXT：修复前静默交换数据，ADR-A 后明确失败
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.QueryAsync<ColumnOrderEntity>(
                $"SELECT Id, last_name, first_name FROM colorder"));

        await Assert.That(ex!.Message).Contains("column order mismatch");
    }

    [Test]
    public async Task QueryAsync_ValidationDisabled_AllowsCustomOrder()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync(validate: false);
        await SeedAsync(session);

        // 关闭校验后调用方自行保证语义——数据按 ordinal 装入（此处刻意错位以证明未拦截）
        var rows = await session.QueryAsync<ColumnOrderEntity>(
            $"SELECT Id, last_name, first_name FROM colorder");

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].FirstName).IsEqualTo("Lovelace");
    }

    [Test]
    public async Task QueryAsyncEnumerable_SwappedColumns_Throws()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await SeedAsync(session);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (ColumnOrderEntity _ in session.QueryAsyncEnumerable<ColumnOrderEntity>(
                $"SELECT Id, last_name, first_name FROM colorder"))
            {
            }
        });
    }

    [Test]
    public async Task QueryAsync_SelectStar_MatchesGeneratedDdlOrder()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await SeedAsync(session);

        // 建表列序 = 实体声明序 → SELECT * 物理列序通过校验
        var rows = await session.QueryAsync<ColumnOrderEntity>($"SELECT * FROM colorder");

        await Assert.That(rows[0].LastName).IsEqualTo("Lovelace");
    }
}
