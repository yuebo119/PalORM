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
}
