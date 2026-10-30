using System.Data.Common;
using Marten;

namespace PalORM.PerfHub;

/// <summary>Marten 臂（清单条目 marten，9.14.1）——PG-only 文档数据库，仅 pg 方言注册。
/// <para>形态决策：
/// <list type="bullet">
/// <item><b>存储</b>：DocumentStore.For(Connection + AutoCreateSchemaObjects=All)——
/// AotProbe 适配器同配置；文档 = mt_doc_martens1 的 jsonb，无 SQL 表位可比
///（与其他臂测的是"同数据的文档化存取"，报告单列注记）。</item>
/// <item><b>会话</b>：LightweightSession（无变更追踪的官方轻量会话）per 操作；
/// 写路径 = Store + SaveChangesAsync（SaveChanges 即事务单元，Marten 对大批自动
/// 优化为批处理写入——官方批量路径；Marten 9 无文档 BulkInsert API，仅事件流）。</item>
/// <item><b>集合式删除</b>：DeleteWhere（表达式翻译集合式 DELETE，官方路径）。</item>
/// <item><b>回滚</b>：Marten 事务语义 = 不调 SaveChanges 即丢弃 pending
///（Dispose 等价回滚，AotProbe 适配器实证）。</item>
/// <item><b>状态管理</b>：与 LiteDb 臂同款自管快照（SetupAsync 建内存快照、
/// 写置脏、读入口重建）。</item>
/// </list></para></summary>
internal sealed class MartenImpl : IPerfImplementation, IDisposable
{
    private readonly DocumentStore _store;
    private List<MartenS1>? _snapshot;
    private bool _dirty;

    /// <summary>写校验期抑制脏复位（语义同 LiteDb 臂同名标志）。</summary>
    internal bool SuppressResetForValidation;

    public string Name => "Marten";

    /// <summary>Marten 文档实体（公共可写属性 + Id 成员约定；decimal→numeric）。</summary>
    public class MartenS1
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public int Qty { get; set; }

        public decimal Price { get; set; }

        public long Marker { get; set; }

        internal static MartenS1 From(S1Row row) =>
            new() { Id = row.Id, Name = row.Name, Qty = row.Qty, Price = row.Price, Marker = row.Marker };
    }

    /// <summary>枚举文档实体——Status 字符串列（每行 ToString 转换，与地板形态同构）。</summary>
    public class MartenEnum
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public string Status { get; set; } = "";
    }

    public MartenImpl(DialectInfo dialect)
    {
        if (dialect.Dialect != Dialect.PostgreSql)
        {
            throw new NotSupportedException("Marten 是 PG-only 文档库——仅 pg 方言注册（清单钦定）");
        }

        string cs = Connections.Resolve(dialect);
        _store = DocumentStore.For(options =>
        {
            options.Connection(cs);
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.All;
        });
    }

    public void Dispose() => _store.Dispose();

    private async Task ResetIfDirtyAsync()
    {
        if (SuppressResetForValidation || !_dirty || _snapshot is null)
        {
            return;
        }

        await using IDocumentSession session = _store.LightweightSession();
        session.DeleteWhere<MartenS1>(x => x.Id >= 0);
        session.Store(_snapshot.ToArray());
        await session.SaveChangesAsync().ConfigureAwait(false);
        _dirty = false;
    }

    public async Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
    {
        _snapshot = Dataset.SeedRows(rows).Select(MartenS1.From).ToList();
        await using IDocumentSession session = _store.LightweightSession();
        session.DeleteWhere<MartenS1>(x => x.Id >= 0);
        session.Store(_snapshot.ToArray());
        await session.SaveChangesAsync().ConfigureAwait(false);
        _dirty = false;
    }

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        await ResetIfDirtyAsync().ConfigureAwait(false);
        await using IDocumentSession session = _store.LightweightSession();
        MartenS1? found = await session.LoadAsync<MartenS1>(id, ct).ConfigureAwait(false);
        return found is null ? null : new S1Row
        {
            Id = found.Id,
            Name = found.Name,
            Qty = found.Qty,
            Price = found.Price,
            Marker = found.Marker
        };
    }

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        await ResetIfDirtyAsync().ConfigureAwait(false);
        await using IDocumentSession session = _store.LightweightSession();
        List<MartenS1> rows = [.. await session.Query<MartenS1>().ToListAsync(ct).ConfigureAwait(false)];
        return rows.Select(static r => new S1Row
        {
            Id = r.Id,
            Name = r.Name,
            Qty = r.Qty,
            Price = r.Price,
            Marker = r.Marker
        }).ToList();
    }

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        _dirty = true;
        await using IDocumentSession session = _store.LightweightSession();
        session.Store(MartenS1.From(row));
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量插入——Store 范围 + SaveChanges（Marten 9 官方批量路径：大批自动
    /// 优化为批处理写入；无文档 BulkInsert API）。</summary>
    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        _dirty = true;
        await using IDocumentSession session = _store.LightweightSession();
        session.Store(rows.Select(MartenS1.From).ToArray());
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        _dirty = true;
        await using IDocumentSession session = _store.LightweightSession();
        session.Store(MartenS1.From(row));
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return 1;
    }

    /// <summary>批量更新——Store 范围 + SaveChanges（文档 upsert 语义：已存在 Id = 整文档覆写，
    /// Marten 官方更新路径）。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        _dirty = true;
        await using IDocumentSession session = _store.LightweightSession();
        session.Store(rows.Select(MartenS1.From).ToArray());
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary>批量删除——DeleteWhere 表达式（1000/批与 BulkSql.BatchRows 对齐，
    /// 每批独立 SaveChanges 事务单元）。</summary>
    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        _dirty = true;
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

            await using IDocumentSession session = _store.LightweightSession();
            session.DeleteWhere<MartenS1>(x => ids.Contains(x.Id));
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
            total += ids.Length;
        }

        return total;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => "Marten LoadAsync<T>(id)（主键文档直取）";

    public string BuildComplexQuerySql(DbConnection conn)
        => "Marten LINQ（Query<T> Where/OrderBy/Take）";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        await ResetIfDirtyAsync().ConfigureAwait(false);
        await using IDocumentSession session = _store.LightweightSession();
        List<MartenS1> rows = [.. (await session.Query<MartenS1>()
            .Where(x => x.Id > lastId)
            .OrderBy(x => x.Id)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false))];
        return rows.Select(static r => new S1Row
        {
            Id = r.Id,
            Name = r.Name,
            Qty = r.Qty,
            Price = r.Price,
            Marker = r.Marker
        }).ToList();
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        await ResetIfDirtyAsync().ConfigureAwait(false);
        await using IDocumentSession session = _store.LightweightSession();
        List<MartenS1> rows = [.. (await session.Query<MartenS1>()
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false))];
        return rows.Select(static r => new S1Row
        {
            Id = r.Id,
            Name = r.Name,
            Qty = r.Qty,
            Price = r.Price,
            Marker = r.Marker
        }).ToList();
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
    {
        await ResetIfDirtyAsync().ConfigureAwait(false);
        await using IDocumentSession session = _store.LightweightSession();
        return await session.Query<MartenS1>().CountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量 UPSERT——Store + SaveChanges（文档库原生 upsert 语义）。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        _dirty = true;
        await using IDocumentSession session = _store.LightweightSession();
        session.Store(rows.Select(MartenS1.From).ToArray());
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    public Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
        => throw new NotSupportedException("InsertReturningId 不在本次子集内");

    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("WideQueryAll 不在本次子集内");

    public Task<(int Parents, int Children)> IncludeJoinAsync(DbConnection conn, int parentCount, CancellationToken ct)
        => throw new NotSupportedException("IncludeJoin 不在本次子集内");

    /// <summary>事务——SaveChanges 即事务单元（Marten 官方形态：一个 SaveChanges 一个事务）。</summary>
    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        _dirty = true;
        await using IDocumentSession session = _store.LightweightSession();
        session.Store(MartenS1.From(row));
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        _dirty = true;
        await using IDocumentSession session = _store.LightweightSession();
        session.Store(rows.Select(MartenS1.From).ToArray());
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        _dirty = true;
        await using IDocumentSession session = _store.LightweightSession();
        session.Store(rows.Select(MartenS1.From).ToArray());
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>回滚——Store 后不调 SaveChanges，Dispose 丢弃 pending
    ///（Marten 事务语义的官方回滚等价，AotProbe 适配器实证）。</summary>
    public Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => Task.CompletedTask;

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

    /// <summary>枚举列事务内逐条插入——Status 字符串 + Store/SaveChanges（Marten 单文档
    /// 事务单元形态；与地板逐行形态的可比性在报告注记）。</summary>
    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        await using IDocumentSession session = _store.LightweightSession();
        session.Store(rows.Select(r => new MartenEnum
        {
            Id = r.Id,
            Name = r.Name,
            Status = r.Status.ToString()
        }).ToArray());
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct) => QueryAllAsync(conn, ct);
}
