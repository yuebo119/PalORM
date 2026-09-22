using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalORM.Bench.Shared;

namespace PalORM.PerfHub;

/// <summary>PerfHub 统一性能测试运行器。
///
/// 用法：
///   dotnet run --project bench/PalORM.PerfHub -- run [--dialects sqlite,mysql,pg]
///           [--tiers 2000,20000] [--concurrency] [--threads 1,4,8] [--quick]
///           [--version v5.5.1] [--label TEXT]
///   dotnet run --project bench/PalORM.PerfHub -- report
///
/// 测试矩阵（每个 方言 × 实现 × 档位 组合）：
///   Build       2 项  纯 SQL 构建开销（不执行）
///   CRUD        9 项  点查/全查/流式/插入/更新/批量插入/批量更新/批量删除
///   Query       3 项  键集分页 / IN 查询 / 计数
///   Transaction 5 项  单条事务 / 10 条 / 100 条 / 批量 / 回滚
///   Baseline    1 项  数据生成自身内存（与实现无关）
///   Concurrency 1 项  80/20 读写混合吞吐（--concurrency 时）
///
/// 输出：
///   bench/perfhub/results/history-&lt;yyyyMMdd-HHmmss&gt;.json   每次运行的完整原始数据
///   bench/perfhub/results/latest.json                       最近一次（供回归对比）
///   bench/perfhub/report.html                              统一报告（含 SVG 增长曲线）
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0].ToUpperInvariant() switch
            {
                "RUN" => await RunAsync(args[1..]).ConfigureAwait(false),
                "REPORT" => Report.Build(),
                _ => Fail($"未知命令 '{args[0]}'")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PerfHub 失败: {ex}");
            return 1;
        }
    }

    private static void PrintUsage() => Console.WriteLine("""
            PerfHub — PalORM 统一性能测试系统

            命令:
              run       执行测试（三方言 × 三实现 × 统一数据集）
              report    仅从历史数据重新生成报告

            run 选项:
              --dialects sqlite,mysql,pg    方言子集（默认全部可用）
              --tiers 2000,20000            行数档位（默认 2000,20000）
              --concurrency                 启用并发吞吐测试
              --threads 1,4,8               并发线程档位（默认 1,4,8）
              --quick                       迭代次数降到 30%（冒烟用）
              --version TEXT                被测版本标识（记入 JSON，用于版本对比）
              --label TEXT                  本次运行的标签（记入 JSON）

            环境变量（与集成测试同一口径，见 CONTRIBUTING.md）:
              PALORM_PG_CONNECTION      PostgreSQL 连接串
              PALORM_MYSQL_CONNECTION   MySQL 连接串
              未设置时自动从仓库根 .env.test 补入缺失项

            示例:
              dotnet run --project bench/PalORM.PerfHub -- run --dialects sqlite --tiers 2000 --quick
              dotnet run --project bench/PalORM.PerfHub -- run --concurrency --version HEAD
            """);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"PerfHub: {message}");
        return 1;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var dialects = new List<Dialect> { Dialect.Sqlite, Dialect.MySql, Dialect.PostgreSql };
        var tiers = new List<int>(Dataset.Tiers);
        bool concurrency = false;
        var threadTiers = new List<int> { 1, 4, 8 };
        double scale = 1.0;
        string label = "";
        string version = "HEAD";

        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--dialects":
                    dialects = ParseList(args[++i], ParseDialect);
                    break;
                case "--tiers":
                    tiers = ParseList(args[++i], int.Parse);
                    break;
                case "--threads":
                    threadTiers = ParseList(args[++i], int.Parse);
                    break;
                case "--concurrency":
                    concurrency = true;
                    break;
                case "--quick":
                    scale = 0.3;
                    label = label.Length == 0 ? "quick" : label;   // 冒烟批次进结果库时带标记，避免被当可引用数字
                    break;
                case "--label":
                    label = args[++i];
                    break;
                case "--version":
                    version = args[++i];
                    break;
                default:
                    return Fail($"未知选项 '{args[i]}'");
            }
            i++;
        }

        // 探测可用方言（PG/MySQL 需连接串）
        var available = new List<DialectInfo>();
        foreach (Dialect d in dialects)
        {
            var info = DialectInfo.Of(d);
            try
            {
                _ = Connections.Resolve(info);
                available.Add(info);
                Console.WriteLine($"[PerfHub] {info.DisplayName} 可用");
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException
                or JsonException or ArgumentException)
            {
                Console.WriteLine($"[PerfHub] {info.DisplayName} 跳过 — {ex.Message}");
            }
        }
        if (available.Count == 0)
        {
            return Fail("没有可用方言");
        }

        var results = new List<Measurement>();
        var swTotal = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();

        // ── 数据生成基线：与实现、方言无关，每档位一条 ──
        // 回答「测试数据自身的真实内存占用峰值」——不含 ORM、不含数据库。
        foreach (int rows in tiers)
        {
            Measurement gen = Measure.MeasureDataGeneration(rows);
            results.Add(gen);
            Console.WriteLine($"[PerfHub] 数据生成基线 {rows:N0} 行: "
                + $"{Fmt.Bytes(gen.AllocatedBytesPerOp)}/行 · 堆峰 {Fmt.Bytes(gen.PeakHeapBytes)}");
        }

        foreach (DialectInfo info in available)
        {
            Console.WriteLine();
            Console.WriteLine($"══════ {info.DisplayName} ══════");
            string cs = Connections.Resolve(info);
            await using DbConnection rawConn = info.OpenConnection(cs);
            await rawConn.OpenAsync(cts.Token).ConfigureAwait(false);

            // 连接配置口径（规范 §4.1）：三臂共用这一条连接，故此处一次治理即全臂同值。
            // 与 DapperSuite 的口径差 D10 逐项一致——SQLite 上不开 WAL/mmap，整档会落在
            // I/O 主导区间，8 µs 级差异不可分辨（2026-09-22 实测：同一修复在两种配置下
            // 分别是 −30% 与 0%）。
            if (info.Dialect == Dialect.Sqlite)
            {
                await using DbCommand pragma = rawConn.CreateCommand();
                pragma.CommandText = SqliteGovernancePragma;
                await pragma.ExecuteNonQueryAsync(cts.Token).ConfigureAwait(false);
            }

            // 维度 8（往返与语句效率）：包一层计数装饰器。包在连接上而不是各臂内部，
            // 三臂（ADO 自建命令 / Dapper 在传入连接上建 / PalORM 会话在调用方连接上建）
            // 才能用同一把尺子量，且能抓到意外 N+1。
            await using var conn = new CountingConnection(rawConn);

            // 三实现共用同一连接（B76：建连口径一致）
            IPerfImplementation[] impls = [new AdoNetImpl(info), new DapperImpl(info), new PalormImpl(info)];

            foreach (int rows in tiers)
            {
                Console.WriteLine();
                Console.WriteLine($"── 行数档位 {rows:N0} ──");

                // 阶段 2 新表：每 (方言 × 档位) 播一次，臂无关——测量只读或自清理
                await SetupV2TablesAsync(info, conn, rows, cts.Token).ConfigureAwait(false);

                foreach (IPerfImplementation impl in impls)
                {
                    await RunOneImplAsync(info, impl, rows, conn, results, scale, cts.Token)
                        .ConfigureAwait(false);
                }

                // 并发吞吐（三实现 × 线程档位）
                if (concurrency)
                {
                    Console.WriteLine();
                    Console.WriteLine("  并发吞吐（80/20 读写混合，3s）");
                    foreach (IPerfImplementation impl in impls)
                    {
                        await impl.SetupAsync(conn, rows, cts.Token).ConfigureAwait(false);
                        foreach (int threads in threadTiers)
                        {
                            try
                            {
                                Measurement m = await Measure.ConcurrentAsync(info, impl, rows, threads, 2.0, 0.20,
                                    cts.Token).ConfigureAwait(false);
                                results.Add(m);
                                Console.WriteLine($"    {impl.Name,-9} {threads,2} 线程  {m.OpsPerSecond,10:N0} ops/s  "
                                    + $"p50={m.P50Ms,6:F2}ms p95={m.P95Ms,6:F2}ms p99={m.P99Ms,6:F2}ms");
                            }
                            catch (InvalidOperationException ex)
                            {
                                // 3 秒的并发探针不该中断整轮测量——记明跳过原因继续跑
                                Console.WriteLine($"    {impl.Name,-9} {threads,2} 线程  跳过 — {ex.Message}");
                            }
                        }
                    }
                }
            }

            await conn.CloseAsync().ConfigureAwait(false);
        }

        swTotal.Stop();
        Console.WriteLine();
        Console.WriteLine($"[PerfHub] 完成，共 {results.Count} 项测量，耗时 {swTotal.Elapsed.TotalMinutes:F1} 分钟");

        Save(results, label, version, swTotal.Elapsed);
        Report.Build();
        return 0;
    }

    /// <summary>阶段 2 新表播种——每 (方言 × 档位) 一次：宽表全量、子表前 500 父 × 3 子、
    /// 自增表建空。播种走 ADO 多值 VALUES（臂无关的地板路径，不计入任何测量）。</summary>
    private static async Task SetupV2TablesAsync(
        DialectInfo info, DbConnection conn, int rows, CancellationToken ct)
    {
        Dialect d = info.Dialect;
        await AdoNetImpl.ExecAsync(conn, Dataset.DropWideTableSql(d), ct).ConfigureAwait(false);
        await AdoNetImpl.ExecAsync(conn, Dataset.CreateWideTableSql(d), ct).ConfigureAwait(false);
        await AdoNetImpl.ExecAsync(conn, Dataset.DropChildTableSql(d), ct).ConfigureAwait(false);
        await AdoNetImpl.ExecAsync(conn, Dataset.CreateChildTableSql(d), ct).ConfigureAwait(false);
        await AdoNetImpl.ExecAsync(conn, Dataset.DropAutoIncTableSql(d), ct).ConfigureAwait(false);
        await AdoNetImpl.ExecAsync(conn, Dataset.CreateAutoIncTableSql(d), ct).ConfigureAwait(false);

        // 宽表播种：多值 VALUES（20 列 → 批宽收敛到 100 行/语句）
        const int wideBatch = 100;
        string wideCols = Dataset.WideSelectColumns(d);
        for (int start = 0; start < rows; start += wideBatch)
        {
            int end = Math.Min(start + wideBatch, rows);
            var sql = new System.Text.StringBuilder();
            sql.Append("INSERT INTO ").Append(Dataset.WideTable(d)).Append(" (").Append(wideCols)
                .Append(") VALUES ");
            await using DbCommand cmd = conn.CreateCommand();
            int p = 0;
            for (int r = start; r < end; r++)
            {
                if (r > start)
                {
                    sql.Append(", ");
                }

                sql.Append('(');
                for (int c = 0; c < 20; c++)
                {
                    if (c > 0)
                    {
                        sql.Append(", ");
                    }

                    sql.Append(Dataset.P(p + c));
                }
                sql.Append(')');
                WideRow w = WideRow.Seed(r);
                object?[] vals =
                [
                    w.Id, w.C01, w.C02, w.C03, w.C04, w.C05, w.C06, w.C07, w.C08, w.C09,
                    w.C10, w.C11, w.C12, w.C13, w.C14, w.C15, w.C16, w.C17, w.C18, w.C19
                ];
                for (int c = 0; c < 20; c++)
                {
                    DbParameter par = cmd.CreateParameter();
                    par.ParameterName = Dataset.P(p + c);
                    par.Value = vals[c] ?? DBNull.Value;
                    cmd.Parameters.Add(par);
                }
                p += 20;
            }
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 子表播种：前 500 父 × 3 子（IncludeJoin 查 50 父，500 留足上界）
        const int childParents = 500;
        var childSql = new System.Text.StringBuilder();
        childSql.Append("INSERT INTO ").Append(Dataset.ChildTable(d)).Append(" (")
            .Append(Dataset.Q(d, "ChildId")).Append(", ").Append(Dataset.Q(d, "ParentId")).Append(", ")
            .Append(Dataset.Q(d, "Note")).Append(") VALUES ");
        await using DbCommand childCmd = conn.CreateCommand();
        int cp = 0;
        foreach (ChildRow child in Dataset.SeedChildren(childParents))
        {
            if (cp > 0)
            {
                childSql.Append(", ");
            }

            childSql.Append('(').Append(Dataset.P(cp * 3)).Append(", ")
                .Append(Dataset.P((cp * 3) + 1)).Append(", ").Append(Dataset.P((cp * 3) + 2)).Append(')');
            DbParameter p1 = childCmd.CreateParameter();
            p1.ParameterName = Dataset.P(cp * 3);
            p1.Value = child.ChildId;
            childCmd.Parameters.Add(p1);
            DbParameter p2 = childCmd.CreateParameter();
            p2.ParameterName = Dataset.P((cp * 3) + 1);
            p2.Value = child.ParentId;
            childCmd.Parameters.Add(p2);
            DbParameter p3 = childCmd.CreateParameter();
            p3.ParameterName = Dataset.P((cp * 3) + 2);
            p3.Value = child.Note;
            childCmd.Parameters.Add(p3);
            cp++;
        }
        childCmd.CommandText = childSql.ToString();
        await childCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>对一个 (方言, 实现, 行数) 组合跑全部 23 个单操作测量。
    /// <para>顺序即状态依赖：Build/查询类先跑（要求表内恰好 <paramref name="rows"/> 行），
    /// 写类后跑（每轮用不同主键段，表会增长，但已不影响后续查询类——
    /// 下一个实现进场时 <c>SetupAsync</c> 会重建表）。</para></summary>
    private static async Task RunOneImplAsync(
        DialectInfo info, IPerfImplementation impl, int rows, DbConnection conn,
        List<Measurement> results, double scale, CancellationToken ct)
    {
        // 库重置为 rows 行——查询类操作要求表内恰好 rows 行
        await impl.SetupAsync(conn, rows, ct).ConfigureAwait(false);
        Task reset(DbConnection c)
        {
            return impl.SetupAsync(c, rows, ct);
        }

        long[] keys = Dataset.KeySet(rows);
        long[] whereInIds = BuildWhereInIds(rows);
        int bulkDeleteIters = BulkDeleteSeedRounds(scale);

        // ── Build：纯 SQL 构建开销，不执行、不碰库 ──
        await MeasAsync(info, impl, "BuildGetByKeySql", "Build", rows, conn, results, scale, null,
            (im, c, i) =>
            {
                _ = im.BuildGetByKeySql(c, 1);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "BuildComplexQuerySql", "Build", rows, conn, results, scale, null,
            (im, c, i) =>
            {
                _ = im.BuildComplexQuerySql(c);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);

        // ── CRUD ──
        await MeasAsync(info, impl, "GetByKey", "CRUD", rows, conn, results, scale, null,
            async (im, c, i) =>
            {
                // 步长 7919（质数）打散访问位置，避免顺序扫描缓存放大点查优势
                long id = keys[i * 7919 % keys.Length];
                _ = await im.GetByKeyAsync(c, id, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "QueryAll", "CRUD", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.QueryAllAsync(c, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "StreamAll", "CRUD", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.StreamAllAsync(c, static row => default, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "Insert", "CRUD", rows, conn, results, scale, reset,
            async (im, c, i) => await im.InsertAsync(c, Dataset.Seed(rows + i), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "Update", "CRUD", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.UpdateAsync(c, Dataset.Seed(i * 7919 % rows), ct)
                .ConfigureAwait(false), ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "BulkInsert", "CRUD", rows, conn, results, scale, reset,
            async (im, c, i) => _ = await im.BulkInsertAsync(
                c, Dataset.SeedRows(rows, (long)rows * (i + 1)), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "BulkUpdate", "CRUD", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.BulkUpdateAsync(c, Dataset.SeedRows(rows, 0), ct)
                .ConfigureAwait(false), ct).ConfigureAwait(false);

        // 批量删除：prepare 多种 rows×iterations 行，每轮删掉其中一段互不重叠的窗口
        Task bulkDeleteReset(DbConnection c)
        {
            return impl.SetupAsync(c, rows * bulkDeleteIters, ct);
        }

        await MeasAsync(info, impl, "BulkDelete", "CRUD", rows, conn, results, scale, bulkDeleteReset,
            async (im, c, i) =>
            {
                object[] batch = new object[rows];
                for (int k = 0; k < rows; k++)
                {
                    batch[k] = (long)((i * rows) + k + 1);
                }

                _ = await im.BulkDeleteAsync(c, batch, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        // ── Query ──
        await MeasAsync(info, impl, "KeysetPage", "Query", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.KeysetPageAsync(c, i % 20 * 100, 100, ct)
                .ConfigureAwait(false), ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "WhereIn", "Query", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.WhereInAsync(c, whereInIds, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "Count", "Query", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.CountAsync(c, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        // ── 阶段 2 新增测项 ──
        await MeasAsync(info, impl, "UpsertBatch", "CRUD", rows, conn, results, scale, reset,
            async (im, c, i) =>
            {
                // 每轮对已存在的主键段整批 UPSERT——首轮含插入、后续以更新为主。
                // MySQL ON DUPLICATE KEY 对更新行计数 2 而产品 BulkMerge 返回实体数，
                // 三臂返回口径不同：此处只计时延与分配，不比返回值
                _ = await im.UpsertBatchAsync(c, Dataset.SeedRows(rows, 0), ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        async Task ClearAutoInc(DbConnection c)
        {
            await AdoNetImpl.ExecAsync(c, "DELETE FROM " + Dataset.AutoIncTable(info.Dialect), ct)
                .ConfigureAwait(false);
        }
        await MeasAsync(info, impl, "InsertReturningId", "CRUD", rows, conn, results, scale, ClearAutoInc,
            async (im, c, i) =>
            {
                _ = await im.InsertReturningIdAsync(c, $"row-{i}", i, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "WideQueryAll", "Query", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.WideQueryAllAsync(c, ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);

        // IncludeJoin 的策略不同构已标注（ADO 全手工 / Dapper multi-mapping / PalORM JOIN 生成）
        await MeasAsync(info, impl, "IncludeJoin", "Query", rows, conn, results, scale, null,
            async (im, c, i) =>
            {
                (int parents, int children) = await im.IncludeJoinAsync(c, 50, ct).ConfigureAwait(false);
                if (parents != 50 || children != 150)
                    throw new InvalidOperationException(
                        $"IncludeJoin 结果集不等价：parents={parents}(期望 50), children={children}(期望 150)");
            }, ct).ConfigureAwait(false);

        // ── Transaction：真实业务形态（开启事务 → N 条写 → 提交/回滚）──
        await MeasAsync(info, impl, "TxSingleInsert", "Transaction", rows, conn, results, scale, reset,
            async (im, c, i) => await im.TxSingleInsertAsync(c, Dataset.Seed(rows + i), ct)
                .ConfigureAwait(false), ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "TxTenInserts", "Transaction", rows, conn, results, scale, reset,
            async (im, c, i) => await im.TxTenInsertsAsync(
                c, Dataset.SeedRows(10, rows + ((long)i * 10)), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "TxHundredInserts", "Transaction", rows, conn, results, scale, reset,
            async (im, c, i) => await im.TxHundredInsertsAsync(
                c, Dataset.SeedRows(100, rows + ((long)i * 100)), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "TxBulkInsert", "Transaction", rows, conn, results, scale, reset,
            async (im, c, i) => await im.TxBulkInsertAsync(
                c, Dataset.SeedRows(rows, (long)rows * (i + 1)), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        // 回滚场景固定 1000 行上限——回滚成本由「撤销量」决定，与表规模无直接关系，
        // 无上限会让 20000 档的回滚测量耗时失控。
        await MeasAsync(info, impl, "TxRollback", "Transaction", rows, conn, results, scale, reset,
            async (im, c, i) => await im.TxRollbackAsync(
                c, Dataset.SeedRows(Math.Min(rows, 500), 0), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

    }

    /// <summary>带操作名上下文的测量包装——失败时报出是哪个操作，便于定位。</summary>
    private static async Task MeasAsync(
        DialectInfo info, IPerfImplementation impl, string operation, string group, int rows,
        DbConnection conn, List<Measurement> results, double scale,
        Func<DbConnection, Task>? prepare,
        Func<IPerfImplementation, DbConnection, int, Task> action, CancellationToken ct)
    {
        _ = (rows, scale);
        try
        {
            Measurement m = await Measure.SingleAsync(info, impl, operation, group, rows, action, conn,
                MaxIterations(operation), ct, prepare, BudgetSeconds(operation)).ConfigureAwait(false);
            results.Add(m);
            PrintRow([m]);
        }
        catch (Exception ex)
        {
            // 操作名上下文——失败时直接知道是哪个 (实现, 操作, 方言, 行数) 组合
            throw new InvalidOperationException(
                $"{impl.Name} / {operation} / {info.DisplayName} / {rows} 行 失败", ex);
        }
    }

    /// <summary>BulkDelete 播种规模的迭代上界——自适应迭代后实际轮数由预算决定，
    /// 播种按最坏情况（上限轮数 × 每轮删除行数）准备主键空间。</summary>
    private static int BulkDeleteSeedRounds(double scale)
        => Math.Max(3, (int)Math.Ceiling(MaxIterations("BulkDelete") * scale));

    /// <summary>测项的时间预算（秒）——阶段 3.1 的自适应迭代上限基准。
    /// 迭代数 = clamp(预算 ÷ 预热后单次耗时, 3, 上限)，单测量耗时结构性有界。</summary>
    private static double BudgetSeconds(string operation) => operation switch
    {
        // 纯 CPU 构建——亚微秒级，1s 预算自然收敛到数千次迭代
        "BuildGetByKeySql" or "BuildComplexQuerySql" => 1.0,
        // 批量为档位行数——每次成本高，给足 4s 让中位数有足够样本
        "BulkInsert" or "BulkUpdate" or "BulkDelete" or "TxBulkInsert" or "UpsertBatch" => 4.0,
        "TxRollback" or "WideQueryAll" => 2.0,
        _ => 1.5
    };

    /// <summary>迭代数上限——防止极快操作（Build 类亚微秒）在 1s 预算内跑出
    /// 无意义的十万次迭代（计时循环自身的开销会污染测量）。</summary>
    private static int MaxIterations(string operation) => operation switch
    {
        "BuildGetByKeySql" or "BuildComplexQuerySql" => 2_000,
        _ => 200
    };

    /// <summary>WhereIn 的 100 个键——按档位步长均匀取样，三实现三方言完全相同。</summary>
    private static long[] BuildWhereInIds(int rows)
    {
        const int count = 100;
        long[] ids = new long[count];
        long step = Math.Max(1, rows / count);
        for (int i = 0; i < count; i++)
        {
            ids[i] = (i * step) + 1;
        }

        return ids;
    }

    private static void PrintRow(Measurement[] rows)
    {
        foreach (Measurement m in rows)
        {
            string warn = m.ErrorRatio > 0.05 ? " !" : "";
            string line = "    " + m.Implementation.PadRight(9) + " " + m.Operation.PadRight(20)
                + " " + Fmt.Time(m.MedianNs).PadLeft(12) + "  "
                + Fmt.Bytes(m.AllocatedBytesPerOp).PadLeft(10) + "/op  堆峰 "
                + Fmt.Bytes(m.PeakHeapBytes).PadLeft(10) + "  Gen0=" + m.Gen0Collections.ToString().PadLeft(3) + warn;
            Console.WriteLine(line);
        }
    }

    private static Dialect ParseDialect(string s) => s.ToUpperInvariant() switch
    {
        "SQLITE" or "SQLITE3" => Dialect.Sqlite,
        "MYSQL" or "MARIA" => Dialect.MySql,
        "PG" or "POSTGRES" or "POSTGRESQL" => Dialect.PostgreSql,
        _ => throw new ArgumentException($"未知方言 '{s}'")
    };

    private static List<T> ParseList<T>(string csv, Func<string, T> parse)
        => [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(parse)];

    private static void Save(List<Measurement> results, string label, string version, TimeSpan elapsed)
    {
        string dir = Path.Combine(FindRepoRoot(), "bench", "perfhub", "results");
        Directory.CreateDirectory(dir);

        var run = new PerfRun
        {
            Schema = 3,
            Version = version,
            Label = label,
            Timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            ElapsedSeconds = elapsed.TotalSeconds,
            Environment = new PerfEnvironment
            {
                Os = Environment.OSVersion.ToString(),
                Processor = Environment.ProcessorCount + " 逻辑核",
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Machine = Environment.MachineName,
                GcMode = System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation"
            },
            Measurements = results
        };

        string json = JsonSerializer.Serialize(run, PerfJsonContext.Default.PerfRun);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(Path.Combine(dir, $"history-{stamp}.json"), json);
        // 冒烟批次不顶 latest：否则 HTML 报告的"当前数字"会变成 quick 值，而它不可引用
        if (!label.Contains("quick", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(Path.Combine(dir, "latest.json"), json);
        }

        Console.WriteLine($"[PerfHub] 原始数据已写入 bench/perfhub/results/history-{stamp}.json");

        // 结果库信封（规范 v2 §6）：跨夹具可查询的最小集 + 口径登记 + 健康度
        WriteEnvelope(results, label, version, elapsed, Path.Combine("bench", "perfhub", "results", $"history-{stamp}.json"));
    }

    /// <summary>把本批次映射成结果库信封。Ratio 以同方言同档位的 ADO_NET 行为地板现算
    /// （与报告口径一致）；健康度取**地板行散布的中位数**（取最大值会被 sub-µs 项的调度抖动
    /// 支配，取最小值会漏掉只影响慢项的争用），阈值为本夹具实测噪声底上界。</summary>
    private static void WriteEnvelope(
        List<Measurement> results, string label, string version, TimeSpan elapsed, string detailPath)
    {
        var spreads = new List<double>();
        foreach (Measurement m in results)
        {
            if (m.Implementation != "ADO_NET" || m.ErrorRatio <= 0) continue;
            spreads.Add(m.ErrorRatio * Math.Sqrt(Math.Max(m.Iterations, 1)));
        }

        double floorRatio = 0;
        if (spreads.Count > 0)
        {
            spreads.Sort();
            floorRatio = spreads[spreads.Count / 2];
        }

        var envelope = new PerfResultEnvelope
        {
            Harness = "perfhub",
            Version = version,
            Label = label,
            ElapsedSeconds = elapsed.TotalSeconds,
            DetailPath = detailPath.Replace('\\', '/'),
            Environment = PerfResultWriter.CaptureEnvironment("PerfHub " + version),
            Regime = new PerfResultRegime
            {
                ConnectionConfig = RegimeDescription,
                SessionLifecycle = "per-operation（每操作新建 DataSession，与 Dapper 无状态扩展方法对等）",
                HealthRatio = floorRatio,
                HealthThreshold = HealthThreshold,
                Health = PerfResultWriter.Verdict(floorRatio, HealthThreshold)
            }
        };

        foreach (Measurement m in results)
        {
            Measurement? floor = results.Find(b => b.Implementation == "ADO_NET"
                && b.Dialect == m.Dialect && b.Operation == m.Operation && b.Rows == m.Rows);
            envelope.Items.Add(new PerfResultItem
            {
                Name = m.Operation,
                Dialect = m.Dialect,
                Arm = m.Implementation,
                Tier = m.Rows,
                MeanUs = m.MeanNs / 1000.0,
                AllocBytes = (long)m.AllocatedBytesPerOp,
                Ratio = floor is null || floor.MeanNs <= 0 ? 0 : m.MeanNs / floor.MeanNs,
                RoundTripsPerOp = m.RoundTripsPerOp,
                PreparedReuse = m.PreparedReuse,
                Note = m.Group
            });
        }

        string? path = PerfResultWriter.Write(envelope);
        Console.WriteLine(path is null
            ? "[PerfHub] 结果库信封写入失败（明细已落盘，不影响本次测量）"
            : $"[PerfHub] 结果库信封已写入 {path}");
    }

    /// <summary>健康度阈值（规范 §4.2）——本夹具自适应短跑的地板散布实测 0.21 / 0.26 / 0.31
    ///（2026-09-22 三批 --quick），取上界加余量作阈值；越界即判 noisy。
    /// 不借用 BDN 长跑的 0.10：两者散布不是一个量级（BDN unroll 500 × 10 迭代 vs 自适应 3-50 迭代）。
    /// <para><b>口径提醒</b>：quick 模式只用于冒烟，可引用的数字必须来自全量模式（迭代预算更高）。</para></summary>
    private const double HealthThreshold = 0.35;

    /// <summary>SQLite 连接治理（规范 §4.1 的连接配置口径）——与产品
    /// <c>SqliteProvider.InitializeConnectionAsync</c> 逐条一致，也与 DapperSuite 同值。</summary>
    private const string SqliteGovernancePragma =
        "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL; PRAGMA synchronous = NORMAL; "
        + "PRAGMA cache_size = -65536; PRAGMA temp_store = MEMORY; PRAGMA wal_autocheckpoint = 1000; "
        + "PRAGMA mmap_size = 268435456";

    /// <summary>连接配置口径（规范 §4.1）——三臂同值，改这里必须同步报告与文档。</summary>
    internal const string RegimeDescription =
        "sqlite: 三臂同一组 PRAGMA（foreign_keys=ON / journal_mode=WAL / synchronous=NORMAL / "
        + "cache_size=-65536 / temp_store=MEMORY / wal_autocheckpoint=1000 / mmap_size=268435456）；"
        + "pg/mysql: 驱动默认 + MySQL 追加 AllowLoadLocalInfile=true（三臂同值）";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}

// ─── JSON 序列化模型（AOT 友好：源生成上下文）──

internal sealed class PerfRun
{
    public int Schema { get; set; }
    /// <summary>被测版本标识（如 v5.5.1 / HEAD）——版本对比与增长曲线的横轴。</summary>
    public string Version { get; set; } = "";
    public string Label { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public double ElapsedSeconds { get; set; }
    public PerfEnvironment Environment { get; set; } = new();
    public List<Measurement> Measurements { get; set; } = [];
}

internal sealed class PerfEnvironment
{
    public string Os { get; set; } = "";
    public string Processor { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string Machine { get; set; } = "";
    public string GcMode { get; set; } = "";
}

[JsonSerializable(typeof(PerfRun))]
internal sealed partial class PerfJsonContext : JsonSerializerContext;
