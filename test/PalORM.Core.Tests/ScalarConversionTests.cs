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

    [Test]
    public async Task MaxAsync_NullableGeneric_UnwrapsUnderlyingType()
    {
        // ITM-711(r20)：TValue 为可空值类型时直接 Convert.ChangeType 会抛 InvalidCastException
        //（实测 Convert.ChangeType(5L, typeof(long?)) 失败）——须先取底层类型（与 ScalarAsync 一致）。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE scalar_agg (Id INTEGER PRIMARY KEY, amount REAL NOT NULL)");
        await session.ExecuteAsync($"INSERT INTO scalar_agg (Id, amount) VALUES (7, 1.5)");

        long? max = await session.MaxAsync<ScalarAggEntity, long?>($"Id");

        await Assert.That(max).IsEqualTo(7L);
    }

    [Test]
    public async Task MinAsync_NullableGeneric_UnwrapsUnderlyingType()
    {
        // ITM-711(r20)：MinAsync 同型缺口
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE scalar_agg (Id INTEGER PRIMARY KEY, amount REAL NOT NULL)");
        await session.ExecuteAsync($"INSERT INTO scalar_agg (Id, amount) VALUES (7, 1.5)");

        long? min = await session.MinAsync<ScalarAggEntity, long?>($"Id");

        await Assert.That(min).IsEqualTo(7L);
    }

    [Test]
    public async Task ToListAsync_HugeTake_DoesNotPreallocateUnbounded()
    {
        // ITM-712(r20)：Take 是结果上界而非预期行数——曾直接作为 List 预分配容量，
        // Take(1_000_000_000) 会立即触发巨量分配/OOM。封顶后必须正常返回。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE scalar_agg (Id INTEGER PRIMARY KEY, amount REAL NOT NULL)");
        await session.ExecuteAsync($"INSERT INTO scalar_agg (Id, amount) VALUES (1, 1.0), (2, 2.0)");

        List<ScalarAggEntity> rows = await session.From<ScalarAggEntity>()
            .OrderBy(e => e.Id)
            .Take(1_000_000_000)
            .ToListAsync();

        await Assert.That(rows.Count).IsEqualTo(2);
    }
}

#region Test Entities
[Table("scalar_agg")]
internal sealed partial class ScalarAggEntity
{
    [Key]
    public long Id { get; set; }
    [Column("amount")]
    public decimal Amount { get; set; }
}
#endregion
