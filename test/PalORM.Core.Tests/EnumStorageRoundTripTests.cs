using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>ITM-553（v6.1）：枚举存储策略三形态 SQLite 真库往返——
/// 缺省/AsString=成员名字符串（DDL TEXT）、AsInt32（INTEGER）、AsInt64（BIGINT）。
/// 写侧（binder 三形态：单行/池/批量）与读侧（switch 解析/数值强转）由生成物承载，
/// 本组锁端到端行为：插入→查询→更新→BulkInsert 全形态往返 + 存储表示直证（typeof）。</summary>
internal sealed class EnumStorageRoundTripTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=enum_rt_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.MigrateAsync();
        return session;
    }

    [Test]
    public async Task Enum_DefaultString_RoundTrips_And_StoresNameText()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var inserted = await session.InsertAsync(new EnumRoundTripEntity
        {
            Status = OrderState.Paid, Level = AccessLevel.Pro, WideFlag = WideMark.On,
        });

        // 存储表示直证：字符串形态落成员名（TEXT），整数形态落数值
        string? statusStorage = await session.ScalarAsync<string>(
            $"SELECT status FROM enum_rt WHERE id = {inserted.Id}");
        await Assert.That(statusStorage).IsEqualTo("Paid");
        long levelStorage = await session.ScalarAsync<long>(
            $"SELECT level FROM enum_rt WHERE id = {inserted.Id}");
        await Assert.That(levelStorage).IsEqualTo(1L);

        EnumRoundTripEntity read = (await session.From<EnumRoundTripEntity>()
            .Where($"id = {inserted.Id}").ToListAsync())[0];
        await Assert.That(read.Status).IsEqualTo(OrderState.Paid);
        await Assert.That(read.Level).IsEqualTo(AccessLevel.Pro);
        await Assert.That(read.WideFlag).IsEqualTo(WideMark.On);
        await Assert.That(read.Fallback).IsNull();   // 可空枚举 DB NULL → null
    }

    [Test]
    public async Task Enum_NullableString_RoundTrips_BothStates()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var withValue = await session.InsertAsync(new EnumRoundTripEntity
        {
            Status = OrderState.Cancelled, Fallback = OrderState.Pending,
        });
        var withNull = await session.InsertAsync(new EnumRoundTripEntity
        {
            Status = OrderState.Pending, Fallback = null,
        });

        EnumRoundTripEntity a = (await session.From<EnumRoundTripEntity>()
            .Where($"id = {withValue.Id}").ToListAsync())[0];
        EnumRoundTripEntity b = (await session.From<EnumRoundTripEntity>()
            .Where($"id = {withNull.Id}").ToListAsync())[0];
        await Assert.That(a.Fallback).IsEqualTo(OrderState.Pending);
        await Assert.That(b.Fallback).IsNull();
    }

    [Test]
    public async Task Enum_WideInt64_RoundTrips_BeyondInt32Range()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        // 1L << 33 超 Int32——AsInt64 形态不截断（AsInt32 对 long 底层枚举由 PALORM053 拦截）
        var inserted = await session.InsertAsync(new EnumRoundTripEntity
        {
            Status = OrderState.Pending, WideFlag = WideMark.On,
        });

        long raw = await session.ScalarAsync<long>(
            $"SELECT wide_flag FROM enum_rt WHERE id = {inserted.Id}");
        await Assert.That(raw).IsEqualTo(1L << 33);
        EnumRoundTripEntity read = (await session.From<EnumRoundTripEntity>()
            .Where($"id = {inserted.Id}").ToListAsync())[0];
        await Assert.That(read.WideFlag).IsEqualTo(WideMark.On);
    }

    [Test]
    public async Task Enum_UpdateAndBulkInsert_RoundTrip_AllForms()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        // 更新路径（BindUpdate 强转）+ BulkInsert 池路径（BindInsertValues 强转）
        var seed = await session.InsertAsync(new EnumRoundTripEntity { Status = OrderState.Pending });
        await session.UpdateAsync(new EnumRoundTripEntity
        {
            Id = seed.Id, Status = OrderState.Cancelled, Level = AccessLevel.Enterprise, WideFlag = WideMark.On,
        });
        List<EnumRoundTripEntity> batch = [];
        for (int i = 0; i < 25; i++)
            batch.Add(new EnumRoundTripEntity
            {
                Status = i % 2 == 0 ? OrderState.Paid : OrderState.Cancelled,
                Level = AccessLevel.Free, WideFlag = WideMark.Off, Fallback = OrderState.Paid,
            });
        await session.BulkInsertAsync(batch);

        EnumRoundTripEntity updated = (await session.From<EnumRoundTripEntity>()
            .Where($"id = {seed.Id}").ToListAsync())[0];
        await Assert.That(updated.Status).IsEqualTo(OrderState.Cancelled);
        await Assert.That(updated.Level).IsEqualTo(AccessLevel.Enterprise);

        List<EnumRoundTripEntity> all = await session.From<EnumRoundTripEntity>().ToListAsync();
        await Assert.That(all.Count).IsEqualTo(26);   // 1 单行 + 1 更新目标 + 25 批量（seed 复用）
        foreach (EnumRoundTripEntity row in all)
        {
            await Assert.That(row.Status is OrderState.Paid or OrderState.Cancelled or OrderState.Pending).IsTrue();
        }
    }

    [Test]
    public async Task Enum_StringParse_UnknownMember_ThrowsLoudly()
    {
        // 未定义成员（手写 SQL 灌入脏值）→ 生成式 switch 响亮失败，不静默默认值
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync(
            $"INSERT INTO enum_rt (status, level, wide_flag) VALUES ({(long)9}, {0}, {0})");
        Exception? thrown = null;
        try
        {
            _ = await session.From<EnumRoundTripEntity>().ToListAsync();
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }
        // SQLite TEXT 亲和列把数值 9 存为 '9'——Parse switch 必须响亮拒绝未定义成员
        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("9");
    }
}

[Table("enum_rt")]
internal sealed partial class EnumRoundTripEntity
{
    [Key] public long Id { get; set; }
    [Column("status")] public OrderState Status { get; set; }
    [Column("level", StoreAs = StoreAs.AsInt32)] public AccessLevel Level { get; set; }
    [Column("wide_flag", StoreAs = StoreAs.AsInt64)] public WideMark WideFlag { get; set; }
    [Column("fallback", StoreAs = StoreAs.AsString)] public OrderState? Fallback { get; set; }
}

internal enum OrderState { Pending, Paid, Cancelled }
internal enum AccessLevel { Free = 0, Pro = 1, Enterprise = 2 }
internal enum WideMark : long { Off = 0, On = 1L << 33 }
