using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// 整数主键类型转换回归测试（ITM-587）。
///
/// 根因：源生成器生成的 BindDelete/BindKey 曾用 ((long)key) 直接拆箱，
/// 当 key 是装箱的 int/short/byte 时运行期抛 InvalidCastException。
/// 修复后改用 Convert.ToInt64(key)——兼容装箱类型间转换。
///
/// 这些测试覆盖回归路径——BenchmarkDotNet 的 NextId() 返回 int 时暴露了此 bug。
/// </summary>
public sealed class KeyConversionTests
{
    [Test]
    public async Task GetAsync_LongPk_WithIntKey_Works()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await CreateTableAsync(session);
        var inserted = await session.InsertAsync(new KeyConversionEntity { Name = "int-key" });

        // 核心回归：key 传 int 而非 long——原 ((long)key) 会抛 InvalidCastException
        KeyConversionEntity? found = await session.GetAsync<KeyConversionEntity>((int)inserted.Id);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(inserted.Id);
        await Assert.That(found.Name).IsEqualTo("int-key");
    }

    [Test]
    public async Task GetAsync_LongPk_WithShortKey_Works()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await CreateTableAsync(session);
        var inserted = await session.InsertAsync(new KeyConversionEntity { Name = "short-key" });

        // short 也要支持——ORM 边界应宽松接受所有整数类型
        KeyConversionEntity? found = await session.GetAsync<KeyConversionEntity>((short)inserted.Id);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(inserted.Id);
    }

    [Test]
    public async Task GetAsync_LongPk_WithByteKey_Works()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await CreateTableAsync(session);
        var inserted = await session.InsertAsync(new KeyConversionEntity { Name = "byte-key" });

        // byte 主键场景（小表 ID < 256）
        KeyConversionEntity? found = await session.GetAsync<KeyConversionEntity>((byte)inserted.Id);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(inserted.Id);
    }

    [Test]
    public async Task GetAsync_LongPk_WithNativeLongKey_StillWorks()
    {
        // 反向验证 S3：修复不能破坏原生 long 调用路径
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await CreateTableAsync(session);
        var inserted = await session.InsertAsync(new KeyConversionEntity { Name = "long-key" });

        KeyConversionEntity? found = await session.GetAsync<KeyConversionEntity>(inserted.Id);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(inserted.Id);
    }

    [Test]
    public async Task DeleteAsync_LongPk_WithIntKey_Works()
    {
        // DeleteAsync 也走 BindDelete 路径——同样的修复覆盖
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await CreateTableAsync(session);
        var inserted = await session.InsertAsync(new KeyConversionEntity { Name = "del-int" });

        await session.DeleteAsync<KeyConversionEntity>((int)inserted.Id);
        KeyConversionEntity? found = await session.GetAsync<KeyConversionEntity>(inserted.Id);
        await Assert.That(found).IsNull();
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

    private static async Task CreateTableAsync(DataSession<SqliteProvider> session)
    {
        // 用 FormattableString 执行 DDL——避免暴露内部 CreateCommand API
        await session.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS key_conversion_long_pk (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)");
    }
}

#region Test Entities
[Table("key_conversion_long_pk")]
internal sealed partial class KeyConversionEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = string.Empty;
}
#endregion
