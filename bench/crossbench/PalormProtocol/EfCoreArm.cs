using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace PalORM.PerfHub;

/// <summary>EF Core 臂（清单条目 efcore，EF Core 10.0.14 线）。
/// <para>形态决策：
/// <list type="bullet">
/// <item><b>DbContext 生命周期</b>：每操作单元一个 DbContext（EF 的官方用法——
/// "short-lived context"），options 构建一次臂内复用（模型/服务提供者缓存跨 context 生效，
/// 模型构建成本只付一次，测的是稳态）。</item>
/// <item><b>读路径</b>：AsNoTracking（EF 官方只读最佳实践）。</item>
/// <item><b>批量</b>：EF 无原生 bulk API——BulkInsert = AddRange + SaveChanges（自动批处理，
/// 官方路径）；BulkUpdate = UpdateRange + SaveChanges（逐行值官方唯一路径）；
/// BulkDelete = ExecuteDeleteAsync（EF7+ 集合式，官方路径）。</item>
/// <item><b>UpsertBatch</b>：EF Core 无 upsert API，用官方「查已有行 → 改属性/Add →
/// SaveChanges」路径（2026-11-01 审计起，替换原 ExecuteSqlRaw 手写 SQL 通道）。</item>
/// <item><b>MySQL provider</b>：Oracle 官方 MySql.EntityFrameworkCore 10.0.12（镜像无 Pomelo
/// EF10 线）；连接串与连接池由 EF 管理。</item>
/// <item><b>枚举列</b>：HasConversion&lt;string&gt; 官方形态；EnumInserts 为事务内
/// AddRange + SaveChanges（EF 的批量写原生形态，单 SaveChanges 自动批处理）。</item>
/// </list></para></summary>
internal sealed class EfCoreImpl(DialectInfo dialect) : IPerfImplementation
{
    private readonly Dialect _d = dialect.Dialect;
    private readonly DbContextOptions<BenchDbContext> _options = BuildOptions(dialect);

    public string Name => "EfCore";

    private static DbContextOptions<BenchDbContext> BuildOptions(DialectInfo dialect)
    {
        string cs = Connections.Resolve(dialect);
        var builder = new DbContextOptionsBuilder<BenchDbContext>();
        _ = dialect.Dialect switch
        {
            Dialect.Sqlite => builder.UseSqlite(cs),
            Dialect.MySql => builder.UseMySQL(cs),
            Dialect.PostgreSql => builder.UseNpgsql(cs),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect.Dialect, null)
        };
        return builder.Options;
    }

    internal sealed class BenchDbContext(DbContextOptions<BenchDbContext> options) : DbContext(options)
    {
        public DbSet<S1Row> S1 => Set<S1Row>();
        public DbSet<EnumRow> Enums => Set<EnumRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<S1Row>(e =>
            {
                e.ToTable("perf_s1");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<EnumRow>(e =>
            {
                e.ToTable("perf_enum");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Status).HasConversion<string>();
            });
        }
    }

    private BenchDbContext New() => new(_options);

    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct) => Task.CompletedTask;

    /// <summary>单键直查——AsNoTracking + FirstAsync（官方只读路径；FindAsync 走本地缓存，
    /// 不适合无追踪基准）。</summary>
    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        return await db.S1.AsNoTracking().FirstAsync(x => x.Id == id, ct).ConfigureAwait(false);
    }

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        return await db.S1.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        _ = db.S1.Add(row);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量插入——AddRange + SaveChanges（EF 自动批处理，官方唯一路径；
    /// EF 无 COPY/LOAD DATA 类 bulk API）。</summary>
    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        db.S1.AddRange(rows);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        _ = db.S1.Update(row);
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量更新——UpdateRange + SaveChanges：逐行值的官方唯一路径
    /// （ExecuteUpdate 是集合式单 SET 表达式，不适用逐行异值）。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        db.S1.UpdateRange(rows);
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量删除——ExecuteDeleteAsync（EF7+ 集合式，1000/批 IN 分块，
    /// 批宽与 BulkSql.BatchRows 对齐）。</summary>
    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        long total = 0;
        const int batch = BulkSql.BatchRows;
        for (int start = 0; start < keys.Count; start += batch)
        {
            int end = Math.Min(start + batch, keys.Count);
            long[] ids = new long[end - start];
            for (int i = start; i < end; i++)
            {
                ids[i - start] = Convert.ToInt64(keys[i], System.Globalization.CultureInfo.InvariantCulture);
            }

            await using BenchDbContext db = New();
            total += await db.S1.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        return total;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => "EF Core LINQ 翻译（AsNoTracking().First(Id == key)）";

    public string BuildComplexQuerySql(DbConnection conn)
        => "EF Core LINQ 翻译（Where/OrderBy/Take 组合）";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        return await db.S1.AsNoTracking()
            .Where(x => x.Id > lastId)
            .OrderBy(x => x.Id)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        return await db.S1.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        return await db.S1.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量 UPSERT——EF Core 没有 upsert API（无 Merge/OnConflict 对应物），
    /// 官方唯一路径是「查出已存在行 → 改属性 → 新行 Add → SaveChanges」。
    /// <para><b>2026-11-01 审计按「只用 ORM API」原则替换</b>：原实现走
    /// <c>Database.ExecuteSqlRawAsync</c> + BulkSql 手写多值 UPSERT，等于用 ADO 通道测 EF，
    /// 与本项目「测 ORM 不是测 SQL」的宗旨冲突，也与其余组（FreeSql/RepoDb/SqlSugar/PalORM
    /// 均用各自 API）不可比。代价是这一项对 EF 天然包含一次 SELECT 往返（换成 API 形态的
    /// 必然成本），报告说明列已注明。</para></summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        long[] ids = [.. rows.Select(r => r.Id)];
        await using BenchDbContext db = New();
        Dictionary<long, S1Row> existing = (await db.S1
                .Where(x => ids.Contains(x.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(x => x.Id);

        int affected = 0;
        foreach (S1Row row in rows)
        {
            if (existing.TryGetValue(row.Id, out S1Row? tracked))
            {
                tracked.Name = row.Name;
                tracked.Qty = row.Qty;
                tracked.Price = row.Price;
                tracked.Marker = row.Marker;
                affected++;
            }
            else
            {
                _ = db.S1.Add(row);
                affected++;
            }
        }

        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return affected;
    }

    public Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
        => throw new NotSupportedException("InsertReturningId 不在本次子集内");

    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("WideQueryAll 不在本次子集内");

    public Task<(int Parents, int Children)> IncludeJoinAsync(DbConnection conn, int parentCount, CancellationToken ct)
        => throw new NotSupportedException("IncludeJoin 不在本次子集内");

    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        _ = db.S1.Add(row);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    /// <summary>事务内 100 条——AddRange + 单 SaveChanges（EF 批量写原生形态：一个
    /// SaveChanges 把 100 条 INSERT 自动批处理，这是 EF 用户在事务内写 N 行的官方写法）。</summary>
    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        await using BenchDbContext db = New();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.S1.AddRange(rows);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.S1.AddRange(rows);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.S1.UpdateRange(rows);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.RollbackAsync(ct).ConfigureAwait(false);
    }

    public Task<long> TenantCountAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("租户族不在本次子集内");

    public Task<long> TenantCountWhereAsync(DbConnection conn, int rows, CancellationToken ct)
        => throw new NotSupportedException("租户族不在本次子集内");

    public Task<List<BenchTenantPost>> TenantGetAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("租户族不在本次子集内");

    public Task<List<BenchTenantPost>> OwnedJsonQueryAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("OwnedJson 不在本次子集内");

    public Task<int> SessionBatchInsertsAsync(DbConnection conn, int offset, CancellationToken ct)
        => throw new NotSupportedException("SessionBatch 不在本次子集内");

    /// <summary>枚举列事务内插入——HasConversion&lt;string&gt; + AddRange + 单 SaveChanges
    /// （EF 批量写原生形态）。</summary>
    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        await using BenchDbContext db = New();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Enums.AddRange(rows);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct) => QueryAllAsync(conn, ct);
}
