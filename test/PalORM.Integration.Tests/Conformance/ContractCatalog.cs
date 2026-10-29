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
        new("C-INSERT-01",
            "显式主键实体（[Key(AutoIncrement = false)]）",
            "InsertAsync 返回同一实体实例，Id 保持调用方给的值",
            "API参考.md 直查 / CRUD",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                CfBulkPlainEntity entity = Plain(7);
                CfBulkPlainEntity returned = await session.InsertAsync(entity);
                await Assert.That(ReferenceEquals(returned, entity)).IsTrue();
                await Assert.That(returned.Id).IsEqualTo(7L);
            }),

        new("C-INSERT-02",
            "自增主键实体（[SoftDelete] + [Key] 默认自增）",
            "InsertAsync 后实体 Id 被回填为正数",
            "API参考.md 直查 / CRUD",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                CfSoftDeleteEntity entity = new() { Name = "auto" };
                await session.InsertAsync(entity);
                await Assert.That(entity.Id).IsGreaterThan(0L);
            }),

        new("C-SAVE-01",
            "主键不存在",
            "SaveAsync 插入新行，影响行数为 1",
            "API参考.md 写入 / 批量（InsertOrUpdate 单次往返 UPSERT）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.SaveAsync(Plain(1, "inserted"));
                CfBulkPlainEntity? row = await session.GetAsync<CfBulkPlainEntity>(1L);
                await Assert.That(row).IsNotNull();
                await Assert.That(row!.Name).IsEqualTo("inserted");
            }),

        new("C-SAVE-02",
            "主键已存在",
            "SaveAsync 走冲突更新分支，行内容被覆盖且不新增行",
            "API参考.md 写入 / 批量（InsertOrUpdate 单次往返 UPSERT）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.InsertAsync(Plain(1, "before", 1));
                await session.SaveAsync(Plain(1, "after", 9));
                await Assert.That(await session.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
                CfBulkPlainEntity row = (await session.GetAsync<CfBulkPlainEntity>(1L))!;
                await Assert.That(row.Name).IsEqualTo("after");
                await Assert.That(row.Amount).IsEqualTo(9L);
            }),

        new("C-UPDATE-01",
            "实体存在或不存在",
            "UpdateAsync 返回受影响行数：存在为 1，不存在为 0",
            "API参考.md 直查 / CRUD",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.InsertAsync(Plain(1));
                int existing = await session.UpdateAsync(Plain(1, "updated"));
                int missing = await session.UpdateAsync(Plain(999, "ghost"));
                await Assert.That(existing).IsEqualTo(1);
                await Assert.That(missing).IsEqualTo(0);
            }),

        new("C-UPDCOLS-01",
            "实体与单列表达式",
            "UpdateColumnsAsync 只写指定列，其余列保持原值，返回 1",
            "API参考.md 直查 / CRUD（部分更新）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.InsertAsync(Plain(1, "keep", 1));
                int affected = await session.UpdateColumnsAsync(
                    new CfBulkPlainEntity { Id = 1, Name = "ignored", Amount = 42 }, e => e.Amount);
                CfBulkPlainEntity row = (await session.GetAsync<CfBulkPlainEntity>(1L))!;
                await Assert.That(affected).IsEqualTo(1);
                await Assert.That(row.Name).IsEqualTo("keep");
                await Assert.That(row.Amount).IsEqualTo(42L);
            }),

        new("C-DELETE-01",
            "非软删实体",
            "DeleteAsync 物理删除，返回 1，行不再存在",
            "API参考.md 直查 / CRUD",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.InsertAsync(Plain(1));
                int affected = await session.DeleteAsync<CfBulkPlainEntity>(1L);
                await Assert.That(affected).IsEqualTo(1);
                await Assert.That(await session.GetAsync<CfBulkPlainEntity>(1L)).IsNull();
            }),

        new("C-DELETE-02",
            "软删实体",
            "DeleteAsync 只写 deleted_at，行仍存在但默认查询不可见，IgnoreFilters 可见",
            "API参考.md 直查 / CRUD + [SoftDelete] 注解说明",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                CfSoftDeleteEntity entity = new() { Name = "soft" };
                await session.InsertAsync(entity);
                int affected = await session.DeleteAsync<CfSoftDeleteEntity>(entity.Id);
                await Assert.That(affected).IsEqualTo(1);
                await Assert.That(await session.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(0L);
                await using DataSession<SqliteProvider> inspect = await shared.OpenSessionAsync();
                await Assert.That((await inspect.IgnoreFilters().From<CfSoftDeleteEntity>().ToListAsync()).Count)
                    .IsEqualTo(1);
            }),

        new("C-GET-01",
            "主键存在与不存在",
            "GetAsync 命中返回实体，未命中返回 null",
            "API参考.md 直查 / CRUD",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.InsertAsync(Plain(1, "found"));
                CfBulkPlainEntity? hit = await session.GetAsync<CfBulkPlainEntity>(1L);
                CfBulkPlainEntity? miss = await session.GetAsync<CfBulkPlainEntity>(2L);
                await Assert.That(hit).IsNotNull();
                await Assert.That(hit!.Name).IsEqualTo("found");
                await Assert.That(miss).IsNull();
            }),

        new("C-GETALL-01",
            "实体有 3 行（软删形状含 1 行已删）",
            "GetAllAsync 返回全部有效行，数量与裸 ADO 的有效集一致",
            "API参考.md 直查 / CRUD",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.InsertAsync(new CfSoftDeleteEntity { Name = "a" });
                await session.InsertAsync(new CfSoftDeleteEntity { Name = "b" });
                await session.InsertAsync(new CfSoftDeleteEntity { Name = "c", DeletedAt = "2026-01-01" });
                List<CfSoftDeleteEntity> rows = await session.GetAllAsync<CfSoftDeleteEntity>();
                await Assert.That(rows.Count).IsEqualTo(2);
            }),

        new("C-QUERY-01",
            "四行数据",
            "ToListAsync 条件查询返回满足条件的行，且默认过滤仍生效",
            "API参考.md 查询构建器 / 执行方法",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1, "a", 10), Plain(2, "b", 20), Plain(3, "c", 30)]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .Where($"amount >= 20").ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(2);
            }),

        new("C-AGG-01",
            "三行 amount 为 10/20/30",
            "Count/Sum/Max/Min/Avg 与手写 SQL 的聚合值一致",
            "API参考.md 聚合",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1, "a", 10), Plain(2, "b", 20), Plain(3, "c", 30)]);
                await Assert.That(await session.CountAsync<CfBulkPlainEntity>()).IsEqualTo(3L);
                await Assert.That(await session.SumAsync<CfBulkPlainEntity>($"amount")).IsEqualTo(60m);
                await Assert.That(await session.MaxAsync<CfBulkPlainEntity, long>($"amount")).IsEqualTo(30L);
                await Assert.That(await session.MinAsync<CfBulkPlainEntity, long>($"amount")).IsEqualTo(10L);
                await Assert.That(await session.AvgAsync<CfBulkPlainEntity>($"amount")).IsEqualTo(20d);
            }),

        new("C-SCALAR-01",
            "原始 SQL 入口",
            "ScalarAsync 返回与裸 ADO 相同的标量值",
            "API参考.md 执行方法（原始 SQL 入口，默认过滤不适用，ITM-700）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2)]);
                long count = await session.ScalarAsync<long>($"SELECT COUNT(*) FROM cf_bulk_plain");
                await Assert.That(count).IsEqualTo(2L);
            }),

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
            "ToPageAsync 返回 (rows, total)；descending 默认 true（从最大值续页）；末键续页得到下一页",
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
            }),

        new("C-BULKIN-01",
            "三行显式主键",
            "BulkInsertAsync 返回影响行数，且不回填自增主键（显式主键形状不应被改写）",
            "API参考.md 写入 / 批量 + 实测 2026-10-29（B11）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                CfBulkPlainEntity[] rows = [Plain(1, "a"), Plain(2, "b"), Plain(3, "c")];
                long affected = await session.BulkInsertAsync(rows);
                await Assert.That(affected).IsEqualTo(3L);
                await Assert.That(rows[0].Id).IsEqualTo(1L);
                await Assert.That(rows[2].Id).IsEqualTo(3L);
            }),

        new("C-BULKUP-01",
            "两行已插入",
            "BulkUpdateAsync 按主键逐行更新，返回影响行数 2",
            "API参考.md 写入 / 批量",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                CfBulkPlainEntity[] rows = [Plain(1, "a"), Plain(2, "b")];
                await session.BulkInsertAsync(rows);
                rows[0].Name = "a2";
                rows[1].Name = "b2";
                long affected = await session.BulkUpdateAsync(rows);
                await Assert.That(affected).IsEqualTo(2L);
                await Assert.That((await session.GetAsync<CfBulkPlainEntity>(1L))!.Name).IsEqualTo("a2");
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

        new("C-BULKMERGE-01",
            "主键 1 已存在，主键 2 不存在",
            "BulkMergeAsync 对已存在键更新、对新键插入",
            "API参考.md 写入 / 批量",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1, "existing", 1)]);
                await session.BulkMergeAsync([Plain(1, "merged", 11), Plain(2, "added", 2)]);
                await Assert.That(await session.CountAsync<CfBulkPlainEntity>()).IsEqualTo(2L);
                await Assert.That((await session.GetAsync<CfBulkPlainEntity>(1L))!.Name).IsEqualTo("merged");
                await Assert.That((await session.GetAsync<CfBulkPlainEntity>(2L))!.Name).IsEqualTo("added");
            }),

        new("C-BULKDEL-01",
            "三行，删除其中两个主键",
            "BulkDeleteAsync 返回影响行数 2，剩余 1 行",
            "API参考.md 写入 / 批量",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3)]);
                long affected = await session.BulkDeleteAsync<CfBulkPlainEntity, long>([2L, 3L]);
                await Assert.That(affected).IsEqualTo(2L);
                await Assert.That(await session.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
            }),

        new("C-TX-01",
            "显式事务内插入",
            "提交后行可见",
            "API参考.md 事务",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                using System.Data.Common.DbTransaction transaction = await session.BeginTransactionAsync();
                await session.InsertAsync(Plain(1, "committed"));
                await transaction.CommitAsync();
                await Assert.That(await session.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
            }),

        new("C-TX-02",
            "显式事务内插入后回滚",
            "回滚后行不可见",
            "API参考.md 事务",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                using System.Data.Common.DbTransaction transaction = await session.BeginTransactionAsync();
                await session.InsertAsync(Plain(1, "rolled-back"));
                await transaction.RollbackAsync();
                await Assert.That(await session.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
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