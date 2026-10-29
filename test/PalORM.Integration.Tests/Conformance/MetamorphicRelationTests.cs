using PalORM.Sqlite;

// 本文件所有"逐行"路径都是变形关系的对照臂（批量对逐行），N+1 是刻意构造而非缺陷。
#pragma warning disable PALORM005

namespace PalORM.Integration.Tests.Conformance;

/// <summary>变形关系八条（方案 §4.5）。
/// <para>同一语义的两条路径互证：批量对逐行、合并对插入加更新、批量删除对逐行删除、
/// 幂等（两次等于一次）、分页并集对全量、切批对逐批、流式对批量读。这些关系不需要手写 SQL，
/// 边际成本低，但盲区是两条路径共享缺陷，因此每个操作族仍保留手写 SQL 锚点（0b 的双侧对照）。</para></summary>
public sealed class MetamorphicRelationTests
{
    private static CfBulkPlainEntity Plain(long id, string name, long amount)
        => new() { Id = id, Name = name, Amount = amount };

    private static async ValueTask<(SharedSqliteDb Shared, DataSession<SqliteProvider> Db)> OpenAsync(
        string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
        return (shared, db);
    }

    private static async Task<List<(long Id, string Name, long Amount)>> SnapshotAsync(
        DataSession<SqliteProvider> db, CancellationToken ct = default)
    {
        List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>().ToListAsync(ct);
        return [.. rows.Select(r => (r.Id, r.Name, r.Amount)).OrderBy(r => r.Id)];
    }

    private static string Render(List<(long Id, string Name, long Amount)> rows)
        => string.Join("|", rows.Select(r => $"{r.Id}:{r.Name}:{r.Amount}"));

    [Test]
    public async Task Relation1_BulkInsertEqualsRowByRowInsert()
    {
        (SharedSqliteDb sharedBulk, DataSession<SqliteProvider> bulkDb) = await OpenAsync("mr_bulk_insert_bulk");
        await using SharedSqliteDb scopeBulk = sharedBulk;
        await using DataSession<SqliteProvider> bulk = bulkDb;
        (SharedSqliteDb sharedRow, DataSession<SqliteProvider> rowDb) = await OpenAsync("mr_bulk_insert_row");
        await using SharedSqliteDb scopeRow = sharedRow;
        await using DataSession<SqliteProvider> rowByRow = rowDb;

        CfBulkPlainEntity[] rows = [Plain(1, "a", 1), Plain(2, "b", 2), Plain(3, "c", 3)];
        await bulk.BulkInsertAsync(rows);
        foreach (CfBulkPlainEntity entity in rows)
            await rowByRow.InsertAsync(entity);

        await Assert.That(Render(await SnapshotAsync(bulk))).IsEqualTo(Render(await SnapshotAsync(rowByRow)));
    }

    [Test]
    public async Task Relation2_BulkUpdateEqualsRowByRowUpdate()
    {
        (SharedSqliteDb sharedBulk, DataSession<SqliteProvider> bulkDb) = await OpenAsync("mr_bulk_update_bulk");
        await using SharedSqliteDb scopeBulk = sharedBulk;
        await using DataSession<SqliteProvider> bulk = bulkDb;
        (SharedSqliteDb sharedRow, DataSession<SqliteProvider> rowDb) = await OpenAsync("mr_bulk_update_row");
        await using SharedSqliteDb scopeRow = sharedRow;
        await using DataSession<SqliteProvider> rowByRow = rowDb;

        CfBulkPlainEntity[] bulkRows = [Plain(1, "before", 1), Plain(2, "before", 2)];
        CfBulkPlainEntity[] rowRows = [Plain(1, "before", 1), Plain(2, "before", 2)];
        await bulk.BulkInsertAsync(bulkRows);
        foreach (CfBulkPlainEntity entity in rowRows) await rowByRow.InsertAsync(entity);

        bulkRows[0].Name = "after";
        bulkRows[1].Amount = 22;
        await bulk.BulkUpdateAsync(bulkRows);

        rowRows[0].Name = "after";
        await rowByRow.UpdateAsync(rowRows[0]);
        rowRows[1].Amount = 22;
        await rowByRow.UpdateAsync(rowRows[1]);

        await Assert.That(Render(await SnapshotAsync(bulk))).IsEqualTo(Render(await SnapshotAsync(rowByRow)));
    }

    [Test]
    public async Task Relation3_BulkMergeEqualsInsertPlusUpdate()
    {
        (SharedSqliteDb sharedMerge, DataSession<SqliteProvider> mergeDb) = await OpenAsync("mr_merge_merge");
        await using SharedSqliteDb scopeMerge = sharedMerge;
        await using DataSession<SqliteProvider> merge = mergeDb;
        (SharedSqliteDb sharedMixed, DataSession<SqliteProvider> mixedDb) = await OpenAsync("mr_merge_mixed");
        await using SharedSqliteDb scopeMixed = sharedMixed;
        await using DataSession<SqliteProvider> mixed = mixedDb;

        // 两侧先各有主键 1，再合并 [1 更新, 2 插入]
        await merge.BulkInsertAsync([Plain(1, "old", 1)]);
        await mixed.InsertAsync(Plain(1, "old", 1));

        await merge.BulkMergeAsync([Plain(1, "new", 11), Plain(2, "added", 2)]);
        await mixed.UpdateAsync(Plain(1, "new", 11));
        await mixed.InsertAsync(Plain(2, "added", 2));

        await Assert.That(Render(await SnapshotAsync(merge))).IsEqualTo(Render(await SnapshotAsync(mixed)));
    }

    [Test]
    public async Task Relation4_BulkDeleteEqualsRowByRowDelete()
    {
        (SharedSqliteDb sharedBulk, DataSession<SqliteProvider> bulkDb) = await OpenAsync("mr_bulk_delete_bulk");
        await using SharedSqliteDb scopeBulk = sharedBulk;
        await using DataSession<SqliteProvider> bulk = bulkDb;
        (SharedSqliteDb sharedRow, DataSession<SqliteProvider> rowDb) = await OpenAsync("mr_bulk_delete_row");
        await using SharedSqliteDb scopeRow = sharedRow;
        await using DataSession<SqliteProvider> rowByRow = rowDb;

        CfBulkPlainEntity[] rows = [Plain(1, "a", 1), Plain(2, "b", 2), Plain(3, "c", 3)];
        await bulk.BulkInsertAsync(rows);
        foreach (CfBulkPlainEntity entity in rows) await rowByRow.InsertAsync(entity);

        await bulk.BulkDeleteAsync<CfBulkPlainEntity, long>([2L, 3L]);
        await rowByRow.DeleteAsync<CfBulkPlainEntity>(2L);
        await rowByRow.DeleteAsync<CfBulkPlainEntity>(3L);

        await Assert.That(Render(await SnapshotAsync(bulk))).IsEqualTo(Render(await SnapshotAsync(rowByRow)));
    }

    [Test]
    public async Task Relation5_SaveTwiceEqualsSaveOnce()
    {
        (SharedSqliteDb sharedTwice, DataSession<SqliteProvider> twiceDb) = await OpenAsync("mr_save_twice");
        await using SharedSqliteDb scopeTwice = sharedTwice;
        await using DataSession<SqliteProvider> twice = twiceDb;
        (SharedSqliteDb sharedOnce, DataSession<SqliteProvider> onceDb) = await OpenAsync("mr_save_once");
        await using SharedSqliteDb scopeOnce = sharedOnce;
        await using DataSession<SqliteProvider> once = onceDb;

        await twice.SaveAsync(Plain(1, "v1", 1));
        await twice.SaveAsync(Plain(1, "v1", 1));
        await once.SaveAsync(Plain(1, "v1", 1));

        await Assert.That(Render(await SnapshotAsync(twice))).IsEqualTo(Render(await SnapshotAsync(once)));
        await Assert.That(await twice.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
    }

    [Test]
    public async Task Relation6_PageUnionEqualsGetAll()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync("mr_page_union");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        CfBulkPlainEntity[] rows =
        [
            .. Enumerable.Range(1, 7).Select(i => Plain(i, $"p{i}", i))
        ];
        await session.BulkInsertAsync(rows);

        List<long> paged = [];
        long? lastValue = null;
        while (true)
        {
            (List<CfBulkPlainEntity> page, _) = lastValue is null
                ? await session.From<CfBulkPlainEntity>().ToPageAsync(3, e => e.Id, descending: false)
                : await session.From<CfBulkPlainEntity>().ToPageAsync(3, e => e.Id, lastValue, descending: false);
            if (page.Count == 0) break;
            paged.AddRange(page.Select(e => e.Id));
            lastValue = page[^1].Id;
            if (page.Count < 3) break;
        }

        List<long> all = [.. (await SnapshotAsync(session)).Select(r => r.Id)];
        await Assert.That(paged.Count).IsEqualTo(paged.Distinct().Count());
        await Assert.That(string.Join(",", paged.OrderBy(id => id))).IsEqualTo(string.Join(",", all));
    }

    [Test]
    public async Task Relation7_WhereInBatchEqualsPerBatchUnion()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync("mr_where_in_batches");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        await session.BulkInsertAsync([.. Enumerable.Range(1, 20).Select(i => Plain(i, $"p{i}", i))]);
        long[] allKeys = [.. Enumerable.Range(1, 501).Select(i => (long)i)];

        // 一次 501 个值（触发切批）
        List<long> singleCall = [.. (await session.From<CfBulkPlainEntity>()
            .WhereIn(e => e.Id, allKeys).ToListAsync()).Select(e => e.Id)];

        // 手动按 500 切批
        List<long> batched = [];
        foreach (long[] chunk in allKeys.Chunk(500))
        {
            batched.AddRange((await session.From<CfBulkPlainEntity>()
                .WhereIn(e => e.Id, chunk).ToListAsync()).Select(e => e.Id));
        }

        await Assert.That(string.Join(",", singleCall.OrderBy(id => id)))
            .IsEqualTo(string.Join(",", batched.OrderBy(id => id)));
        await Assert.That(singleCall.Count).IsEqualTo(20);
    }

    [Test]
    public async Task Relation8_AsyncEnumerableEqualsQueryAsync()
    {
        (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync("mr_stream");
        await using SharedSqliteDb scope = shared;
        await using DataSession<SqliteProvider> session = db;

        await session.BulkInsertAsync([.. Enumerable.Range(1, 30).Select(i => Plain(i, $"p{i}", i))]);

        List<long> materialized = [.. (await session.QueryAsync<CfBulkPlainEntity>(
            $"SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain")).Select(e => e.Id)];

        List<long> streamed = [];
        await foreach (CfBulkPlainEntity entity in session.QueryAsyncEnumerable<CfBulkPlainEntity>(
            $"SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain"))
        {
            streamed.Add(entity.Id);
        }

        await Assert.That(streamed.Count).IsEqualTo(30);
        await Assert.That(string.Join(",", streamed.OrderBy(id => id)))
            .IsEqualTo(string.Join(",", materialized.OrderBy(id => id)));
    }
}