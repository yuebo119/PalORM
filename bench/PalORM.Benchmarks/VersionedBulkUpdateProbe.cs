using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;
using PalORM;
using PalORM.Bench.Shared;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

/// <summary>S6 探针：版本化（[ConcurrencyCheck]）实体的单语句批量更新是否值得实现。
/// <para><b>背景</b>：产品 <c>BulkUpdateAsync</c> 对带 <c>IncrementVersion</c> 的实体恒走逐条
/// （每条 SQL 都带 <c>WHERE pk=@p AND version=@v</c>，批内共享单事务）。这是语义上最直接的
/// 形态，但 N 行付 N 次协议往返。<b>探针只回答"上限差多少"</b>，不实现产品路径——单语句形态
/// 要保住"逐行冲突可归因 + 整批回滚"的既有契约，还需要方言各自的冲突报告方案
/// （PG 可 RETURNING 主键，MySQL 只有 affected rows 且默认是 changed rows 口径）。</para>
/// <para><b>对照臂</b>：① 产品现行路径（逐条池化，单事务）；② 候选地板（PG UNNEST /
/// MySQL UPDATE JOIN VALUES ROW，每行附带 version 谓词并 <c>version = version + 1</c>）。
/// SQLite 无等值 JOIN 形态（CASE WHEN 在无版本列时已实测慢 6.4×），只报产品形态。</para>
/// <para><b>口径</b>：两条臂各用独立 id 区间（产品 1..N，候选 N+1..2N），避免同表同行的
/// 版本串扰；候选臂的 SQL 与参数对象在测量外预建，稳态只填值（与产品复用槽同口径）。
/// 每操作包一个事务提交，与产品批内单事务语义对齐。</para></summary>
internal static class VersionedBulkUpdateProbe
{
    /// <summary>产品臂经生成元数据写实体表（与 <see cref="BenchVersioned"/> 的 [Table] 同名），
    /// 候选臂发原始 SQL 也必须落在同一张表——探针自建后两臂各用独立 id 区间。</summary>
    private const string Table = "bench_versioned";

    public static Task RunAsync(string dialect, int rows) => dialect switch
    {
        "pg" or "postgresql" => RunPgAsync(Require("PALORM_PG_CONNECTION"), rows),
        "mysql" => RunMySqlAsync(Require("PALORM_MYSQL_CONNECTION"), rows),
        _ => RunSqliteAsync(rows)
    };

    private static string Require(string name)
        => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"{name} 未设置（--versioned-update pg/mysql 需要）");

    private static async Task RunPgAsync(string connectionString, int rows)
    {
        var options = new DbOptions
        {
            ConnectionString = connectionString,
            MaxRetries = 0,
            CircuitBreakerThreshold = 0
        };
        await using DataSession<PostgreSqlProvider> session =
            await DataSession<PostgreSqlProvider>.CreateAsync(options).ConfigureAwait(false);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        try
        {
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText =
                    $"DROP TABLE IF EXISTS {Table}; "
                    + $"CREATE TABLE {Table} (id BIGINT PRIMARY KEY, name TEXT NOT NULL, "
                    + "version BIGINT NOT NULL DEFAULT 0)";
                await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText =
                    $"INSERT INTO {Table} (id, name, version) "
                    + "SELECT g, 'seed', 0 FROM generate_series(1, @n) AS g";
                seed.Parameters.Add(new NpgsqlParameter("@n", NpgsqlDbType.Integer) { Value = rows * 2 });
                await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            List<BenchVersioned> productRows = BuildRows(1, rows);
            List<BenchVersioned> candidateRows = BuildRows(rows + 1, rows);

            (double productUs, long productAlloc) = await MeasureAsync(iterations: 15, async () =>
            {
                long affected = await session.BulkUpdateAsync(productRows).ConfigureAwait(false);
                EnsureAffected("product", affected, rows);
            }).ConfigureAwait(false);

            await using var candidate = new PgVersionedSingleStatement(connection, rows);
            (double candidateUs, long candidateAlloc) = await MeasureAsync(iterations: 15, async () =>
            {
                await using DbTransaction transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
                long affected = await candidate.ExecuteAsync(candidateRows, transaction, default).ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                EnsureAffected("candidate", affected, rows);
            }).ConfigureAwait(false);

            ReportAsync("PostgreSQL", rows, productUs, productAlloc, candidateUs, candidateAlloc,
                supportsCandidate: true);
        }
        finally
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP TABLE IF EXISTS {Table}";
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunMySqlAsync(string connectionString, int rows)
    {
        var options = new DbOptions
        {
            ConnectionString = connectionString,
            MaxRetries = 0,
            CircuitBreakerThreshold = 0
        };
        await using DataSession<MySqlProvider> session =
            await DataSession<MySqlProvider>.CreateAsync(options).ConfigureAwait(false);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        try
        {
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText =
                    $"DROP TABLE IF EXISTS {Table}; "
                    + $"CREATE TABLE {Table} (id BIGINT PRIMARY KEY, name VARCHAR(200) NOT NULL, "
                    + "version BIGINT NOT NULL DEFAULT 0) ENGINE=InnoDB";
                await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await SeedMySqlAsync(connection, rows * 2).ConfigureAwait(false);

            List<BenchVersioned> productRows = BuildRows(1, rows);
            List<BenchVersioned> candidateRows = BuildRows(rows + 1, rows);

            (double productUs, long productAlloc) = await MeasureAsync(iterations: 15, async () =>
            {
                long affected = await session.BulkUpdateAsync(productRows).ConfigureAwait(false);
                EnsureAffected("product", affected, rows);
            }).ConfigureAwait(false);

            await using var candidate = new MySqlVersionedSingleStatement(connection, rows);
            (double candidateUs, long candidateAlloc) = await MeasureAsync(iterations: 15, async () =>
            {
                await using DbTransaction transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
                long affected = await candidate.ExecuteAsync(candidateRows, transaction, default).ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                EnsureAffected("candidate", affected, rows);
            }).ConfigureAwait(false);

            ReportAsync("MySQL", rows, productUs, productAlloc, candidateUs, candidateAlloc,
                supportsCandidate: true);
        }
        finally
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP TABLE IF EXISTS {Table}";
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunSqliteAsync(int rows)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"palorm-versioned-{Environment.ProcessId}.db");
        var options = new DbOptions
        {
            ConnectionString = $"Data Source={dbPath}",
            MaxRetries = 0,
            CircuitBreakerThreshold = 0
        };
        try
        {
            await using DataSession<SqliteProvider> session =
                await DataSession<SqliteProvider>.CreateAsync(options).ConfigureAwait(false);
            await session.ExecuteAsync(FormattableStringFactory.Create(
                $"CREATE TABLE IF NOT EXISTS {Table} (id INTEGER PRIMARY KEY, name TEXT NOT NULL, "
                + "version INTEGER NOT NULL DEFAULT 0)")).ConfigureAwait(false);
            await SeedSqliteAsync(session, rows).ConfigureAwait(false);

            List<BenchVersioned> productRows = BuildRows(1, rows);
            (double productUs, long productAlloc) = await MeasureAsync(iterations: 15, async () =>
            {
                long affected = await session.BulkUpdateAsync(productRows).ConfigureAwait(false);
                EnsureAffected("product", affected, rows);
            }).ConfigureAwait(false);

            ReportAsync("SQLite", rows, productUs, productAlloc, 0, 0,
                supportsCandidate: false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            File.Delete(dbPath + "-wal");
            File.Delete(dbPath + "-shm");
        }
    }

    /// <summary>构造实体行——ids 从 <paramref name="startId"/> 起，version 从 0 起。</summary>
    private static List<BenchVersioned> BuildRows(long startId, int rows)
    {
        var list = new List<BenchVersioned>(rows);
        for (int i = 0; i < rows; i++)
        {
            list.Add(new BenchVersioned
            {
                id = startId + i,
                name = "updated",
                version = 0
            });
        }
        return list;
    }

    private static async Task SeedMySqlAsync(DbConnection connection, int rows)
    {
        const int chunk = 1000;
        for (int start = 1; start <= rows; start += chunk)
        {
            int end = Math.Min(start + chunk - 1, rows);
            var sb = new StringBuilder(32 + ((end - start + 1) * 24));
            sb.Append($"INSERT INTO {Table} (id, name, version) VALUES ");
            for (int id = start; id <= end; id++)
            {
                if (id > start) sb.Append(',');
                sb.Append('(').Append(id).Append(",'seed',0)");
            }
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sb.ToString();
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task SeedSqliteAsync<TProvider>(DataSession<TProvider> session, int rows)
        where TProvider : IDbProvider
    {
        const int chunk = 500;
        for (int start = 1; start <= rows; start += chunk)
        {
            int end = Math.Min(start + chunk - 1, rows);
            var sb = new StringBuilder(32 + ((end - start + 1) * 24));
            sb.Append($"INSERT INTO {Table} (id, name, version) VALUES ");
            for (int id = start; id <= end; id++)
            {
                if (id > start) sb.Append(',');
                sb.Append('(').Append(id).Append(",'seed',0)");
            }
            await session.ExecuteAsync(FormattableStringFactory.Create(sb.ToString())).ConfigureAwait(false);
        }
    }

    /// <summary>测量循环——先 2 轮预热（覆盖复用槽晋升与驱动预处理），再计时/计分配。
    /// 每轮执行后校验受影响行数，任何一行版本不匹配都会让探针显式失败而非静默。</summary>
    private static async Task<(double Us, long AllocB)> MeasureAsync(int iterations, Func<ValueTask> action)
    {
        for (int i = 0; i < 2; i++)
            await action().ConfigureAwait(false);

#pragma warning disable S1215 // 分配测量前强制回收是量具步骤（与 PoolProbe 同口径）
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            await action().ConfigureAwait(false);
        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;
#pragma warning restore S1215
        return (sw.Elapsed.TotalMicroseconds / iterations, alloc / iterations);
    }

    private static void EnsureAffected(string arm, long affected, int expected)
    {
        if (affected != expected)
            throw new InvalidOperationException(
                $"[versioned-update] {arm} 臂受影响行数 {affected} ≠ {expected}（版本谓词或绑定失配）");
    }

    [SuppressMessage("Globalization", "CA1303",
        Justification = "探针控制台输出是机器可读报告，固定文本。")]
    private static void ReportAsync(string dialect, int rows,
        double productUs, long productAlloc, double candidateUs, long candidateAlloc, bool supportsCandidate)
    {
        string line = supportsCandidate
            ? string.Format(CultureInfo.InvariantCulture,
                "[versioned-update] {0} rows={1}: product(逐条池化)={2:F1} µs（{3} B） · "
                + "candidate(单语句)={4:F1} µs（{5} B） · speedup={6:F2}×",
                dialect, rows, productUs, productAlloc, candidateUs, candidateAlloc,
                productUs / candidateUs)
            : string.Format(CultureInfo.InvariantCulture,
                "[versioned-update] {0} rows={1}: product(逐条池化)={2:F1} µs（{3} B） · "
                + "candidate=N/A（SQLite 无等值 JOIN 形态）",
                dialect, rows, productUs, productAlloc);
        Console.WriteLine(line);

        var envelope = new PerfResultEnvelope
        {
            Harness = "benchmarks",
            Version = Environment.GetEnvironmentVariable("PALORM_BENCH_VERSION") ?? "HEAD",
            Label = "versioned-update",
            DetailPath = "（版本化批量更新探针：控制台明细）",
            Environment = PerfResultWriter.CaptureEnvironment("PalORM.Benchmarks --versioned-update"),
            Regime = new PerfResultRegime
            {
                ConnectionConfig = dialect == "SQLite"
                    ? BenchmarkConfig.RegimeFileDb
                    : "pg/mysql: 驱动默认（探针；每条臂各一个专用 id 区间）",
                SessionLifecycle = "产品逐条池化（单事务） vs 单语句候选（单事务）",
                Health = "unknown"
            }
        };
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["rowsPerBatch"] = rows,
            ["productUs"] = productUs,
            ["productAllocB"] = productAlloc
        };
        if (supportsCandidate)
        {
            metrics["candidateUs"] = candidateUs;
            metrics["candidateAllocB"] = candidateAlloc;
            metrics["speedup"] = productUs / candidateUs;
        }
        envelope.Sections.Add(new PerfResultSection
        {
            Kind = "versioned-bulk-update",
            Dialect = dialect,
            Label = $"rows={rows}",
            Note = supportsCandidate
                ? "带 [ConcurrencyCheck] 的批量更新：产品逐条（每行 version 谓词）vs 单语句等值 JOIN 候选；仅观测，不卡阈值"
                : "带 [ConcurrencyCheck] 的批量更新：SQLite 产品形态（逐条裹单事务）；无单语句候选",
            Metrics = metrics
        });
        string? path = PerfResultWriter.Write(envelope);
        Console.WriteLine(path is null
            ? "[versioned-update] 结果库信封写入失败（不影响本次观测）"
            : $"[versioned-update] 结果库信封已写入 {path}");
    }

    /// <summary>PG 候选：UPDATE ... FROM UNNEST，SET 含 version=version+1，
    /// WHERE 同时匹配主键与原 version。一条语句更新整批，语句文本恒定（只换数组内容）。</summary>
    [SuppressMessage("Major Code Smell",
        "S3265:Non-flags enums should not be used in bitwise operations",
        Justification = "Npgsql 官方文档要求 NpgsqlDbType.Array 与元素类型按位或组合。")]
    private sealed class PgVersionedSingleStatement : IAsyncDisposable
    {
        private readonly DbCommand _command;
        private readonly long[] _ids;
        private readonly long[] _versions;
        private readonly string[] _names;

        public PgVersionedSingleStatement(DbConnection connection, int rows)
        {
            _names = new string[rows];
            _ids = new long[rows];
            _versions = new long[rows];
            _command = connection.CreateCommand();
            _command.CommandText =
                $"UPDATE {Table} AS tgt SET name = v.c0, version = tgt.version + 1 "
                + "FROM UNNEST(@names, @ids, @versions) AS v(c0, pk, ver) "
                + "WHERE tgt.id = v.pk AND tgt.version = v.ver";
            _command.Parameters.Add(new NpgsqlParameter("@names", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = _names
            });
            _command.Parameters.Add(new NpgsqlParameter("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = _ids
            });
            _command.Parameters.Add(new NpgsqlParameter("@versions", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = _versions
            });
        }

        public async ValueTask<long> ExecuteAsync(
            List<BenchVersioned> entities, DbTransaction transaction, CancellationToken ct)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                BenchVersioned entity = entities[i];
                _names[i] = entity.name;
                _ids[i] = entity.id;
                _versions[i] = entity.version;
            }
            _command.Transaction = transaction;
            long affected = await _command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            for (int i = 0; i < entities.Count; i++)
                entities[i].version++;
            return affected;
        }

        public ValueTask DisposeAsync() => _command.DisposeAsync();
    }

    /// <summary>MySQL 候选：UPDATE ... JOIN (VALUES ROW(...)) AS v(c0,pk,ver)，
    /// ON 同时匹配主键与原 version。按 2000 行/语句切批（产品同档批宽）。</summary>
    private sealed class MySqlVersionedSingleStatement : IAsyncDisposable
    {
        private const int BatchRows = 2000;
        private readonly List<Batch> _batches = [];

        public MySqlVersionedSingleStatement(DbConnection connection, int totalRows)
        {
            for (int start = 0; start < totalRows; start += BatchRows)
            {
                int len = Math.Min(BatchRows, totalRows - start);
                _batches.Add(new Batch(connection, len, start));
            }
        }

        public async ValueTask<long> ExecuteAsync(
            List<BenchVersioned> entities, DbTransaction transaction, CancellationToken ct)
        {
            long total = 0;
            foreach (Batch batch in _batches)
            {
                total += await batch.ExecuteAsync(entities, transaction, ct).ConfigureAwait(false);
            }
            return total;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (Batch batch in _batches)
                await batch.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class Batch : IAsyncDisposable
        {
            private readonly DbCommand _command;
            private readonly int _start;
            private readonly MySqlParameter[] _names;
            private readonly MySqlParameter[] _ids;
            private readonly MySqlParameter[] _versions;

            public Batch(DbConnection connection, int length, int start)
            {
                _start = start;
                var sb = new StringBuilder(96 + (length * 48));
                sb.Append($"UPDATE {Table} AS tgt JOIN (VALUES ");
                for (int r = 0; r < length; r++)
                {
                    if (r > 0) sb.Append(", ");
                    sb.Append("ROW(@n").Append(r).Append(",@i").Append(r).Append(",@v").Append(r).Append(')');
                }
                sb.Append(") AS v(c0, pk, ver) ON tgt.id = v.pk AND tgt.version = v.ver ")
                    .Append("SET tgt.name = v.c0, tgt.version = tgt.version + 1");
                _command = connection.CreateCommand();
                _command.CommandText = sb.ToString();
                _names = new MySqlParameter[length];
                _ids = new MySqlParameter[length];
                _versions = new MySqlParameter[length];
                for (int r = 0; r < length; r++)
                {
                    _names[r] = new MySqlParameter($"@n{r}", MySqlDbType.VarChar) { Value = "" };
                    _ids[r] = new MySqlParameter($"@i{r}", MySqlDbType.Int64) { Value = 0L };
                    _versions[r] = new MySqlParameter($"@v{r}", MySqlDbType.Int64) { Value = 0L };
                    _command.Parameters.Add(_names[r]);
                    _command.Parameters.Add(_ids[r]);
                    _command.Parameters.Add(_versions[r]);
                }
            }

            public async ValueTask<long> ExecuteAsync(
                List<BenchVersioned> entities, DbTransaction transaction, CancellationToken ct)
            {
                for (int r = 0; r < _names.Length; r++)
                {
                    BenchVersioned entity = entities[_start + r];
                    _names[r].Value = entity.name;
                    _ids[r].Value = entity.id;
                    _versions[r].Value = entity.version;
                }
                _command.Transaction = transaction;
                long affected = await _command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                for (int r = 0; r < _names.Length; r++)
                    entities[_start + r].version++;
                return affected;
            }

            public ValueTask DisposeAsync() => _command.DisposeAsync();
        }
    }
}
