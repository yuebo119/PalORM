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

    [Test]
    public async Task GetAsync_AcrossPromotionThreshold_ConvertedKeyFormsStillWork()
    {
        // C3（2026-10-11）：复用路径键值换算改经生成器 ConvertDeleteKey（与 BindDelete 的
        // @p0 同一真源 DeleteKeyValueExpression）——跨晋升阈值（第 3 次起）后异型键
        // （int/short/byte → long PK）必须与新建路径同判定。本用例锁定两条路径的等价性，
        // 是上面四条新建路径用例在复用分支的延伸。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await CreateTableAsync(session);
        long idA = (await session.InsertAsync(new KeyConversionEntity { Name = "reuse-a" })).Id;
        long idB = (await session.InsertAsync(new KeyConversionEntity { Name = "reuse-b" })).Id;

        // 前两次走新建路径（BindGeneratedKeyParameter 绑定），第 3 次起晋升为复用路径
        //（ConvertDeleteKey 就地写 Value）
        _ = await session.GetAsync<KeyConversionEntity>(idA);
        _ = await session.GetAsync<KeyConversionEntity>(idB);

        KeyConversionEntity? byInt = await session.GetAsync<KeyConversionEntity>((int)idA);
        await Assert.That(byInt).IsNotNull();
        await Assert.That(byInt!.Name).IsEqualTo("reuse-a");

        KeyConversionEntity? byShort = await session.GetAsync<KeyConversionEntity>((short)idA);
        await Assert.That(byShort).IsNotNull();
        await Assert.That(byShort!.Id).IsEqualTo(idA);

        KeyConversionEntity? byByte = await session.GetAsync<KeyConversionEntity>(checked((byte)idB));
        await Assert.That(byByte).IsNotNull();
        await Assert.That(byByte!.Name).IsEqualTo("reuse-b");

        // 原生 long 键在复用路径同样保持
        KeyConversionEntity? byLong = await session.GetAsync<KeyConversionEntity>(idB);
        await Assert.That(byLong!.Id).IsEqualTo(idB);
    }

    [Test]
    public async Task ConvertDeleteKey_IsWiredInRegistry_ForSingleColumnPrimaryKey()
    {
        // C3：注册表层契约——单列主键实体的聚合元数据必须携带转换委托（晋升门禁消费）。
        // 复合主键/旧生成器程序集为 null 属设计回退，此处只锁真源形态不被未来发射变更静默丢弃。
        // 2026-10-18 审计：空值断言升级为可调用性断言（T14）——委托存在但不可调用同为断链。
        Func<object, object?>? convert = PalORM_Runtime.CrudMetadatas[typeof(KeyConversionEntity)].ConvertDeleteKey;
        object? converted = convert?.Invoke(7L);
        await Assert.That(converted).IsEqualTo(7L);
    }

    [Test]
    public async Task ConvertDeleteKey_SameTypeBoxedKey_ReusesCallerBox()
    {
        // R2-A2（2026-10-13 微项包）：生成器 identity 快路径——同型装箱键（long 主键实体收
        // long 键）复用调用方装箱实例（ReferenceEquals），异型键照走 Convert 归一（新箱）。
        // 锁定的是"非槽路径 −24B/绑定"的机制本体：Convert.ToInt64(object) 必解箱重箱，
        // identity 分支返回原始引用零新分配。
        Func<object, object?>? convert = PalORM_Runtime.CrudMetadatas[typeof(KeyConversionEntity)].ConvertDeleteKey;
        await Assert.That(convert).IsNotNull();

        long keyValue = 42L;
        object boxedKey = keyValue;   // 调用点装箱（唯一一箱）
        object? converted = convert!(boxedKey);
        await Assert.That(converted).IsEqualTo(42L);
        await Assert.That(ReferenceEquals(converted, boxedKey)).IsTrue();   // 同一箱复用

        // 异型键（int → long 主键）照走 Convert 归一：值正确、产新实例
        object? crossType = convert!(42);
        await Assert.That(crossType).IsEqualTo(42L);
        await Assert.That(ReferenceEquals(crossType, boxedKey)).IsFalse();
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
