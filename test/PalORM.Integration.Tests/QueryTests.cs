using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

public sealed class QueryTests
{
    private static readonly string[] _includedStatuses = ["A", "C"];
    private static readonly string[] _excludedStatuses = ["A", "B"];

    // ─── Phase 1 ────────────────────────────────────────

    [Test]
    public async Task HealthCheck_Succeeds()
    {
        await using var db = await TestDb.SqliteAsync();
        await Assert.That((await db.HealthCheckAsync()).IsHealthy).IsTrue();
    }

    [Test]
    public async Task From_Where_ToListAsync_ReturnsResults()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.ExecuteAsync($"INSERT INTO orders (status, total, created_at) VALUES ({"P"}, {99m}, {0L})");
        var r = await db.From<Order>().Where($"status = {"P"}").ToListAsync();
        await Assert.That(r.Count).IsEqualTo(1);
    }

    [Test]
    public async Task From_OrderBy_Take_Works()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.ExecuteAsync($"INSERT INTO orders (status, total, created_at) VALUES ({"A"},{10m},{0L})");
        await db.ExecuteAsync($"INSERT INTO orders (status, total, created_at) VALUES ({"B"},{20m},{0L})");
        await db.ExecuteAsync($"INSERT INTO orders (status, total, created_at) VALUES ({"C"},{30m},{0L})");
        var r = await db.From<Order>().OrderBy(o => o.Total, true).Take(2).ToListAsync();
        await Assert.That(r.Count).IsEqualTo(2);
        await Assert.That(r[0].Total).IsEqualTo(30m);
    }

    [Test]
    public async Task ScalarAsync_ReturnsCount()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.ExecuteAsync($"INSERT INTO orders (status,total,created_at) VALUES ({"A"},{10m},{0L})");
        await db.ExecuteAsync($"INSERT INTO orders (status,total,created_at) VALUES ({"B"},{20m},{0L})");
        long? c = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM orders WHERE total > {15m}");
        await Assert.That(c).IsEqualTo(1);
    }

    [Test]
    public async Task ScalarAsync_CompositeFormat_ReturnsFilteredCount()
    {
        // r19/T-P3-06：原名 RemainsParameterized 无参数化断言——按实际行为改名
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "F", Total = 12.5m, CreatedAt = 0 });

        long? count = await db.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM orders WHERE total = {12.5m:N1}");

        await Assert.That(count).IsEqualTo(1);
    }

    // ─── Phase 2 CRUD ──────────────────────────────────

    [Test]
    public async Task InsertAsync_ReturnsEntityWithId()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var r = await db.InsertAsync(new Order { Status = "N", Total = 42m, CreatedAt = 0 });
        await Assert.That(r.Id).IsGreaterThan(0);
    }

    [Test]
    public async Task GetAsync_FindsEntity()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var ins = await db.InsertAsync(new Order { Status = "T", Total = 5m, CreatedAt = 0 });
        var r = await db.GetAsync<Order>(ins.Id);
        await Assert.That(r!.Status).IsEqualTo("T");
    }

    [Test]
    public async Task UpdateAsync_ModifiesEntity()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var ins = await db.InsertAsync(new Order { Status = "O", Total = 10m, CreatedAt = 0 });
        ins.Status = "U";
        await db.UpdateAsync(ins);
        await Assert.That((await db.GetAsync<Order>(ins.Id))!.Status).IsEqualTo("U");
    }

    [Test]
    public async Task DeleteAsync_RemovesEntity()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var ins = await db.InsertAsync(new Order { Status = "D", Total = 1m, CreatedAt = 0 });
        await db.DeleteAsync<Order>(ins.Id);
        await Assert.That(await db.GetAsync<Order>(ins.Id)).IsNull();
    }

    // ─── Phase 2 Extended ───────────────────────────────

    [Test]
    public async Task SaveAsync_InsertsNewEntity()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var r = await db.SaveAsync(new Order { Status = "S", Total = 7m, CreatedAt = 0 });
        await Assert.That(r.Id).IsGreaterThan(0);
    }

    [Test]
    public async Task SaveAsync_WithExistingIdUpdatesWithoutAddingRow()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var inserted = await db.InsertAsync(new Order { Status = "Old", Total = 1m, CreatedAt = 0 });
        inserted.Status = "New";
        var saved = await db.SaveAsync(inserted);
        await Assert.That(await db.CountAsync<Order>()).IsEqualTo(1);
        await Assert.That(saved.Id).IsEqualTo(inserted.Id);
        await Assert.That((await db.GetAsync<Order>(inserted.Id))!.Status).IsEqualTo("New");
    }

    [Test]
    public async Task MigrateAsync_CreatesTableAutomatically()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var r = await db.InsertAsync(new Order { Status = "M", Total = 3m, CreatedAt = 0 });
        await Assert.That(r.Id).IsGreaterThan(0);
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "X", Total = 1m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "Y", Total = 2m, CreatedAt = 0 });
        var r = await db.GetAllAsync<Order>();
        await Assert.That(r.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task BulkInsertAsync_InsertsBatch()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var items = new[] {
            new Order { Status = "B1", Total = 1m, CreatedAt = 0 },
            new Order { Status = "B2", Total = 2m, CreatedAt = 0 }
        };
        long n = await db.BulkInsertAsync(items);
        await Assert.That(n).IsEqualTo(2);
        await Assert.That((await db.GetAllAsync<Order>()).Count).IsGreaterThanOrEqualTo(2);
    }

    // ─── Phase 1 补充 ──────────────────────────────────

    [Test]
    public async Task WhereIn_FiltersByList()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "A", Total = 1m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "B", Total = 2m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "C", Total = 3m, CreatedAt = 0 });
        var query = db.From<Order>().WhereIn(o => o.Status, _includedStatuses);
        var dry = query.AsDryRun();
        var r = await query.ToListAsync();
        await Assert.That(dry.Parameters.Select(parameter => parameter.ParameterName).Distinct().Count()).IsEqualTo(2);
        await Assert.That(r.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SingleAsync_ReturnsSingle()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "Only", Total = 1m, CreatedAt = 0 });
        var r = await db.From<Order>().Where($"status = {"Only"}").SingleAsync();
        await Assert.That(r.Status).IsEqualTo("Only");
    }

    [Test]
    public async Task ToPageAsync_Works()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await InsertOrdersAsync(db, 10, i => new Order { Status = $"P{i}", Total = i * 10m, CreatedAt = 0 });
        var (rows, total) = await db.From<Order>().ToPageAsync(3, o => o.Total, descending: true);
        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(total).IsEqualTo(10);
    }

    [Test]
    public async Task ToPageAsync_BindsWhereParametersAndDoesNotModifyBuilder()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await InsertOrdersAsync(db, 6, i => new Order { Status = i % 2 == 0 ? "A" : "B", Total = i * 10m, CreatedAt = 0 });
        var query = db.From<Order>().Where($"status = {"A"}");
        var (rows, total) = await query.ToPageAsync(2, o => o.Total, 20m, descending: true);
        var originalRows = await query.ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(total).IsEqualTo(3);
        await Assert.That(originalRows.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ToPageAsync_ReusesExistingTransactionWithoutCommittingIt()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "Committed", Total = 1m, CreatedAt = 0 });
        await using var transaction = await db.BeginTransactionAsync();
        await db.InsertAsync(new Order { Status = "Pending", Total = 2m, CreatedAt = 0 });
        var (rows, total) = await db.From<Order>().ToPageAsync(10, o => o.Total);
        await transaction.RollbackAsync();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(total).IsEqualTo(2);
        await Assert.That(await db.CountAsync<Order>()).IsEqualTo(1);
    }

    [Test]
    public async Task CTE_WithClause_Works()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "X", Total = 100m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "Y", Total = 50m, CreatedAt = 0 });
        var r = await db.From<Order>().With("cte", $"SELECT * FROM orders WHERE total > {60m}").Where($"status = {"X"}").ToListAsync();
        await Assert.That(r.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AsSplitQuery_Works()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "S1", Total = 1m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "S2", Total = 2m, CreatedAt = 0 });
        var r = await db.From<Order>().AsSplitQuery().ToListAsync();
        await Assert.That(r.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Select_DryRun_UsesGeneratedColumnMapping()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Order>().Select(o => o.Status).AsDryRun();
        await Assert.That(dry.Sql).Contains("\"orders\".\"status\"");
    }

    [Test]
    public async Task Query1000Rows_ReturnsAllRows()
    {
        // T-P3-04：删除 100ms 硬阈值——CI 抖动误报且性能由 bench 体系负责（01/05 类）。
        // 本测试只锁 1000 行大批量查询的正确性（行数完整）。
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var items = Enumerable.Range(0, 1000).Select(i => new Order { Status = "T", Total = i * 10m, CreatedAt = 0 }).ToList();
        await db.BulkInsertAsync(items);
        await db.From<Order>().ToListAsync();
        await db.From<Order>().ToListAsync();
        var r = await db.From<Order>().ToListAsync();
        await Assert.That(r.Count).IsEqualTo(1000);
    }

    // ─── Phase 3 聚合 ──────────────────────────────────

    [Test]
    public async Task CountAsync_ReturnsCount()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "A", Total = 1m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "B", Total = 2m, CreatedAt = 0 });
        var c = await db.CountAsync<Order>();
        await Assert.That(c).IsEqualTo(2);
    }

    [Test]
    public async Task CountAsync_WithWhere_Filters()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "A", Total = 1m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "B", Total = 2m, CreatedAt = 0 });
        var c = await db.CountAsync<Order>($"status = {"A"}");
        await Assert.That(c).IsEqualTo(1);
    }

    [Test]
    public async Task SumAsync_ReturnsSum()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "X", Total = 10m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "Y", Total = 20m, CreatedAt = 0 });
        var s = await db.SumAsync<Order>($"total");
        await Assert.That(s).IsEqualTo(30m);
    }

    [Test]
    public async Task MaxAsync_ReturnsMax()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "X", Total = 10m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "Y", Total = 50m, CreatedAt = 0 });
        var m = await db.MaxAsync<Order, decimal>($"total");
        await Assert.That(m).IsEqualTo(50m);
    }

    [Test]
    public async Task MinAsync_ReturnsMin()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "X", Total = 10m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "Y", Total = 5m, CreatedAt = 0 });
        var m = await db.MinAsync<Order, decimal>($"total");
        await Assert.That(m).IsEqualTo(5m);
    }

    [Test]
    public async Task AvgAsync_ReturnsAvg()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "X", Total = 10m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "Y", Total = 20m, CreatedAt = 0 });
        var a = await db.AvgAsync<Order>($"total");
        await Assert.That(a).IsEqualTo(15d);
    }

    // ─── QueryBuilder: 执行方法 ──────────────────────────

    [Test]
    public async Task FirstAsync_ReturnsFirst()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "F1", Total = 1m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "F2", Total = 2m, CreatedAt = 0 });
        var r = await db.From<Order>().OrderBy(o => o.Total).FirstAsync();
        await Assert.That(r.Total).IsEqualTo(1m);
    }

    [Test]
    public async Task FirstOrDefaultAsync_ReturnsDefault()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var r = await db.From<Order>().Where($"status = {"NOPE"}").FirstOrDefaultAsync();
        await Assert.That(r).IsNull();
    }

    [Test]
    public async Task SingleOrDefaultAsync_ReturnsDefaultWhenEmpty()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var r = await db.From<Order>().Where($"status = {"NOPE"}").SingleOrDefaultAsync();
        await Assert.That(r).IsNull();
    }

    [Test]
    public async Task Skip_Take_Works()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await InsertOrdersAsync(db, 5, i => new Order { Status = "S", Total = i * 10m, CreatedAt = 0 });
        var r = await db.From<Order>().OrderBy(o => o.Total).Skip(2).Take(2).ToListAsync();
        await Assert.That(r.Count).IsEqualTo(2);
        await Assert.That(r[0].Total).IsEqualTo(20m);
    }

    [Test]
    public async Task OrWhere_Works()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "A", Total = 1m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "B", Total = 2m, CreatedAt = 0 });
        var r = await db.From<Order>().Where($"status = {"A"}").OrWhere($"status = {"B"}").ToListAsync();
        await Assert.That(r.Count).IsEqualTo(2);
    }

    [Test]
    public async Task WhereNotIn_FiltersCorrectly()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "A", Total = 1m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "B", Total = 2m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "C", Total = 3m, CreatedAt = 0 });
        var r = await db.From<Order>().WhereNotIn(o => o.Status, _excludedStatuses).ToListAsync();
        await Assert.That(r.Count).IsEqualTo(1);
        await Assert.That(r[0].Status).IsEqualTo("C");
    }

    [Test]
    public async Task ThenBy_SecondarySort()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "A", Total = 10m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "A", Total = 5m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "B", Total = 20m, CreatedAt = 0 });
        var r = await db.From<Order>().OrderBy(o => o.Status).ThenBy(o => o.Total).ToListAsync();
        await Assert.That(r[0].Status).IsEqualTo("A");
        await Assert.That(r[0].Total).IsEqualTo(5m);
        await Assert.That(r[1].Total).IsEqualTo(10m);
    }

    // ITM-306 回归：已有 OrderBy 的查询再分页/再排序不得生成双 ORDER BY 非法 SQL
    [Test]
    public async Task ToPageAsync_AfterOrderBy_DoesNotEmitDoubleOrderBy()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await InsertOrdersAsync(db, 6, i => new Order { Status = $"S{i}", Total = i * 10m, CreatedAt = 0 });
        var (rows, total) = await db.From<Order>().OrderBy(o => o.Status).ToPageAsync(3, o => o.Id);
        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(total).IsEqualTo(6);
    }

    [Test]
    public async Task OrderBy_CalledTwice_DegradesToMultiKeySort()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "A", Total = 10m, CreatedAt = 0 });
        await db.InsertAsync(new Order { Status = "A", Total = 5m, CreatedAt = 0 });
        var r = await db.From<Order>().OrderBy(o => o.Status).OrderBy(o => o.Total).ToListAsync();
        await Assert.That(r[0].Total).IsEqualTo(5m);
    }

    // ITM-307 回归：含 OR 的用户条件必须括号包裹，不得穿透软删过滤
    [Test]
    public async Task Where_WithOr_DoesNotBypassSoftDeleteFilter()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var keep = await db.InsertAsync(new SoftDeletableEntity { Name = "keep" });
        var gone = await db.InsertAsync(new SoftDeletableEntity { Name = "gone" });
        await db.DeleteAsync<SoftDeletableEntity>(gone.Id);
        _ = keep;
        var r = await db.From<SoftDeletableEntity>().Where($"name = {"keep"} OR name = {"gone"}").ToListAsync();
        await Assert.That(r.Count).IsEqualTo(1);
        await Assert.That(r[0].Name).IsEqualTo("keep");
    }

    // ─── Phase 2: Bulk/事务 ─────────────────────────────

    [Test]
    public async Task BulkDeleteAsync_RemovesBatch()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var items = await InsertOrdersAsync(db, 5, i => new Order { Status = "D", Total = i * 10m, CreatedAt = 0 });
        var keys = items.Select(x => (object)x.Id).ToList();
        var n = await db.BulkDeleteAsync<Order>(keys);
        await Assert.That(n).IsEqualTo(5);
        await Assert.That(await db.CountAsync<Order>()).IsEqualTo(0);
    }

    [Test]
    public async Task BulkUpdateAsync_UpdatesBatch()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var items = await InsertOrdersAsync(db, 3, i => new Order { Status = "Old", Total = i * 10m, CreatedAt = 0 });
        foreach (var x in items) { x.Status = "New"; }
        await db.BulkUpdateAsync(items);
        var r = await db.GetAllAsync<Order>();
        await Assert.That(r.All(x => x.Status == "New")).IsTrue();
    }

    [Test]
    public async Task BeginTransactionAsync_CommitWorks()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        using var tran = await db.BeginTransactionAsync();
        await db.InsertAsync(new Order { Status = "T", Total = 1m, CreatedAt = 0 });
        await tran.CommitAsync();
        await Assert.That(await db.CountAsync<Order>()).IsEqualTo(1);
    }

    [Test]
    public async Task BeginTransactionAsync_RollbackWorks()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        using var tran = await db.BeginTransactionAsync();
        await db.InsertAsync(new Order { Status = "R", Total = 1m, CreatedAt = 0 });
        await tran.RollbackAsync();
        await Assert.That(await db.CountAsync<Order>()).IsEqualTo(0);
    }

    // ─── Phase 3: 流式/保存点/缓存 ─────────────────────

    [Test]
    public async Task QueryAsyncEnumerable_StreamingWorks()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await InsertOrdersAsync(db, 5, i => new Order { Status = "E", Total = i * 10m, CreatedAt = 0 });
        var list = new List<Order>();
        await foreach (var o in db.QueryAsyncEnumerable<Order>($"SELECT * FROM orders")) { list.Add(o); }
        await Assert.That(list.Count).IsEqualTo(5);
    }

    [Test]
    public async Task RowFactory_VerifySnapshot()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var ins = await db.InsertAsync(new Order { Status = "RF", Total = 99m, CreatedAt = 1234567890 });
        var r = await db.GetAsync<Order>(ins.Id);
        await Assert.That(r!.Status).IsEqualTo("RF");
        await Assert.That(r.Total).IsEqualTo(99m);
        await Assert.That(r.CreatedAt).IsEqualTo(1234567890);
    }

    [Test]
    public async Task IQueryInterceptor_FiresCallbacks()
    {
        var interceptor = new CountingTestInterceptor();
        var opts = new DbOptions { ConnectionString = "Data Source=:memory:" };
        opts = opts with { Interceptors = [interceptor] };
        await using var db = await DataSession<SqliteProvider>.CreateAsync(opts);
        await db.MigrateAsync();
        await db.InsertAsync(new Order { Status = "I", Total = 1m, CreatedAt = 0 });
        await db.From<Order>().ToListAsync();
        await Assert.That(interceptor.BeforeCount).IsGreaterThan(0);
        await Assert.That(interceptor.AfterCount).IsGreaterThan(0);
    }

    [Test]
    public async Task ForUpdate_AppearsAfterLimit()
    {
        await using var db = await TestDb.SqliteAsync();
        var sql = db.From<Order>().Take(1).ForUpdate().ToSql();
        await Assert.That(sql.IndexOf("LIMIT", StringComparison.Ordinal)).IsLessThan(sql.IndexOf("FOR UPDATE", StringComparison.Ordinal));
    }

    [Test]
    public async Task MultipleSetClauses_UpdateOneStatement()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var order = await db.InsertAsync(new Order { Status = "Old", Total = 1m, CreatedAt = 0 });
        int rows = await db.From<Order>().Set(item => item.Status, "New").Set(item => item.Total, 2m).Where($"id = {order.Id}").ExecuteNonQueryAsync();
        var updated = await db.GetAsync<Order>(order.Id);
        await Assert.That(rows).IsEqualTo(1);
        await Assert.That(updated!.Status).IsEqualTo("New");
        await Assert.That(updated.Total).IsEqualTo(2m);
    }

    [Test]
    public async Task ExecuteNonQuery_WithoutSetFailsBeforeDatabase()
    {
        await using var db = await TestDb.SqliteAsync();
        await Assert.That(async () => await db.From<Order>().Where($"id = {1}").ExecuteNonQueryAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task PartialEntityProjection_ExecutionIsRejected()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await Assert.That(async () => await db.From<Order>().Select(item => item.Status).ToListAsync()).Throws<NotSupportedException>();
    }

    // ITM-555 下沉：键集续页条件必须约束用户 OR 组的全部分支——页间无重复、可推进到穷尽
    [Test]
    public async Task ToPageAsync_KeysetConditionConstrainsAllOrBranches()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await InsertOrdersAsync(db, 6, i => new Order { Status = i % 2 == 0 ? "A" : "B", Total = i * 10m, CreatedAt = 0 });
        var seen = new HashSet<long>();
        long? last = null;
        long total = 0;
#pragma warning disable PALORM005 // 分页循环本质就是逐页查询，非 N+1（ITM-574 已登记该误报形态）
        for (int page = 0; page < 10; page++)
        {
            var query = db.From<Order>().Where($"status = {"A"}").OrWhere($"status = {"B"}");
            var (rows, pageTotal) = last is null
                ? await query.ToPageAsync(2, o => o.Id)
                : await query.ToPageAsync(2, o => o.Id, last.Value);
            total = pageTotal;
            if (rows.Count == 0) break;
            foreach (var row in rows)
                await Assert.That(seen.Add(row.Id)).IsTrue();  // 页间重复 = ITM-555 回归
            last = rows[^1].Id;
        }
#pragma warning restore PALORM005
        await Assert.That(seen.Count).IsEqualTo(6);
        await Assert.That(total).IsEqualTo(6);
    }

    // ITM-556 下沉：BulkUpdate 中途冲突整批回滚后，已成功条目重试不得假冲突
    [Test]
    public async Task BulkUpdate_RollbackKeepsInMemoryVersionInSync()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var a = await db.InsertAsync(new VersionedEntity { Name = "A", Version = 0 });
        var b = await db.InsertAsync(new VersionedEntity { Name = "B", Version = 0 });
        var bFresh = await db.GetAsync<VersionedEntity>(b.Id);
        bFresh!.Name = "B-bump";
        await db.UpdateAsync(bFresh);  // DB: B.version=1，b 成为 stale
        a.Name = "A-bulk";
        b.Name = "B-bulk";
        await Assert.That(async () => await db.BulkUpdateAsync([a, b])).Throws<ConcurrencyConflictException>();
        await Assert.That(a.Version).IsEqualTo(0);  // 回滚后内存 version 未被抬高
        a.Name = "A-retry";
        int rows = await db.UpdateAsync(a);  // 修复前此处抛假 ConcurrencyConflictException
        await Assert.That(rows).IsEqualTo(1);
        await Assert.That(a.Version).IsEqualTo(1);
    }

    // ITM-556 对照：全部成功时 version 正常回填
    [Test]
    public async Task BulkUpdate_SuccessBackfillsVersions()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var a = await db.InsertAsync(new VersionedEntity { Name = "A", Version = 0 });
        var b = await db.InsertAsync(new VersionedEntity { Name = "B", Version = 0 });
        a.Name = "A2"; b.Name = "B2";
        long total = await db.BulkUpdateAsync([a, b]);
        await Assert.That(total).IsEqualTo(2);
        await Assert.That(a.Version).IsEqualTo(1);
        await Assert.That(b.Version).IsEqualTo(1);
    }

    private static async Task<List<Order>> InsertOrdersAsync(DataSession<SqliteProvider> db, int count, Func<int, Order> create)
    {
        var orders = new List<Order>(count);
        for (int i = 0; i < count; i++)
        {
#pragma warning disable PALORM005 // 仅批量布置测试数据；此循环不调用 From<T>() 或执行查询。
            orders.Add(await db.InsertAsync(create(i)));
#pragma warning restore PALORM005
        }
        return orders;
    }
}

#region Test Entities
[Table("orders")]
public partial class Order
{
    [Key] public long Id { get; set; }
    [Column("status")] public string Status { get; set; } = "";
    [Column("total")] public decimal Total { get; set; }
    [Column("created_at")] public long CreatedAt { get; set; }
}
#endregion
