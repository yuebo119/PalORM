using System.Data.Common;
using LiteDB;

namespace PalORM.PerfHub;

/// <summary>LiteDB 臂（清单条目 litedb，5.0.21）——嵌入式文件文档库，方言无关。
/// <para>运行形态：<c>--dialects litedb</c> 伪方言（Runner 跳过 SQL 快照机制，
/// 本臂唯一注册）；存储 = 临时目录独立文件，连接形态 Connection=direct。
/// 与 SQL 臂的差异：无服务器网络栈、无 SQL 引擎（B-树文档 API），延迟量级天然不同——
/// 报告单列呈现，不做同表位直接比对（注记）。</para>
/// <para>状态管理：SQL 臂的读不变量由 Runner 的快照 Restore 保证；本臂自管——
/// SetupAsync(rows) 时用 Dataset.SeedRows 建内存快照，任何写方法置脏，读方法入口
/// 若脏则重建（DeleteAll + InsertBulk 快照）。重建发生在计量窗口开始后的首次调用
///（被预热吸收），后续迭代命中干净状态——与 SQL 臂 Restore 在 prepare 阶段完成同效。</para></summary>
internal sealed class LiteDbImpl : IPerfImplementation, IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<S1Row> _s1;
    private readonly ILiteCollection<EnumRow> _enums;
    private List<S1Row>? _snapshot;
    private bool _dirty;

    /// <summary>写校验期抑制脏复位（ValidateArmWritesAsync 的 BulkUpdate→读回是方法间序列，
    /// 与 SQL 方言"同序列直读"语义对齐；测量路径不受影响）。</summary>
    internal bool SuppressResetForValidation;

    public string Name => "LiteDB";

    public LiteDbImpl()
    {
        // 独立临时文件（每批进程一次）；direct 连接（独占锁，与 AotProbe 适配器同形态）
        string path = Path.Combine(
            Path.GetTempPath(),
            $"crossbench-litedb-{Environment.ProcessId}.db");
        _db = new LiteDatabase($"Filename={path};Connection=direct");
        _s1 = _db.GetCollection<S1Row>("perf_s1");
        _enums = _db.GetCollection<EnumRow>("perf_enum");
    }

    public void Dispose() => _db.Dispose();

    /// <summary>读前守卫：脏状态重建（见类注释——与 SQL 臂 Restore 语义对齐）。</summary>
    private void EnsureClean()
    {
        if (SuppressResetForValidation || !_dirty || _snapshot is null)
        {
            return;
        }

        _s1.DeleteAll();
        _s1.InsertBulk(_snapshot);
        _dirty = false;
    }

    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
    {
        // 档位切换/prepare 复位：重建快照并复位文档库（Dataset.SeedRows 与 SQL 表种子同源）；
        // perf_enum 同步清空（EnumInserts 两阶段同 ID 复插的复位面，SQL 侧对应 SetupPerfEnumTableAsync）
        _snapshot = [.. Dataset.SeedRows(rows)];
        _s1.DeleteAll();
        _s1.InsertBulk(_snapshot);
        _enums.DeleteAll();
        _dirty = false;
        return Task.CompletedTask;
    }

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        await Task.Yield();
        EnsureClean();
        return _s1.FindById(id);
    }

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        await Task.Yield();
        EnsureClean();
        return [.. _s1.FindAll()];
    }

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await Task.Yield();
        _dirty = true;
        BsonValue id = _s1.Insert(row);
        if (id.AsInt64 != row.Id)
        {
            throw new InvalidOperationException($"LiteDB Insert id mismatch {id} != {row.Id}");
        }
    }

    /// <summary>批量插入——InsertBulk（LiteDB 官方批量 API，内部事务单元）。</summary>
    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await Task.Yield();
        _dirty = true;
        return _s1.InsertBulk(rows);
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await Task.Yield();
        _dirty = true;
        return _s1.Update(row) ? 1 : 0;
    }

    /// <summary>批量更新——集合 Update（LiteDB 官方批量更新）。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await Task.Yield();
        _dirty = true;
        return _s1.Update(rows);
    }

    /// <summary>BsonExpression IN 谓词——LiteDB 表达式翻译不支持闭包集合 Contains
    ///（冒烟实证 Method Contains not available），IN 谓词串是其官方集合式形态；
    /// long 直拼无注入面。分块与 BulkSql.BatchRows 对齐。</summary>
    private static string InPredicate(long[] ids) =>
        "_id IN [" + string.Join(",", ids.Select(static i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

    /// <summary>批量删除——DeleteMany IN 谓词（LiteDB 集合式删除，1000/批）。</summary>
    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        await Task.Yield();
        _dirty = true;
        long[] ids = keys.Select(static k => Convert.ToInt64(k, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        long total = 0;
        const int batch = BulkSql.BatchRows;
        for (int start = 0; start < ids.Length; start += batch)
        {
            int end = Math.Min(start + batch, ids.Length);
            long[] chunk = ids[start..end];
            total += _s1.DeleteMany(InPredicate(chunk));
        }

        return total;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => "LiteDB 文档 API（FindById，无 SQL）";

    public string BuildComplexQuerySql(DbConnection conn)
        => "LiteDB 文档 API（Query Where/OrderBy/Limit）";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        await Task.Yield();
        EnsureClean();
        return [.. _s1.Query()
            .Where(x => x.Id > lastId)
            .OrderBy(x => x.Id)
            .Limit(pageSize)
            .ToList()];
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        await Task.Yield();
        EnsureClean();
        return [.. _s1.Query().Where(InPredicate(ids)).ToList()];
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
    {
        await Task.Yield();
        EnsureClean();
        return _s1.Count();
    }

    /// <summary>批量 UPSERT——集合 Upsert（LiteDB 官方 insert-or-update）。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await Task.Yield();
        _dirty = true;
        return _s1.Upsert(rows);
    }

    public Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
        => throw new NotSupportedException("InsertReturningId 不在本次子集内");

    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("WideQueryAll 不在本次子集内");

    public Task<(int Parents, int Children)> IncludeJoinAsync(DbConnection conn, int parentCount, CancellationToken ct)
        => throw new NotSupportedException("IncludeJoin 不在本次子集内");

    /// <summary>事务——BeginTrans/Commit（BeginTrans 返回 bool，LiteDB v5 契约）。</summary>
    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await Task.Yield();
        _ = _db.BeginTrans();
        try
        {
            _ = _s1.Insert(row);
            _db.Commit();
            _dirty = true;
        }
        catch
        {
            _db.Rollback();
            throw;
        }
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        await Task.Yield();
        _ = _db.BeginTrans();
        try
        {
            foreach (S1Row row in rows)
            {
                _ = _s1.Insert(row);
            }

            _db.Commit();
            _dirty = true;
        }
        catch
        {
            _db.Rollback();
            throw;
        }
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await Task.Yield();
        _ = _db.BeginTrans();
        try
        {
            _ = _s1.InsertBulk(rows);
            _db.Commit();
            _dirty = true;
        }
        catch
        {
            _db.Rollback();
            throw;
        }
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await Task.Yield();
        _ = _db.BeginTrans();
        try
        {
            _ = _s1.Update(rows);
            _db.Rollback();
        }
        catch
        {
            _db.Rollback();
            throw;
        }
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

    /// <summary>枚举列事务内逐条插入——LiteDB v5 枚举默认字符串存储（BsonMapper 契约），
    /// 逐行 Insert 与 ADO/Dapper 地板形态同构。</summary>
    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        await Task.Yield();
        _ = _db.BeginTrans();
        try
        {
            foreach (EnumRow row in rows)
            {
                _ = _enums.Insert(row);
            }

            _db.Commit();
        }
        catch
        {
            _db.Rollback();
            throw;
        }
    }

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct) => QueryAllAsync(conn, ct);
}
