using System.Data.Common;
using FreeSql;
using Microsoft.Data.Sqlite;

namespace PalORM.PerfHub;

/// <summary>FreeSql 臂——按 FreeSql 官方最佳实践与真实用户写法实现（2026-10-25 写法审计后重构）：
/// <list type="bullet">
/// <item>实体映射走 <c>CodeFirst.ConfigEntity</c> Fluent 配置（FreeSql 文档对「无法加特性的
/// POCO」的标准做法）：S1Row/EnumRow 即 FreeSql 的原生实体，读路径 <c>Select&lt;T&gt;().ToListAsync()</c>
/// 物化原生实体（编译行读取器），不再是 DTO 投影（ToListAsync&lt;TDto&gt;），也消除了
/// S1Row↔FsS1 的手工拷贝。</item>
/// <item>全局 <c>UseNoneCommandParameter(true)</c>（官方 benchmarker 同款）。</item>
/// <item>批量：MySQL <c>ExecuteMySqlBulkCopyAsync</c> / PG <c>ExecutePgCopyAsync</c> /
/// SQLite <c>ExecuteSqliteBulkInsert</c>（provider 级 API，探针实测各自最快）。</item>
/// <item>UpsertBatch：MySQL 多值 ON DUPLICATE KEY（实测快于临时表）、PG provider 批量
/// UPSERT、SQLite 多值 ON CONFLICT。</item>
/// <item>枚举列：Fluent MapType=string（字符串枚举存储的官方写法）。</item>
/// <item>事务：池连接 + WithTransaction 绑定（异步事务的文档形态）。</item>
/// <item>读连接在 SQLite 上使用同款 PRAGMA 的独立连接（池连接无法逐连接注入 PRAGMA；
/// WithConnection 绑共享连接会让写事务泄漏锁库，2026-10-24 实测）。</item>
/// </list></summary>
internal sealed class FreeSqlImpl : IPerfImplementation, IDisposable
{
    private readonly IFreeSql _fsql;
    private readonly SqliteConnection? _readConn;
    private readonly Dialect _dialect;

    public FreeSqlImpl(DialectInfo dialect)
    {
        _dialect = dialect.Dialect;
        string cs = Connections.Resolve(dialect);
        DataType dataType = dialect.Dialect switch
        {
            Dialect.Sqlite => DataType.Sqlite,
            Dialect.MySql => DataType.MySql,
            Dialect.PostgreSql => DataType.PostgreSQL,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect.Dialect, null)
        };
        if (dialect.Dialect == Dialect.MySql)
        {
            cs = MySqlConnString.WithLocalInfile(cs);
        }

        _fsql = new FreeSqlBuilder()
            .UseConnectionString(dataType, cs)
            .UseNoneCommandParameter(true)
            .UseAutoSyncStructure(false)
            .Build();

        // Fluent 实体配置——属性名即列名（Id/Name/Qty/Price/Marker），只需表名 + 主键 +
        // 枚举列 MapType。S1Row/EnumRow 上的 PalORM 特性对 FreeSql 不可见，由这里接管映射。
        _fsql.CodeFirst.ConfigEntity<S1Row>(a =>
        {
            a.Name("perf_s1");
            a.Property(p => p.Id).IsPrimary(true);
        });
        _fsql.CodeFirst.ConfigEntity<EnumRow>(a =>
        {
            a.Name("perf_enum");
            a.Property(p => p.Id).IsPrimary(true);
            a.Property(p => p.Status).MapType(typeof(string));
        });

        if (dialect.Dialect == Dialect.Sqlite)
        {
            // 顺序基准下池内复用同一条连接——ctor 里对首个池连接应用与 ADO/PalORM
            // 臂同款的 PRAGMA（WAL/cache/mmap 等），三臂连接配置对齐
            using FreeSql.Internal.ObjectPool.Object<DbConnection> pooled = _fsql.Ado.MasterPool.Get();
            using DbCommand cmd = pooled.Value.CreateCommand();
            cmd.CommandText = Program.Pragma;
            cmd.ExecuteNonQuery();

            _readConn = new SqliteConnection(cs);
            _readConn.Open();
            using DbCommand readCmd = _readConn.CreateCommand();
            readCmd.CommandText = Program.Pragma;
            readCmd.ExecuteNonQuery();
        }
    }

    private ISelect<T> Read<T>(ISelect<T> query) where T : class
        => _readConn is null ? query : query.WithConnection(_readConn);

    public void Dispose()
    {
        _readConn?.Dispose();
        _fsql.Dispose();
    }

    public string Name => "FreeSql";

    /// <summary>池化连接 + 显式事务的统一包装——FreeSql 异步事务的文档形态是
    /// WithTransaction 绑定调用方事务；commit=false 用于回滚场景。</summary>
    private async Task<T> InTransactionAsync<T>(
        Func<DbConnection, DbTransaction, Task<T>> body, bool commit, CancellationToken ct)
    {
        using FreeSql.Internal.ObjectPool.Object<DbConnection> pooled =
            await _fsql.Ado.MasterPool.GetAsync(ct).ConfigureAwait(false);
        DbConnection conn = pooled.Value;
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            T result = await body(conn, tran).ConfigureAwait(false);
            if (commit)
            {
                await tran.CommitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await tran.RollbackAsync(ct).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await tran.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct) => Task.CompletedTask;

    /// <summary>单键直查——FreeSql 的真实用户写法即 Select.Where(主键).ToOne()；
    /// 这里用其内部等价形态 Limit(1).ToList() 取首行（ToListAsync 原生实体路径）。</summary>
    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        List<S1Row> rows = await Read(_fsql.Select<S1Row>().Where(a => a.Id == id))
            .Limit(1)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
        => Read(_fsql.Select<S1Row>()).ToListAsync(ct);

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        int affected = await _fsql.Insert(row).ExecuteAffrowsAsync(ct).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException($"FreeSql Insert affected {affected}");
        }
    }

    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        switch (_dialect)
        {
            case Dialect.Sqlite:
                // SQLite 无 async bulk——官方扩展是同步逐参数写，裹自带事务
                // 显式 <S1Row>：rows 是 IReadOnlyList<S1Row>，不指定会按恒等转换
                // 命中 Insert<T1>(T1) 重载（T1=IReadOnlyList<S1Row>，整个列表被当成单个实体）
                _fsql.Insert<S1Row>(rows).ExecuteSqliteBulkInsert();
                return rows.Count;
            case Dialect.MySql:
                await _fsql.Insert<S1Row>(rows).ExecuteMySqlBulkCopyAsync(cancellationToken: ct).ConfigureAwait(false);
                return rows.Count;
            case Dialect.PostgreSql:
                await _fsql.Insert<S1Row>(rows).ExecutePgCopyAsync(ct).ConfigureAwait(false);
                return rows.Count;
            default:
                throw new ArgumentOutOfRangeException(nameof(_dialect), _dialect, null);
        }
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        int affected = await _fsql.Update<S1Row>()
            .SetSource(row)
            .ExecuteAffrowsAsync(ct)
            .ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException($"FreeSql Update affected {affected} for Id={row.Id}");
        }

        return affected;
    }

    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        switch (_dialect)
        {
            case Dialect.MySql:
            {
                int affected = await _fsql.Update<S1Row>()
                    .SetSource(rows)
                    .ExecuteMySqlBulkCopyAsync(cancellationToken: ct)
                    .ConfigureAwait(false);
                if (affected <= 0)
                {
                    throw new InvalidOperationException($"FreeSql BulkUpdate affected {affected}");
                }

                return affected;
            }
            case Dialect.PostgreSql:
            {
                int affected = await _fsql.Update<S1Row>()
                    .SetSource(rows)
                    .ExecutePgCopyAsync(ct)
                    .ConfigureAwait(false);
                if (affected <= 0)
                {
                    throw new InvalidOperationException($"FreeSql BulkUpdate affected {affected}");
                }

                return affected;
            }
            case Dialect.Sqlite:
            {
                // SQLite 无 provider 级批量更新——SetSource 逐行是 FreeSql 在该方言的唯一路径
                long affected = await _fsql.Update<S1Row>()
                    .SetSource(rows)
                    .ExecuteAffrowsAsync(ct)
                    .ConfigureAwait(false);
                if (affected <= 0)
                {
                    throw new InvalidOperationException($"FreeSql BulkUpdate affected {affected}");
                }

                return affected;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(_dialect), _dialect, null);
        }
    }

    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        // 与 ADO/Dapper 臂同款批形（1000/批 IN 裹事务）——Where(Contains) 是 FreeSql
        // 文档的 IN 写法；批宽对齐 BulkSql.BatchRows 使三臂 SQL 形态等价
        long total = 0;
        var chunk = new List<long>(1000);
        foreach (object key in keys)
        {
            chunk.Add((long)key);
            if (chunk.Count < 1000)
            {
                continue;
            }

            total += await _fsql.Delete<S1Row>()
                .Where(a => chunk.Contains(a.Id))
                .ExecuteAffrowsAsync(ct)
                .ConfigureAwait(false);
            chunk.Clear();
        }

        if (chunk.Count > 0)
        {
            total += await _fsql.Delete<S1Row>()
                .Where(a => chunk.Contains(a.Id))
                .ExecuteAffrowsAsync(ct)
                .ConfigureAwait(false);
        }

        // 不校验 total：PerfHub 原测试对 BulkDelete 同样只计时不断言——
        // 预热轮数可超出播种轮数（warmupCap 语义），后期轮次合法地删 0 行
        return total;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => _fsql.Select<S1Row>().Where(a => a.Id == id).ToSql();

    public string BuildComplexQuerySql(DbConnection conn)
        => _fsql.Select<S1Row>().Where(a => a.Qty > 10).OrderBy(a => a.Marker).Limit(100).ToSql();

    public Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        => Read(
                _fsql.Select<S1Row>()
                    .Where(a => a.Id > lastId)
                    .OrderBy(a => a.Id)
                    .Limit(pageSize))
            .ToListAsync(ct);

    public Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
        => Read(_fsql.Select<S1Row>().Where(a => ids.Contains(a.Id))).ToListAsync(ct);

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => await Read(_fsql.Select<S1Row>()).CountAsync(ct).ConfigureAwait(false);

    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        long affected;
        if (_dialect == Dialect.PostgreSql)
        {
            // PG：provider 批量 UPSERT（COPY 进临时表 + INSERT ON CONFLICT）——探针实测
            // 15.1ms vs 多值形态 20.1ms（2026-10-24，FreeSqlApiProbe）
            affected = await _fsql.InsertOrUpdate<S1Row>()
                .SetSource(rows)
                .ExecutePgCopyAsync(ct)
                .ConfigureAwait(false);
        }
        else
        {
            // MySQL：多值 ON DUPLICATE KEY（探针实测 9.0ms vs 临时表 12.8ms，多值更快）；
            // SQLite：多值 ON CONFLICT（无 provider 批量 UPSERT 扩展）
            affected = await _fsql.InsertOrUpdate<S1Row>()
                .SetSource(rows)
                .ExecuteAffrowsAsync(ct)
                .ConfigureAwait(false);
        }

        if (affected <= 0)
        {
            throw new InvalidOperationException($"FreeSql UpsertBatch affected {affected}");
        }

        return affected;
    }

    public Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
        => throw new NotSupportedException("InsertReturningId 不在本次子集内");

    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("WideQueryAll 不在本次子集内");

    public Task<(int Parents, int Children)> IncludeJoinAsync(
        DbConnection conn, int parentCount, CancellationToken ct)
        => throw new NotSupportedException("IncludeJoin 不在本次子集内");

    public Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => InTransactionAsync<object?>(
            async (_, tran) =>
            {
                int affected = await _fsql.Insert(row)
                    .WithTransaction(tran)
                    .ExecuteAffrowsAsync(ct)
                    .ConfigureAwait(false);
                if (affected != 1)
                {
                    throw new InvalidOperationException($"FreeSql TxSingleInsert affected {affected}");
                }

                return null;
            },
            commit: true, ct);

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    public Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => InTransactionAsync<object?>(
            async (_, tran) =>
            {
                foreach (S1Row row in rows)
                {
                    await _fsql.Insert(row)
                        .WithTransaction(tran)
                        .ExecuteAffrowsAsync(ct)
                        .ConfigureAwait(false);
                }

                return null;
            },
            commit: true, ct);

    public Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => InTransactionAsync<object?>(
            async (_, tran) =>
            {
                long affected = await _fsql.Insert<S1Row>(rows)
                    .WithTransaction(tran)
                    .ExecuteAffrowsAsync(ct)
                    .ConfigureAwait(false);
                if (affected <= 0)
                {
                    throw new InvalidOperationException($"FreeSql TxBulkInsert affected {affected}");
                }

                return null;
            },
            commit: true, ct);

    public Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => InTransactionAsync<object?>(
            async (_, tran) =>
            {
                foreach (S1Row row in rows)
                {
                    await _fsql.Update<S1Row>()
                        .SetSource(row)
                        .WithTransaction(tran)
                        .ExecuteAffrowsAsync(ct)
                        .ConfigureAwait(false);
                }

                return null;
            },
            commit: false, ct);

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

    public Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
        => InTransactionAsync<object?>(
            async (_, tran) =>
            {
                foreach (EnumRow row in rows)
                {
                    await _fsql.Insert(row)
                        .WithTransaction(tran)
                        .ExecuteAffrowsAsync(ct)
                        .ConfigureAwait(false);
                }

                return null;
            },
            commit: true, ct);

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct)
        => Read(_fsql.Select<S1Row>()).ToListAsync(ct);
}
