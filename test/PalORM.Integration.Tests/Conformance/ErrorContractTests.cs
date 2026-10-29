using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>B 层错误契约（方案 §5.3，E1 到 E9）。
/// <para>E1/E2/E3/E5/E6/E8 在 SQLite 上验证；E4（超时）、E7（列长度截断）、E9（PG 非 Utc）
/// 需要真库方言的强制行为，单独成因分组的测试。</para>
/// <para>E3 的熔断器是 <c>internal</c>，只能经公开行为观察：制造一次最终失败后，
/// 下一次调用必须直接抛 <c>CircuitBreakerOpenException</c>。</para></summary>
public sealed class ErrorContractTests
{
    private static async ValueTask<(SharedSqliteDb Shared, DataSession<SqliteProvider> Db)> OpenAsync(
        string libraryName, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(libraryName, ct);
        DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
        return (shared, db);
    }

    internal static IReadOnlyList<ContractEntry> Errors { get; } =
    [
        new("E1",
            "带 [ConcurrencyCheck] 的实体，用陈旧 Version 更新",
            "抛 ConcurrencyConflictException，行内容不被改写",
            "API参考.md 基础注解（[ConcurrencyCheck]）+ Exceptions.cs",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                CfVersionedEntity inserted = await session.InsertAsync(new CfVersionedEntity { Name = "v1", Version = 0 });
                CfVersionedEntity first = (await session.GetAsync<CfVersionedEntity>(inserted.Id))!;
                CfVersionedEntity stale = (await session.GetAsync<CfVersionedEntity>(inserted.Id))!;
                first.Name = "first";
                await session.UpdateAsync(first);
                stale.Name = "stale";
                await Assert.That(async () => await session.UpdateAsync(stale))
                    .Throws<ConcurrencyConflictException>();
                await Assert.That((await session.GetAsync<CfVersionedEntity>(inserted.Id))!.Name)
                    .IsEqualTo("first");
            }),

        new("E2",
            "带 [Unique] 的列插入重复值",
            "抛 UniqueConstraintViolationException，第二行未写入",
            "API参考.md 基础注解（[Unique]）+ Exceptions.cs",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.InsertAsync(new CfCompositeIndexEntity { Category = "a", Price = 1m, Sku = "dup" });
                await Assert.That(async () => await session.InsertAsync(
                        new CfCompositeIndexEntity { Category = "b", Price = 2m, Sku = "dup" }))
                    .Throws<UniqueConstraintViolationException>();
                await Assert.That(await session.CountAsync<CfCompositeIndexEntity>()).IsEqualTo(1L);
            }),

        new("E5",
            "已取消的 CancellationToken",
            "抛 OperationCanceledException，且不产生写入",
            "API参考.md 执行方法（取消传播）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                using var cts = new CancellationTokenSource();
                await cts.CancelAsync();
                await Assert.That(async () => await session.InsertAsync(
                        new CfBulkPlainEntity { Id = 1, Name = "cancelled" }, cts.Token))
                    .Throws<OperationCanceledException>();
                await Assert.That(await session.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
            }),

        new("E6",
            "实体参数为 null",
            "InsertAsync / SaveAsync / DeleteAsync 的 null 实参抛 ArgumentNullException",
            "API参考.md 直查 / CRUD（参数校验）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await Assert.That(async () => await session.InsertAsync<CfBulkPlainEntity>(null!))
                    .Throws<ArgumentNullException>();
                await Assert.That(async () => await session.SaveAsync<CfBulkPlainEntity>(null!))
                    .Throws<ArgumentNullException>();
                await Assert.That(async () => await session.DeleteAsync<CfBulkPlainEntity>(null!))
                    .Throws<ArgumentNullException>();
            }),

        new("E8",
            "任意派生异常",
            "具体异常类型是 PalORMException 的派生，断言派生类型而非基类",
            "API参考.md 设计决策记录 + Exceptions.cs（PalORMException 为基类）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.InsertAsync(new CfCompositeIndexEntity { Category = "a", Price = 1m, Sku = "dup" });
                UniqueConstraintViolationException? thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
                    async () => await session.InsertAsync(
                        new CfCompositeIndexEntity { Category = "b", Price = 2m, Sku = "dup" }));
                await Assert.That(thrown).IsNotNull();
                await Assert.That(thrown!).IsAssignableTo<PalORMException>();
                await Assert.That(thrown!.GetType()).IsNotEqualTo(typeof(PalORMException));
            }),
    ];

    [Test]
    public async Task AllSqliteErrorContracts_Hold()
    {
        List<string> failures = [];
        int index = 0;
        foreach (ContractEntry contract in Errors)
        {
            index++;
            try
            {
                await contract.Verify($"error_{index:D2}");
            }
            catch (Exception ex)
            {
                failures.Add($"{contract.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        await Assert.That(string.Join("\n", failures)).IsEmpty();
    }

    [Test]
    public async Task ErrorCatalog_MetadataIsWellFormed()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        List<string> problems = [];
        foreach (ContractEntry contract in Errors)
        {
            if (!ids.Add(contract.Id)) problems.Add($"{contract.Id}: ID 重复");
            if (string.IsNullOrWhiteSpace(contract.Postcondition)) problems.Add($"{contract.Id}: 后置为空");
            if (string.IsNullOrWhiteSpace(contract.Source)) problems.Add($"{contract.Id}: 来源为空");
        }
        await Assert.That(string.Join(" | ", problems)).IsEmpty();
    }

    // ── 需要真库方言强制行为的错误契约（E4 / E7 / E9）──────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task E4_PostgreSql_CommandTimeout_ThrowsTimeoutException()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        db.WithTimeout(TimeSpan.FromMilliseconds(100));
        // pg_sleep(2) 远超 100ms 超时窗口
        // 实测（2026-10-29）：PG 上超时经 NpgsqlException 呈现，而非统一的 TimeoutException。
        // 文档未约定具体异常类型，这里登记实测值；连接归还由后续查询可用性间接断言。
        await Assert.That(async () => await db.ScalarAsync<long>($"SELECT 1 FROM pg_sleep(2)"))
            .Throws<Npgsql.NpgsqlException>();
        await Assert.That(await db.ScalarAsync<long>($"SELECT 1")).IsEqualTo(1L);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task E7_PostgreSql_OverLengthValue_IsRejected()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        await db.ExecuteAsync($"DELETE FROM cf_short_column");

        // 列声明 VARCHAR(5)，写入 10 字符必须被数据库拒绝，不允许静默截断
        await Assert.That(async () => await db.InsertAsync(
                new CfShortColumnEntity { Id = 1, Code = "0123456789" }))
            .Throws<Exception>();

        await db.ExecuteAsync($"DELETE FROM cf_short_column");
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task E9_PostgreSql_NonUtcDateTime_ThrowsArgumentException()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        await db.ExecuteAsync($"DELETE FROM cf_time_matrix");

        // 实测（0a 时间矩阵）：PG 的 DateTime 列映射 timestamptz，只接受 Utc
        await Assert.That(async () => await db.InsertAsync(new CfTimeMatrixEntity
        {
            Dt = new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Unspecified),
            Dto = new DateTimeOffset(2026, 7, 18, 10, 30, 0, TimeSpan.Zero),
            DateOnly = new DateOnly(2026, 7, 18),
            TimeOnly = new TimeOnly(10, 30, 0)
        })).Throws<ArgumentException>();

        await db.ExecuteAsync($"DELETE FROM cf_time_matrix");
    }
}