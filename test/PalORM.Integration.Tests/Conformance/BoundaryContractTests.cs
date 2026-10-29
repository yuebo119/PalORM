using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>B 层边界契约（方案 §5.5，B1 到 B11）。
/// <para>B8（超长字符串截断）需要真库的列长度强制行为、B9（Include 空子集合）待按文档确认签名、
/// B12（BulkUpdateBatch 对并发令牌实体）是编译期 PALORM031 拦截、B13（ToPage 默认降序）已由
/// 能力契约 C-PAGE-01 覆盖，这四条不在本类执行，边界说明见方案 §5.5。</para></summary>
public sealed class BoundaryContractTests
{
    private static async ValueTask<(SharedSqliteDb Shared, DataSession<SqliteProvider> Db)> OpenAsync(
        string libraryName, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(libraryName, ct);
        DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
        return (shared, db);
    }

    private static CfBulkPlainEntity Plain(long id, string name = "n", long amount = 1)
        => new() { Id = id, Name = name, Amount = amount };

    internal static IReadOnlyList<ContractEntry> Boundaries { get; } =
    [
        new("B1",
            "WhereIn 传入空集合",
            "生成恒假条件，执行返回 0 行且不抛异常（IN () 是非法 SQL）",
            "API参考.md 查询构建器（WhereIn 源码注明 1=0）+ QueryBuilder.cs:315",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2)]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .WhereIn(e => e.Id, Array.Empty<long>()).ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(0);
            }),

        new("B2",
            "WhereNotIn 传入空集合",
            "no-op：结果与不加该条件等价（全量）",
            "API参考.md 查询构建器（WhereNotIn 源码注明 no-op）+ QueryBuilder.cs:315",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2)]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .WhereNotIn(e => e.Id, Array.Empty<long>()).ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(2);
            }),

        new("B3",
            "WhereIn 传入 501 个值（超过 500 的切批阈值）",
            "切批后结果等于单批语义：只返回真实存在的键",
            "API参考.md 查询构建器（超 500 按批切分 OR 组合）+ QueryBuilder.cs:315",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3)]);
                long[] keys = [.. Enumerable.Range(1, 501).Select(i => (long)i)];
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .WhereIn(e => e.Id, keys).ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(3);
            }),

        new("B4",
            "Take(0)",
            "实测（2026-10-29）：抛 ArgumentOutOfRangeException（要求正数），不静默返回 0 行",
            "API参考.md 查询构建器（Take）+ 实测",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2)]);
                await Assert.That(async () => await session.From<CfBulkPlainEntity>().Take(0).ToListAsync())
                    .Throws<ArgumentOutOfRangeException>();
            }),

        new("B5",
            "Skip 超过总行数",
            "返回空集，不抛异常",
            "API参考.md 查询构建器（Skip）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2)]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .Skip(999).ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(0);
            }),

        new("B6",
            "pageSize 2 连续取三页（末页不足一页）",
            "各页并集等于全量，页间无重复，末页 1 行",
            "API参考.md 执行方法（ToPageAsync 键集续页）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3), Plain(4), Plain(5)]);

                (List<CfBulkPlainEntity> page1, long total) =
                    await session.From<CfBulkPlainEntity>().ToPageAsync(2, e => e.Id, descending: false);
                (List<CfBulkPlainEntity> page2, _) = await session.From<CfBulkPlainEntity>()
                    .ToPageAsync(2, e => e.Id, lastValue: page1[^1].Id, descending: false);
                (List<CfBulkPlainEntity> page3, _) = await session.From<CfBulkPlainEntity>()
                    .ToPageAsync(2, e => e.Id, lastValue: page2[^1].Id, descending: false);

                HashSet<long> union = [.. page1.Select(e => e.Id), .. page2.Select(e => e.Id), .. page3.Select(e => e.Id)];
                await Assert.That(total).IsEqualTo(5L);
                await Assert.That(union.Count).IsEqualTo(5);
                await Assert.That(page3.Count).IsEqualTo(1);
            }),

        new("B7",
            "BulkDeleteAsync 传入 null 键集合",
            "抛 ArgumentNullException，不静默当作空集合",
            "API参考.md 写入 / 批量（参数校验）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await Assert.That(async () => await session.BulkDeleteAsync<CfBulkPlainEntity, long>(null!))
                    .Throws<ArgumentNullException>();
            }),

        new("B10",
            "ForUpdate 在 SQLite 上调用",
            "构建期不拒绝；执行期抛 SqliteException 语法错误（ITM-639 已登记：SQLite 不支持 FOR UPDATE）",
            "QueryBuilder.cs:592/601（ITM-639 源码登记）+ 实测",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2)]);
                // 构建期不拒绝（DryRun 可预览），执行期由数据库报错
                await Assert.That(session.From<CfBulkPlainEntity>().ForUpdate().AsDryRun().Sql)
                    .Contains("FOR UPDATE");
                await Assert.That(async () => await session.From<CfBulkPlainEntity>().ForUpdate().ToListAsync())
                    .Throws<Microsoft.Data.Sqlite.SqliteException>();
            }),

        new("B9",
            "Include 关联的子表无匹配行",
            "Include 生成 INNER JOIN：无匹配的主表行被过滤（不是 LEFT JOIN 语义），只返回匹配行",
            "QueryBuilder.cs:502（源码注明按外键/主键 INNER JOIN）+ 实测",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                // 主表两行（Id 1 与 3），子表只有 Id 1：Id 3 无匹配
                await session.BulkInsertAsync([Plain(1, "matched", 1), Plain(3, "orphan", 3)]);
                await session.BulkInsertAsync(
                [
                    new CfCompositeIndexEntity { Id = 1, Category = "c", Price = 1m, Sku = "sku-1" }
                ]);

                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .Include<CfCompositeIndexEntity>(b => b.Id, c => c.Id)
                    .ToListAsync();

                await Assert.That(rows.Count).IsEqualTo(1);
                await Assert.That(rows[0].Id).IsEqualTo(1L);
            }),

        new("B8",
            "SQLite 上写入 4000 字符到 TEXT 列",
            "不截断、不抛异常，读回长度与写入一致（SQLite 的 VARCHAR(n) 不强制长度；真库强制行为见 E7）",
            "API参考.md 基础注解（Length）+ 实测 SQLite 方言",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                string longText = new('x', 4000);
                await session.InsertAsync(Plain(1, longText));
                CfBulkPlainEntity row = (await session.GetAsync<CfBulkPlainEntity>(1L))!;
                await Assert.That(row.Name.Length).IsEqualTo(4000);
            }),

        new("B11",
            "自增主键实体经 BulkInsertAsync 写入",
            "不回填自增主键（实体 Id 保持 0），需要回填时改用 BulkMergeAsync",
            "API参考.md 写入 / 批量 + 实测 2026-10-29（B11 条）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                CfVersionedEntity[] rows = [new() { Name = "a" }, new() { Name = "b" }];
                await session.BulkInsertAsync(rows);
                await Assert.That(rows[0].Id).IsEqualTo(0L);
                await Assert.That(rows[1].Id).IsEqualTo(0L);
                await Assert.That(await session.CountAsync<CfVersionedEntity>()).IsEqualTo(2L);
            }),
    ];

    [Test]
    public async Task AllBoundaryContracts_Hold()
    {
        List<string> failures = [];
        int index = 0;
        foreach (ContractEntry contract in Boundaries)
        {
            index++;
            try
            {
                await contract.Verify($"boundary_{index:D2}");
            }
            catch (Exception ex)
            {
                failures.Add($"{contract.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        await Assert.That(string.Join("\n", failures)).IsEmpty();
    }

    [Test]
    public async Task BoundaryCatalog_MetadataIsWellFormed()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        List<string> problems = [];
        foreach (ContractEntry contract in Boundaries)
        {
            if (!ids.Add(contract.Id)) problems.Add($"{contract.Id}: ID 重复");
            if (string.IsNullOrWhiteSpace(contract.Postcondition)) problems.Add($"{contract.Id}: 后置为空");
            if (string.IsNullOrWhiteSpace(contract.Source)) problems.Add($"{contract.Id}: 来源为空");
        }
        if (Boundaries.Count < 10) problems.Add($"边界契约条数为 {Boundaries.Count}，方案要求至少 10");
        await Assert.That(string.Join(" | ", problems)).IsEmpty();
    }
}