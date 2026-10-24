using System.Text.Json.Serialization;
using PalORM.AotModels.First;
using PalORM.AotModels.Second;
using PalORM.Sqlite;

namespace PalORM.AotTest;

[Table("aot_test")]
internal sealed partial class AotEntity
{
    [Key] public long Id { get; set; }
    [Column("value")] public string Value { get; set; } = "";
    [Column("version")][ConcurrencyCheck] public long Version { get; set; }
    // CA1819 误报：ORM 实体列需要可变数组读写
#pragma warning disable CA1819
    [Column("blob")] public byte[] Blob { get; set; } = [];
#pragma warning restore CA1819
    [Column("preview")] public byte[]? Preview { get; set; }
}

[SoftDelete]
[Table("aot_bulk_test")]
internal sealed partial class AotBulkEntity
{
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
    [Column("created_by")][IgnoreOnInsert] public string CreatedBy { get; set; } = "client";
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}

[Table("aot_json_test")]
internal sealed partial class AotJsonEntity
{
    [Key] public long Id { get; set; }
    [Column("details")][OwnedJson(typeof(AotJsonContext))] public AotDetails Details { get; set; } = new();
}

internal sealed class AotDetails
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

[JsonSerializable(typeof(AotDetails), TypeInfoPropertyName = "AotDetailsInfo")]
internal sealed partial class AotJsonContext : JsonSerializerContext;

// 表达式构建器冒烟用的父子对（Include/ThenInclude 只发 JOIN，不要求导航属性）
[Table("aot_parent")]
internal sealed partial class AotParentEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

[Table("aot_child")]
internal sealed partial class AotChildEntity
{
    [Key] public long Id { get; set; }
    [Column("parent_id")] public long ParentId { get; set; }
    [Column("label")] public string Label { get; set; } = "";
    [Column("weight")] public long Weight { get; set; }
}

// R1（v6.0）：[Projection] DTO 物化的 AOT 全链验证载体（只读类型，无表无写命令；
// 顶层声明——嵌套类型与实体同规被拒绝，PALORM045 兜底实测）
[Projection]
internal sealed class AotSummary
{
    public long Id { get; set; }
    public string Value { get; set; } = "";
}

// 租户直绑（S5）的 AOT 载体：插入自动绑定 WithTenant 值，读取自动按租户过滤
[TenantAware]
[Table("aot_tenant_test")]
internal sealed partial class AotTenantEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("tenant_id")] public long TenantId { get; set; }
}

internal static class Program
{
    internal static async Task Main()
    {
        // 文件型数据库而非 :memory:（ITM-317）——WAL journal_mode 仅对文件库生效，
        // native sqlite3mc 加载 + PRAGMA 初始化钩子在 AOT 下的完整路径只有文件库能验证
        string dbPath = Path.Combine(Path.GetTempPath(), $"palorm-aot-{Environment.ProcessId}.db");
        var options = new DbOptions { ConnectionString = $"Data Source={dbPath}" };
        try
        {
            await RunAsync(options).ConfigureAwait(false);
        }
        finally
        {
            // Windows 下连接池持有文件句柄，File.Delete 抛 IOException（Linux 可 unlink
            // 开启句柄的文件故 CI 无此问题）——清池后再删，保证本地与 CI 行为一致
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            File.Delete(dbPath + "-wal");
            File.Delete(dbPath + "-shm");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303",
        Justification = "固定英文文本是 Native AOT smoke test 的机器可读成功标记。")]
    private static async Task RunAsync(DbOptions options)
    {
        DataSession<SqliteProvider> db = await DataSession<SqliteProvider>.CreateAsync(options).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.MigrateAsync().ConfigureAwait(false);
            await db.ExecuteAsync(
                $"DROP TABLE aot_bulk_test").ConfigureAwait(false);
            await db.ExecuteAsync(
                $"CREATE TABLE aot_bulk_test (Id TEXT PRIMARY KEY, name TEXT NOT NULL, created_by TEXT NOT NULL DEFAULT 'database', deleted_at TEXT)")
                .ConfigureAwait(false);

            AotEntity inserted = await db.InsertAsync(new AotEntity
            {
                Value = "AOT works!",
                Version = 0,
                Blob = [0x00, 0x01, 0xFF, 0x00],
                Preview = null
            }).ConfigureAwait(false);
            if (inserted.Id <= 0)
                throw new InvalidOperationException("INSERT failed");
            if (await db.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM aot_test WHERE id = {inserted.Id:N0}")
                    .ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Composite format parameterization failed");

            AotEntity first = await db.GetAsync<AotEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("GET failed");
            await VerifyBinaryColumnAsync(db, inserted, first).ConfigureAwait(false);
            await VerifyProjectionAsync(db, inserted.Id).ConfigureAwait(false);
            AotEntity stale = await db.GetAsync<AotEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Stale GET failed");

            first.Value = "AOT updated";
            int updated = await db.UpdateAsync(first).ConfigureAwait(false);
            if (updated != 1 || first.Version != 1)
                throw new InvalidOperationException("UPDATE failed");

            stale.Value = "stale";
            try
            {
                await db.UpdateAsync(stale).ConfigureAwait(false);
                throw new InvalidOperationException("Concurrency conflict was not detected");
            }
            catch (ConcurrencyConflictException)
            {
                // S108: 测试期望此异常——stale row 必须被并发控制拒绝。空 catch 即断言成功。
            }

            AotJsonEntity json = await db.InsertAsync(new AotJsonEntity
            {
                Details = new AotDetails { Name = "source-generated", Count = 7 }
            }).ConfigureAwait(false);
            AotJsonEntity jsonRoundTrip = await db.GetAsync<AotJsonEntity>(json.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OwnedJson GET failed");
            if (jsonRoundTrip.Details.Name != "source-generated" || jsonRoundTrip.Details.Count != 7)
                throw new InvalidOperationException("OwnedJson round trip failed");

            FirstEntity firstModel = await db.InsertAsync(new FirstEntity { Name = "first assembly" }).ConfigureAwait(false);
            SecondEntity secondModel = await db.InsertAsync(new SecondEntity { Value = 2 }).ConfigureAwait(false);
            if (await db.GetAsync<FirstEntity>(firstModel.Id).ConfigureAwait(false) is null
                || await db.GetAsync<SecondEntity>(secondModel.Id).ConfigureAwait(false) is null)
                throw new InvalidOperationException("Cross-assembly registry merge failed");

            var bulkEntities = new[]
            {
                new AotBulkEntity { Id = "first", Name = "first" },
                new AotBulkEntity { Id = "second", Name = "second" }
            };
            if (await db.BulkInsertAsync(bulkEntities).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("Bulk INSERT failed");
            AotBulkEntity? bulk = await db.GetAsync<AotBulkEntity>("first").ConfigureAwait(false);
            if (bulk?.CreatedBy != "database")
                throw new InvalidOperationException("Bulk INSERT metadata failed");
            if (await db.BulkDeleteAsync<AotBulkEntity>(["first", "second"]).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("Bulk soft DELETE failed");
            AotBulkEntity? softDeleted = await db.IgnoreFilters()
                .GetAsync<AotBulkEntity>("first").ConfigureAwait(false);
            if (softDeleted?.DeletedAt is null)
                throw new InvalidOperationException("Bulk soft DELETE metadata failed");

            await VerifyExpressionBuildersAsync(db).ConfigureAwait(false);

            await VerifyAggregatePagingStreamingAsync(db).ConfigureAwait(false);
            await VerifyExplicitTransactionAsync(db).ConfigureAwait(false);
            await VerifyTenantIsolationAsync(options).ConfigureAwait(false);
            await VerifyPoolAndInterceptorAsync(db, options).ConfigureAwait(false);

            int deleted = await db.DeleteAsync<AotEntity>(inserted.Id).ConfigureAwait(false);
            if (deleted != 1 || await db.GetAsync<AotEntity>(inserted.Id).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("DELETE failed");
        }

        Console.WriteLine("PalORM AOT verification PASSED");
    }

    /// <summary>表达式构建器的原生 AOT 覆盖。
    /// <para>此前 <c>OrderBy</c>/<c>ThenBy</c>/<c>OrderByDescending</c>/<c>Select</c>/<c>GroupBy</c>/
    /// <c>Having</c>/<c>WhereIn</c>/<c>WhereNotIn</c>/<c>Set</c>/<c>Include</c>/<c>ThenInclude</c>/<c>With(CTE)</c>/
    /// <c>UnsafeWindowOver</c>/<c>ForUpdate</c>/<c>ForShare</c>/<c>WithCache</c>/<c>AsPrepared</c>/<c>Tag</c>
    /// 从未被任何 AOT 程序调用——它们的 AOT 兼容性此前是**未验证的声明**。本方法在原生二进制里
    /// 执行这些路径并校验结果（与 JIT 侧 <c>ExpressionBuilderSmokeTests</c> 同一套断言）。</para>
    /// <para>锁语句按 ITM-639 的登记契约**只验形态不执行**：SQLite 不支持 FOR UPDATE/SHARE，
    /// 构建期故意不拒绝（既定契约允许在 SQLite 上预览面向 PG/MySQL 的锁语句形态），
    /// 执行会报 <c>SQLite Error 1: near "FOR": syntax error</c>。</para></summary>
    private static async Task VerifyExpressionBuildersAsync(DataSession<SqliteProvider> db)
    {
        AotParentEntity parent = await db.InsertAsync(new AotParentEntity { Name = "p0" }).ConfigureAwait(false);
        // 逐条插入：循环内 DB 调用会被 PALORM005（N+1 检测）拦下——分析器行为正确，
        // 冒烟只需 3 行数据，展开即可，不必为此改走 bulk 路径（那会混淆本方法的覆盖点）。
        await db.InsertAsync(new AotChildEntity { ParentId = parent.Id, Label = "c0", Weight = 0 }).ConfigureAwait(false);
        await db.InsertAsync(new AotChildEntity { ParentId = parent.Id, Label = "c1", Weight = 1 }).ConfigureAwait(false);
        await db.InsertAsync(new AotChildEntity { ParentId = parent.Id, Label = "c2", Weight = 2 }).ConfigureAwait(false);

        await VerifySortingFilteringAndProjectionAsync(db, parent).ConfigureAwait(false);
        await VerifyJoinsAndCteAsync(db).ConfigureAwait(false);
        await VerifyLocksCachingAndSplitQueryAsync(db).ConfigureAwait(false);
    }

    private static async Task VerifySortingFilteringAndProjectionAsync(
        DataSession<SqliteProvider> db, AotParentEntity parent)
    {
        List<AotChildEntity> page = await db.From<AotChildEntity>()
            .Where($"parent_id = {parent.Id}")
            .OrderByDescending(x => x.Label)
            .ThenBy(x => x.Id)
            .Skip(1)
            .Take(1)
            .ToListAsync().ConfigureAwait(false);
        if (page.Count != 1 || page[0].Label != "c1")
            throw new InvalidOperationException("OrderByDescending/ThenBy/Skip/Take failed");

        List<AotChildEntity> inList = await db.From<AotChildEntity>()
            .WhereIn(x => x.Label, ["c0", "c2"]).ToListAsync().ConfigureAwait(false);
        List<AotChildEntity> notInList = await db.From<AotChildEntity>()
            .WhereNotIn(x => x.Label, ["c0"]).ToListAsync().ConfigureAwait(false);
        if (inList.Count != 2 || notInList.Count != 2)
            throw new InvalidOperationException("WhereIn/WhereNotIn failed");

        int renamed = await db.From<AotChildEntity>()
            .Set(x => x.Label, "renamed")
            .Where($"label = {"c2"}")
            .ExecuteNonQueryAsync().ConfigureAwait(false);
        if (renamed != 1)
            throw new InvalidOperationException("Set/ExecuteNonQueryAsync failed");

        string projectionSql = db.From<AotChildEntity>().Select(x => x.Id, x => x.Label).AsDryRun().Sql;
        if (!projectionSql.Contains("label", StringComparison.Ordinal)
            || projectionSql.Contains("weight", StringComparison.Ordinal))
            throw new InvalidOperationException("Select projection failed");

        string groupedSql = db.From<AotChildEntity>()
            .GroupBy(x => x.ParentId).Having($"COUNT(*) > {0}").AsDryRun().Sql;
        if (!groupedSql.Contains("GROUP BY", StringComparison.Ordinal)
            || !groupedSql.Contains("HAVING", StringComparison.Ordinal))
            throw new InvalidOperationException("GroupBy/Having failed");
    }

    private static async Task VerifyJoinsAndCteAsync(DataSession<SqliteProvider> db)
    {
        var withInclude = db.From<AotChildEntity>()
            .Include<AotParentEntity>(c => c.ParentId, p => p.Id)
            .Where($"label = {"c0"}");
        if (!withInclude.AsDryRun().Sql.Contains("aot_parent", StringComparison.Ordinal)
            || (await withInclude.ToListAsync().ConfigureAwait(false)).Count != 1)
            throw new InvalidOperationException("Include failed");

        var withThenInclude = db.From<AotChildEntity>()
            .ThenInclude<AotParentEntity, AotChildEntity>(p => p.Id, c => c.ParentId);
        if ((await withThenInclude.ToListAsync().ConfigureAwait(false)).Count == 0)
            throw new InvalidOperationException("ThenInclude failed");

        List<AotChildEntity> cteRows = await db.From<AotChildEntity>()
            .With("aot_cte", $"SELECT * FROM aot_child WHERE weight >= {1}")
            .ToListAsync().ConfigureAwait(false);
        if (cteRows.Count != 2 || cteRows.Any(static row => row.Weight < 1))
            throw new InvalidOperationException("With(CTE) failed");

        List<AotChildEntity> window = await db.From<AotChildEntity>()
            .UnsafeWindowOver("ROW_NUMBER()", "PARTITION BY parent_id ORDER BY id")
            .Take(1).ToListAsync().ConfigureAwait(false);
        if (window.Count != 1)
            throw new InvalidOperationException("UnsafeWindowOver failed");
    }

    private static async Task VerifyLocksCachingAndSplitQueryAsync(DataSession<SqliteProvider> db)
    {
        // 锁语句：只验形态（ITM-639）——SQLite 执行期报语法错误是登记契约
        if (!db.From<AotChildEntity>().ForUpdate().AsDryRun().Sql.Contains("FOR UPDATE", StringComparison.Ordinal)
            || !db.From<AotChildEntity>().ForUpdate(skipLocked: true).AsDryRun().Sql
                .Contains("SKIP LOCKED", StringComparison.Ordinal)
            || !db.From<AotChildEntity>().ForShare().AsDryRun().Sql.Contains("FOR SHARE", StringComparison.Ordinal))
            throw new InvalidOperationException("ForUpdate/ForShare SQL shape failed");

        var cached = db.From<AotChildEntity>()
            .WithCache("aot-cache", TimeSpan.FromSeconds(5)).Where($"label = {"c0"}");
        List<AotChildEntity> cacheFirst = await cached.ToListAsync().ConfigureAwait(false);
        List<AotChildEntity> cacheSecond = await cached.ToListAsync().ConfigureAwait(false); // 第二次走缓存
        if (cacheFirst.Count != 1 || cacheSecond.Count != 1)
            throw new InvalidOperationException("WithCache failed");

        if ((await db.From<AotChildEntity>().AsPrepared().Where($"label = {"c0"}")
                .ToListAsync().ConfigureAwait(false)).Count != 1)
            throw new InvalidOperationException("AsPrepared failed");

        if (!db.From<AotChildEntity>().Tag("aot-smoke").AsDryRun().Sql
                .Contains("aot-smoke", StringComparison.Ordinal))
            throw new InvalidOperationException("Tag failed");

        var split = db.From<AotChildEntity>()
            .Include<AotParentEntity>(c => c.ParentId, p => p.Id)
            .Where($"label = {"c0"}")
            .AsSplitQuery();
        if (split.AsDryRun().Sql.Contains("JOIN", StringComparison.Ordinal)
            || (await split.ToListAsync().ConfigureAwait(false)).Count != 1)
            throw new InvalidOperationException("AsSplitQuery failed");
    }

    /// <summary>聚合、键集分页、流式物化、多结果集 GridReader、部分更新、更新形态 DryRun 的
    /// 原生 AOT 执行验证——此前这些族的 AOT 兼容性是未验证声明（对账缺口 r25）。</summary>
    private static async Task VerifyAggregatePagingStreamingAsync(DataSession<SqliteProvider> db)
    {
        // aot_child 此时有 3 行（c0/c1/renamed）——表达式构建器验证已铺好数据
        if (await db.CountAsync<AotChildEntity>().ConfigureAwait(false) != 3)
            throw new InvalidOperationException("CountAsync failed");

        (List<AotChildEntity> firstPage, long total) = await db.From<AotChildEntity>()
            .ToPageAsync(2, static c => c.Id, descending: true).ConfigureAwait(false);
        if (total != 3 || firstPage.Count != 2)
            throw new InvalidOperationException("ToPageAsync first page failed");
        // 键集续页锚点是上一页**末行**的 orderBy 键值（ToPageAsync 契约），不是首行
        List<AotChildEntity> secondPage = (await db.From<AotChildEntity>()
                .ToPageAsync(2, static c => c.Id, firstPage[^1].Id, descending: true).ConfigureAwait(false))
            .Rows;
        if (secondPage.Count != 1 || secondPage[0].Id >= firstPage[^1].Id)
            throw new InvalidOperationException("ToPageAsync keyset continuation failed");

        int streamed = 0;
        await foreach (AotChildEntity _ in db.QueryAsyncEnumerable<AotChildEntity>(
                           $"SELECT * FROM aot_child").ConfigureAwait(false))
            streamed++;
        if (streamed != 3)
            throw new InvalidOperationException("QueryAsyncEnumerable failed");

        // CA2007：await using 的隐式 Dispose await 无法挂 ConfigureAwait，改 try/finally 显式释放
        GridReader grid = await db.From<AotChildEntity>()
            .QueryMultipleAsync($"SELECT * FROM aot_child; SELECT * FROM aot_child WHERE label = {"c0"}")
            .ConfigureAwait(false);
        try
        {
            List<AotChildEntity> all = await grid.ReadAsync<AotChildEntity>().ConfigureAwait(false);
            AotChildEntity? single = await grid.ReadFirstAsync<AotChildEntity>().ConfigureAwait(false);
            if (all.Count != 3 || single?.Label != "c0")
                throw new InvalidOperationException("QueryMultipleAsync/GridReader failed");
        }
        finally
        {
            await grid.DisposeAsync().ConfigureAwait(false);
        }

        AotEntity partial = await db.InsertAsync(new AotEntity { Value = "before" }).ConfigureAwait(false);
        partial.Value = "after";
        if (await db.UpdateColumnsAsync(partial, static e => e.Value).ConfigureAwait(false) != 1
            || await db.ScalarAsync<long>(
                   $"SELECT COUNT(*) FROM aot_test WHERE id = {partial.Id:N0} AND value = {"after"}")
               .ConfigureAwait(false) != 1)
            throw new InvalidOperationException("UpdateColumnsAsync failed");

        string dryRunUpdate = db.From<AotChildEntity>()
            .Set(static c => c.Label, "dry").Where($"label = {"renamed"}").AsDryRun().Sql;
        if (!dryRunUpdate.Contains("SET", StringComparison.Ordinal))
            throw new InvalidOperationException("update-shaped DryRun failed");
    }

    /// <summary>显式事务（Begin/Commit）+ savepoint 回滚的原生 AOT 执行验证。
    /// <para>lambda 式 <c>WithTransaction</c> 已由 PG 臂覆盖；SQLite 臂补显式形态与
    /// savepoint 分支（RollbackToAsync 路径在 JIT 侧由集成测试锁定，AOT 侧此前无锚点）。</para></summary>
    private static async Task VerifyExplicitTransactionAsync(DataSession<SqliteProvider> db)
    {
        // CA2007：await using 的隐式 Dispose await 无法挂 ConfigureAwait，改 try/finally 显式释放
        System.Data.Common.DbTransaction tx = await db.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            await db.InsertAsync(new AotEntity { Value = "tx-kept" }).ConfigureAwait(false);
            await db.SavepointAsync(tx, "sp1").ConfigureAwait(false);
            await db.InsertAsync(new AotEntity { Value = "tx-rolled-back" }).ConfigureAwait(false);
            await db.RollbackToAsync(tx, "sp1").ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
        }
        if (await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM aot_test WHERE value = {"tx-kept"}").ConfigureAwait(false) != 1
            || await db.ScalarAsync<long>(
                   $"SELECT COUNT(*) FROM aot_test WHERE value = {"tx-rolled-back"}").ConfigureAwait(false) != 0)
            throw new InvalidOperationException("explicit transaction + savepoint failed");
    }

    /// <summary>租户过滤的原生 AOT 执行验证：WithTenant 是**读取侧**过滤器
    /// （[TenantAware] 实体自动附加 WHERE tenant_id = @value，写入值来自实体属性——
    /// 与集成测试同口径）；租户作用域拼装是源生成物化路径的一部分。
    /// <para>独立会话：<see cref="DataSession{TProvider}.IgnoreFilters"/> 是会话粘性
    /// 标志且无公开复位 API（既定契约）——主冒烟流的软删断言已调用过，在同一会话上
    /// 做租户过滤验证会被静默跳过；独立会话同时覆盖同库多会话形态。方法末尾的
    /// IgnoreFilters 断言必须在会话的最后一个操作位（之后本会话不再做过滤查询）。</para></summary>
    private static async Task VerifyTenantIsolationAsync(DbOptions options)
    {
        DataSession<SqliteProvider> db = await DataSession<SqliteProvider>.CreateAsync(options)
            .ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            db.WithTenant(1L);
            await db.InsertAsync(new AotTenantEntity { Name = "t1-a", TenantId = 1 }).ConfigureAwait(false);
            await db.InsertAsync(new AotTenantEntity { Name = "t1-b", TenantId = 1 }).ConfigureAwait(false);
            await db.InsertAsync(new AotTenantEntity { Name = "t2-a", TenantId = 2 }).ConfigureAwait(false);

            List<AotTenantEntity> mine = await db.GetAllAsync<AotTenantEntity>().ConfigureAwait(false);
            List<AotTenantEntity> builderView = await db.From<AotTenantEntity>().ToListAsync().ConfigureAwait(false);
            if (mine.Count != 2 || builderView.Count != 2
                || mine.Any(static row => row.TenantId != 1L))
                throw new InvalidOperationException(
                    $"tenant filter failed: GetAll={mine.Count}, builder={builderView.Count}, "
                    + $"ids=[{string.Join(",", mine.Select(static r => r.TenantId))}]");

            db.WithTenant(2L);
            if ((await db.GetAllAsync<AotTenantEntity>().ConfigureAwait(false)).Count != 1)
                throw new InvalidOperationException("tenant isolation failed");

            // 必须是本会话最后一个过滤相关断言（粘性标志，见方法注释）
            if ((await db.IgnoreFilters().GetAllAsync<AotTenantEntity>().ConfigureAwait(false)).Count != 3)
                throw new InvalidOperationException("IgnoreFilters cross-tenant view failed");
        }
    }

    /// <summary>会话池（S7）与查询拦截器的原生 AOT 执行验证：池租还/复用与
    /// 拦截器委托分派此前均无 AOT 锚点（对账缺口 r25）。</summary>
    private static async Task VerifyPoolAndInterceptorAsync(
        DataSession<SqliteProvider> db, DbOptions options)
    {
        var interceptor = new CountingInterceptor();
        db.AddInterceptor(interceptor);
        // 拦截面在 builder 执行管线（ITM-582：COUNT 族不经过 IQueryInterceptor）
        _ = await db.From<AotChildEntity>().ToListAsync().ConfigureAwait(false);
        if (interceptor.BeforeCount == 0 || interceptor.AfterCount == 0)
            throw new InvalidOperationException("interceptor callbacks not dispatched");

        DataSessionPool<SqliteProvider> pool = DataSessionPool<SqliteProvider>.Create(options, maxIdleSize: 1);
        try
        {
            DataSession<SqliteProvider> rented = await pool.RentAsync().ConfigureAwait(false);
            AotEntity pooled = await rented.InsertAsync(new AotEntity { Value = "pooled" })
                .ConfigureAwait(false);
            if (pooled.Id <= 0)
                throw new InvalidOperationException("pooled session INSERT failed");
            // 租约会话 Dispose 即归还池（MarkPooled 机制，TryReturn 是 internal 通道）
            await rented.DisposeAsync().ConfigureAwait(false);

            DataSession<SqliteProvider> reused = await pool.RentAsync().ConfigureAwait(false);
            if (await reused.CountAsync<AotEntity>().ConfigureAwait(false) < 1)
                throw new InvalidOperationException("pooled session reuse failed");
            await reused.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>byte[] 列的 AOT 全链验证：参数化等值过滤（含 0x00 字节）+ 物化往返 + 可空列 NULL 守卫。</summary>
    private static async Task VerifyBinaryColumnAsync(
        DataSession<SqliteProvider> db, AotEntity inserted, AotEntity fetched)
    {
        if (await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM aot_test WHERE blob = {inserted.Blob}")
                .ConfigureAwait(false) != 1)
            throw new InvalidOperationException("byte[] equality parameterization failed");
        if (!fetched.Blob.AsSpan().SequenceEqual((ReadOnlySpan<byte>)[0x00, 0x01, 0xFF, 0x00])
            || fetched.Preview is not null)
            throw new InvalidOperationException("byte[] round trip failed");
    }

    /// <summary>R1（v6.0）：[Projection] DTO 物化的 AOT 全链验证——只读类型经源生成注册，
    /// QueryAsync 直接物化（ordinal 契约），不参与写命令与迁移 DDL。</summary>
    private static async Task VerifyProjectionAsync(
        DataSession<SqliteProvider> db, long id)
    {
        System.Collections.Generic.List<AotSummary> rows = await db.QueryAsync<AotSummary>(
                $"SELECT \"Id\", \"value\" AS \"Value\" FROM aot_test WHERE \"Id\" = {id}")
            .ConfigureAwait(false);
        if (rows.Count != 1 || rows[0].Id != id || rows[0].Value != "AOT works!")
            throw new InvalidOperationException("[Projection] materialization failed");
    }

    // 拦截器委托分派的 AOT 载体：OnError 契约为不抛（本冒烟无错误路径，空体即契约）
    private sealed class CountingInterceptor : IQueryInterceptor
    {
        internal int BeforeCount;
        internal int AfterCount;

        public void OnBefore(QueryContext context) => BeforeCount++;

        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) => AfterCount++;

        public void OnError(QueryContext context, Exception exception)
        {
        }
    }
}
