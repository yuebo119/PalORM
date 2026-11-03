using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>B 层契约条目（方案 §5.1）。
/// <para>每条带 ID、前置、后置与来源；<see cref="Verify"/> 在独立库上执行，失败即该条契约被违背。
/// 断言消息必须带 ID（由执行器统一拼接）。</para>
/// <para><b>期望值来源</b>：能力契约的后置一律取自 `docs/API参考.md` 与 XML doc 的承诺；
/// 代码里"实际如此"不作为契约依据。带"实测"字样的条目是 0a/0b 阶段发现文档未写明、经实测确认后登记的行为。</para></summary>
internal sealed record ContractEntry(
    string Id,
    string Precondition,
    string Postcondition,
    string Source,
    Func<string, Task> Verify);

/// <summary>契约条目的「合理性依据」检查（方案 §5.2 纪律的机械化载体）。
/// <para>每条契约的 <see cref="ContractEntry.Source"/> 必须指向三类依据之一：`API参考.md`（文档承诺）、
/// `ADR`（架构决策）、`reviewed:`（实测后经人工评审确认合理，后接一句结论）。只在源码注释里出现、
/// 或纯 `实测` 而无评审结论的，记为「仅实测」，进待评审清单。</para>
/// <para>为什么需要这条：首轮的 13 条产品行为里有 3 条是缺陷，根因正是把「当前行为」记成「合理行为」。
/// 这个检查把「交由评审」从口头纪律变成可核对字段。</para></summary>
internal static class ContractRationale
{
    public static List<string> Validate(IEnumerable<ContractEntry> entries)
    {
        var problems = new List<string>();
        foreach (ContractEntry entry in entries)
        {
            bool hasDocument = entry.Source.Contains("API参考.md", StringComparison.Ordinal);
            bool hasAdr = entry.Source.Contains("ADR", StringComparison.Ordinal);
            bool hasReview = entry.Source.StartsWith("reviewed:", StringComparison.Ordinal)
                || entry.Source.Contains("reviewed:", StringComparison.Ordinal);
            if (!hasDocument && !hasAdr && !hasReview)
                problems.Add(entry.Id + " → " + entry.Source);
        }
        return problems;
    }
}

internal static class ContractCatalog
{
    /// <summary>在独立库上开场，供各契约验证使用。</summary>
    private static async ValueTask<(SharedSqliteDb Shared, DataSession<SqliteProvider> Db)> OpenAsync(
        string libraryName, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(libraryName, ct);
        DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
        return (shared, db);
    }

    private static CfBulkPlainEntity Plain(long id, string name = "n", long amount = 1)
        => new() { Id = id, Name = name, Amount = amount };

    public static IReadOnlyList<ContractEntry> Capabilities { get; } =
    [
        // 2026-11-03 去重（18 条）：INSERT-01/02、SAVE-01/02、UPDATE-01、UPDCOLS-01、
        // DELETE-01/02、GET-01、GETALL-01、QUERY-01、AGG-01、SCALAR-01、BULKIN-01、
        // BULKUP-01、BULKMERGE-01、BULKDEL-01、TX-01/02 的断言体与 DialectContractCatalog
        // 的 D-* 同名条目逐行相同，而 D 层在 SQLite/PG/MySQL 三方言都跑（C 层只在 SQLite）。
        // 删除后 SQLite 侧仍由 D 层覆盖，覆盖率不降。保留的 12 条是 D 层没有的
        //（JOIN-01/02、CTE-01、GROUP-01、WINDOW-01、MULTI-01、RAW-01、SPROC-01、
        // TX-03、BULKUP-02）与断言不同的（PAGE-01：descending 默认 true 只在 C 层断言）。
        // SQLite 兜底由 DialectCapabilityContractTests.Sqlite_CapabilityContracts_Hold 的
        // "SQLite 不得登记不支持"守卫保证。

        new("C-JOIN-01",
            "左表三行、右表两行，按 Id 关联",
            "InnerJoin 只返回匹配的行（2 行）",
            "API参考.md 查询构建器（InnerJoin）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3)]);
                await session.BulkInsertAsync(
                [
                    new CfCompositeIndexEntity { Id = 1, Category = "a", Price = 1m, Sku = "s1" },
                    new CfCompositeIndexEntity { Id = 2, Category = "b", Price = 2m, Sku = "s2" }
                ]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .InnerJoin<CfCompositeIndexEntity>($"cf_composite_index.\"Id\" = cf_bulk_plain.\"Id\"")
                    .ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(2);
            }),

        new("C-JOIN-02",
            "左表三行、右表两行，按 Id 关联",
            "LeftJoin 保留左表全部行（3 行）",
            "API参考.md 查询构建器（LeftJoin）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3)]);
                await session.BulkInsertAsync(
                [
                    new CfCompositeIndexEntity { Id = 1, Category = "a", Price = 1m, Sku = "s1" },
                    new CfCompositeIndexEntity { Id = 2, Category = "b", Price = 2m, Sku = "s2" }
                ]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .LeftJoin<CfCompositeIndexEntity>($"cf_composite_index.\"Id\" = cf_bulk_plain.\"Id\"")
                    .ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(3);
            }),

        new("C-CTE-01",
            "三行 amount 为 10/20/30",
            "With(cteName, subquery) 后 From 以 CTE 为源，结果与子查询条件一致",
            "API参考.md 查询构建器（With）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1, "a", 10), Plain(2, "b", 20), Plain(3, "c", 30)]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .With("cf_cte", $"SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain WHERE \"amount\" > 15")
                    .ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(2);
            }),

        new("C-GROUP-01",
            "五行：x 出现两次、y 两次、z 一次",
            "GroupBy + Having 只返回满足聚合条件的组，组数与手写 SQL 一致",
            "API参考.md 查询构建器（GroupBy / Having）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync(
                [
                    Plain(1, "x"), Plain(2, "x"), Plain(3, "y"), Plain(4, "y"), Plain(5, "z")
                ]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .GroupBy(e => e.Name).Having($"COUNT(*) > 1").ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(2);
            }),

        new("C-WINDOW-01",
            "五行数据",
            "UnsafeWindowOver 只追加窗口列，不改变实体行集",
            "API参考.md 查询构建器（UnsafeWindowOver）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3), Plain(4), Plain(5)]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .UnsafeWindowOver("ROW_NUMBER()", "ORDER BY \"Id\"").ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(5);
            }),

        new("C-PAGE-01",
            "五行数据，pageSize 2",
            "ToPageAsync 返回 (rows, total)；descending 默认 true（从最大值续页）；末键续页得到下一页；builder 上带 Skip() 时抛 InvalidOperationException（键集分页以 lastValue 续页，OFFSET 会被静默丢弃）",
            "API参考.md 执行方法（ToPageAsync）+ 实测 2026-10-29",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3), Plain(4), Plain(5)]);
                (List<CfBulkPlainEntity> descPage, long descTotal) =
                    await session.From<CfBulkPlainEntity>().ToPageAsync(2, e => e.Id);
                await Assert.That(descTotal).IsEqualTo(5L);
                await Assert.That(string.Join(",", descPage.Select(e => e.Id))).IsEqualTo("5,4");

                (List<CfBulkPlainEntity> ascFirst, long ascTotal) =
                    await session.From<CfBulkPlainEntity>().ToPageAsync(2, e => e.Id, descending: false);
                (List<CfBulkPlainEntity> ascSecond, _) = await session.From<CfBulkPlainEntity>()
                    .ToPageAsync(2, e => e.Id, lastValue: ascFirst[^1].Id, descending: false);
                await Assert.That(ascTotal).IsEqualTo(5L);
                await Assert.That(string.Join(",", ascFirst.Select(e => e.Id))).IsEqualTo("1,2");
                await Assert.That(string.Join(",", ascSecond.Select(e => e.Id))).IsEqualTo("3,4");

                // Skip 会被键集分页静默丢弃，因此改为构建期拒绝（D6）
                await Assert.That(async () => await session.From<CfBulkPlainEntity>()
                    .Skip(1).ToPageAsync(2, e => e.Id)).Throws<InvalidOperationException>();
            }),

        new("C-BULKUP-02",
            "两行已插入，实体无 [ConcurrencyCheck]",
            "BulkUpdateBatchAsync 按批更新，返回影响行数 2",
            "API参考.md 写入 / 批量 + PALORM031 限制说明",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                CfBulkPlainEntity[] rows = [Plain(1, "a", 1), Plain(2, "b", 2)];
                await session.BulkInsertAsync(rows);
                rows[0].Amount = 11;
                rows[1].Amount = 12;
                long affected = await session.BulkUpdateBatchAsync(rows);
                await Assert.That(affected).IsEqualTo(2L);
                await Assert.That((await session.GetAsync<CfBulkPlainEntity>(2L))!.Amount).IsEqualTo(12L);
            }),

        new("C-TX-03",
            "事务内插入 A、Savepoint、插入 B、回滚到 Savepoint",
            "A 保留、B 被回滚，提交后只剩 A",
            "API参考.md 事务（SavepointAsync / RollbackToAsync）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                using System.Data.Common.DbTransaction transaction = await session.BeginTransactionAsync();
                await session.InsertAsync(Plain(1, "keep"));
                await session.SavepointAsync(transaction, "sp1");
                await session.InsertAsync(Plain(2, "discard"));
                await session.RollbackToAsync(transaction, "sp1");
                await transaction.CommitAsync();
                await Assert.That(await session.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
                await Assert.That((await session.GetAsync<CfBulkPlainEntity>(1L))!.Name).IsEqualTo("keep");
            }),

        new("C-MULTI-01",
            "两条 SELECT 的多结果集查询",
            "QueryMultipleAsync 返回的 GridReader 可依次读取两个结果集",
            "API参考.md 执行方法（QueryMultipleAsync / GridReader）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1, "a", 10), Plain(2, "b", 20)]);
                await using GridReader reader = await session.From<CfBulkPlainEntity>()
                    .QueryMultipleAsync(
                        $"SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain WHERE \"amount\" > 0; SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain");
                List<CfBulkPlainEntity> first = await reader.ReadAsync<CfBulkPlainEntity>();
                List<CfBulkPlainEntity> second = await reader.ReadAsync<CfBulkPlainEntity>();
                await Assert.That(first.Count).IsEqualTo(2);
                await Assert.That(second.Count).IsEqualTo(2);
            }),

        new("C-SPROC-01",
            "存储过程构建器",
            "StoredProc(name) 对空名与非法字符抛 ArgumentException（SQLite 无真实存储过程，走参数校验面）",
            "API参考.md 执行方法（StoredProc）+ 实测 SQLite 限制",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await Assert.That(() => session.StoredProc("")).Throws<ArgumentException>();
                await Assert.That(() => session.StoredProc("bad name; DROP")).Throws<ArgumentException>();
            }),

        new("C-RAW-01",
            "会话已初始化",
            "GetRawConnection 返回非 null 连接，且可直接执行原生 SQL",
            "API参考.md 执行方法（GetRawConnection 逃生舱）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.CountAsync<CfBulkPlainEntity>();
                using System.Data.Common.DbConnection connection = session.GetRawConnection();
                await Assert.That(connection).IsNotNull();
                await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Open);
            }),
    ];
}