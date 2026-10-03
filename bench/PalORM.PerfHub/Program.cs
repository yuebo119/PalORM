using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
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
///   Tenant      4 项  租户 COUNT（无 where / 带范围）/ 租户全表物化 / OwnedJson 读（LIMIT 50）
///   SessionBatch 1 项 20 条 INSERT 一个批操作（三臂批形态对照）
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
              --warmup-floor all|first-tier 预热时间下限的作用范围（默认 first-tier；all 供交替 A/B 诊断）
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
        bool warmupFloorAll = false;
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
                case "--warmup-floor":
                    // all = 全档都要求预热时间下限（旧行为，A/B 诊断用）；first-tier = 只在每方言首档（默认）
                    warmupFloorAll = args[++i].Equals("all", StringComparison.OrdinalIgnoreCase);
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
        var dialectFailures = new List<(string Dialect, string Reason)>();
        var itemFailures = new List<(string Dialect, int Rows, string Context, string Cause)>();
        var swTotal = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();

        // 档位策略：行数不进测量的项只在最小档跑（见 RowCountSensitive）。
        // 进度条的分母同时确定，每项测量后更新分子。
        _minTier = tiers.Min();
        _planned = PlannedMeasurements(available, tiers, concurrency, threadTiers);
        Console.WriteLine($"[PerfHub] 计划 {_planned} 项测量"
            + $"（{available.Count} 方言 × {tiers.Count} 档 × {ImplCount} 臂"
            + (concurrency ? $" + 并发 {threadTiers.Count} 线程档" : "")
            + $"；行数不进测量的 {OperationNames.Count(op => !RowCountSensitive(op))} 项只在 {_minTier:N0} 档跑）");

        // ── 数据生成基线：与实现、方言无关，每档位一条 ──
        // 回答「测试数据自身的真实内存占用峰值」——不含 ORM、不含数据库。
        foreach (int rows in tiers)
        {
            Measurement gen = Measure.MeasureDataGeneration(rows);
            results.Add(gen);
            _done++;
            Console.WriteLine($"[PerfHub] 数据生成基线 {rows:N0} 行: "
                + $"{Fmt.Bytes(gen.AllocatedBytesPerOp)}/行 · 堆峰 {Fmt.Bytes(gen.PeakHeapBytes)}");
        }

        foreach (DialectInfo info in available)
        {
            Console.WriteLine();
            Console.WriteLine($"══════ {info.DisplayName} ══════");
            // 单方言失败必须被隔离：一个库不可达（实测：托管 MySQL/PG 的虚拟机停机）时，
            // 若整批中断，已测方言的结果会连同未写的信封一起丢。故此处捕获并登记失败，
            // 继续跑其余方言——失败进信封的 sections（kind=dialect-failure），退出码在末尾汇总。
            try
            {
                await RunDialectAsync(info, tiers, concurrency, threadTiers, scale, results, itemFailures, warmupFloorAll, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
            {
                string reason = $"{ex.GetType().Name}: {Shorten(ex.Message)}";
                Console.Error.WriteLine($"[PerfHub] {info.DisplayName} 方言失败，跳过并登记：{reason}");
                dialectFailures.Add((info.DisplayName, reason));
            }
        }

        foreach ((string dialect, string reason) in dialectFailures)
        {
            Console.Error.WriteLine($"[PerfHub] 失败方言登记：{dialect} — {reason}");
        }

        swTotal.Stop();
        Console.WriteLine();
        // 计划数与实际数对账——进度条说谎比没有进度条更糟。差值通常来自失败项
        //（失败项不进 results）或并发探针跳过，故只警告不判失败。
        if (_planned > 0 && _done != _planned)
        {
            Console.Error.WriteLine($"[PerfHub] ⚠ 进度对账不符：计划 {_planned} 项，实际 {_done} 项"
                + $"（差 {_done - _planned:+0;-0}）——常见原因是失败项或并发探针被跳过；"
                + "若差值持续存在，检查 OperationNames 是否与 RunOneImplAsync 的调用点同步");
        }

        Console.WriteLine($"[PerfHub] 完成，共 {results.Count} 项测量，耗时 {Fmt.Duration(swTotal.Elapsed)}"
            + $"（{swTotal.Elapsed.TotalMinutes:F1} 分钟）");

        Save(results, label, version, swTotal.Elapsed, dialectFailures, itemFailures);
        Report.Build();
        return dialectFailures.Count == 0 ? 0 : 1;
    }

    /// <summary>取最内层异常的类型与消息——单项失败的定位信息就在那里，包装层只有上下文。</summary>
    private static string RootCause(Exception ex)
    {
        Exception inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return inner == ex ? ex.GetType().Name : $"{inner.GetType().Name}: {Shorten(inner.Message)}";
    }

    /// <summary>异常消息截断——信封里只留可读的短原因，避免把驱动的长堆栈文本带进结果库。</summary>
    private static string Shorten(string text)
        => text.Length <= 200 ? text : text[..200] + "...";

    /// <summary>单方言全量（档位 × 三臂 + 并发档）——由外层 try 包住，失败只影响本方言。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability",
        "CA2000:DisposeObjectsBeforeLosingScope",
        Justification = "SQLite 分支的 CountingConnection 与 rawConn 共用同一底层连接，实际释放由 rawConn 的 "
            + "await using 负责；包装层只持计数器，无终结器无资源，故不重复释放。")]
    private static async Task RunDialectAsync(
        DialectInfo info, List<int> tiers, bool concurrency, List<int> threadTiers, double scale,
        List<Measurement> results, List<(string Dialect, int Rows, string Context, string Cause)> itemFailures,
        bool warmupFloorAll, CancellationToken ct)
    {
        string cs = Connections.Resolve(info);
        await using DbConnection rawConn = info.OpenConnection(cs);
        await rawConn.OpenAsync(ct).ConfigureAwait(false);

        // 连接配置口径（规范 §4.1）：三臂共用这一条连接，故此处一次治理即全臂同值。
        // 与 DapperSuite 的口径差 D10 逐项一致——SQLite 上不开 WAL/mmap，整档会落在
        // I/O 主导区间，8 µs 级差异不可分辨（2026-09-22 实测：同一修复在两种配置下
        // 分别是 −30% 与 0%）。
        if (info.Dialect == Dialect.Sqlite)
        {
            await using DbCommand pragma = rawConn.CreateCommand();
            pragma.CommandText = SqliteGovernancePragma;
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 维度 8（往返与语句效率）：包一层计数装饰器。包在连接上而不是各臂内部，
        // 三臂（ADO 自建命令 / Dapper 在传入连接上建 / PalORM 会话在调用方连接上建）
        // 才能用同一把尺子量，且能抓到意外 N+1。
        //
        // **只对 SQLite 生效**（2026-09-22 实测）：PG 与 MySQL 的驱动专有协议路径与包装层不兼容——
        // PG 上产品的 PostgreSqlProvider.BulkInsertAsync 直接要求 NpgsqlConnection
        //（ArgumentException: requires an NpgsqlConnection），MySQL 的 LOAD DATA 路径把连接置为
        // Broken 并让后续项全部失败。脱装饰器只能救我方臂（CountingConnection.Unwrap），
        // 救不了产品 Provider 的类型要求，故这两个方言不做维度 8 计数（登记为覆盖缺口）。
        // 不在此处 await using：SQLite 分支的包装层与 rawConn 是同一底层对象，
        // 双份释放虽幂等但会触发 CA2000 误报；实际释放由 rawConn 的 await using 负责
        //（包装层只持有计数器，无终结器、无资源）。
        DbConnection conn = info.Dialect == Dialect.Sqlite
            ? new CountingConnection(rawConn)
            : rawConn;

        // 三实现共用同一连接（B76：建连口径一致）
        IPerfImplementation[] impls = [new AdoNetImpl(info), new DapperImpl(info), new PalormImpl(info)];

        // 批量项的确定性重置用中性播种（只播 perf_s1；三臂经同一路径得到同一张同规模的表）——
        // 各臂自己的 SetupAsync 会把 bench_tenant 一并播到 rows×因子 行，且表布局随臂不同。
        int maxTier = tiers.Max();

        foreach (int rows in tiers)
        {
            Console.WriteLine();
            Console.WriteLine($"── 行数档位 {rows:N0} ──");

            // 预热时间下限只在每方言的**首档**要求：JIT 分层的偏置只在方法首次执行时存在，
            // 而每个方言的驱动代码在它的首档才首次编译。跨档重复预热（旧行为）是纯成本。
            // 该结论经交替 A/B 验证（`--warmup-floor all` 与默认各两轮，比 20000 档读数）。
            _requireWarmupFloor = warmupFloorAll || rows == tiers[0];

            // 阶段 2 新表：每 (方言 × 档位) 播一次，臂无关——测量只读或自清理
            await SetupV2TablesAsync(info, conn, rows, ct).ConfigureAwait(false);

            foreach (IPerfImplementation impl in impls)
            {
                // 单项失败不杀方言：一个 (实现 × 操作 × 方言) 组合失败（实测 MySQL BulkDelete、
                // PG BulkInsert）时，若整段中断，该方言其余 20 项全部丢失。改为记录失败项、继续下一臂，
                // 并把内因（包装异常里的 InnerException）打出来——否则只剩"某某失败"无法定位。
                try
                {
                    await RunOneImplAsync(info, impl, rows, conn, results, scale, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    string cause = RootCause(ex);
                    Console.Error.WriteLine($"    {impl.Name,-9} 跳过 — {ex.Message}｜内因：{cause}");
                    itemFailures.Add((info.DisplayName, rows, $"{impl.Name}: {ex.Message}", cause));
                    // 坏连接会吃掉后续全部项（实测 MySQL：ADO BulkDelete 抛 SocketException 后
                    // 连接变 Broken，Dapper/PalORM 的同档项全被跳过）。重开一次连接继续——
                    // 表在服务端，重连不丢数据；重连失败才算方言级失败（交给外层 catch）。
                    if (conn.State != System.Data.ConnectionState.Open)
                    {
                        Console.Error.WriteLine($"    → 连接状态 {conn.State}，重开连接后继续");
                        await conn.CloseAsync().ConfigureAwait(false);
                        await conn.OpenAsync(ct).ConfigureAwait(false);
                    }
                }
            }

            // 并发吞吐（三实现 × 线程档位）——**只在最高档跑**：并发负载是点查混合
            //（80/20 GetByKey + Insert），与表规模无关，实测两档吞吐差 MySQL/PG 全部在 ±5% 内、
            // SQLite ±8~13%（夹具自身噪声带内），第二档属重复测量（省 1.47 分钟、27 个测量）。
            // 线程档位、时长与分批语义均不变。
            if (concurrency && rows == maxTier)
            {
                Console.WriteLine();
                Console.WriteLine("  并发吞吐（80/20 读写混合，3s）");
                foreach (IPerfImplementation impl in impls)
                {
                    await impl.SetupAsync(conn, rows, ct).ConfigureAwait(false);
                    foreach (int threads in threadTiers)
                    {
                        try
                        {
                            Measurement m = await Measure.ConcurrentAsync(info, impl, rows, threads, 2.0, 0.20, ct)
                                .ConfigureAwait(false);
                            results.Add(m);
                            Console.WriteLine($"{ProgressPrefix()} {impl.Name,-9} {threads,2} 线程  "
                                + $"{m.OpsPerSecond,10:N0} ops/s  "
                                + $"p50={m.P50Ms,6:F2}ms p95={m.P95Ms,6:F2}ms p99={m.P99Ms,6:F2}ms");
                        }
                        catch (InvalidOperationException ex)
                        {
                            // 3 秒的并发探针不该中断整轮测量——记明跳过原因继续跑
                            Console.WriteLine($"    {impl.Name,-9} {threads,2} 线程  跳过 — {ex.Message}");
                        }
                        finally
                        {
                            _done++;
                        }
                    }
                }
            }
        }

        await conn.CloseAsync().ConfigureAwait(false);
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

    /// <summary>对一个 (方言, 实现, 行数) 组合跑全部 26 个单操作测量。
    /// <para>顺序即状态依赖：Build/查询类先跑（要求表内恰好 <paramref name="rows"/> 行），
    /// 写类后跑（每轮用不同主键段，表会增长，但已不影响后续查询类——
    /// 下一个实现进场时 <c>SetupAsync</c> 会重建表）。</para></summary>
    private static async Task RunOneImplAsync(
        DialectInfo info, IPerfImplementation impl, int rows, DbConnection conn,
        List<Measurement> results, double scale, CancellationToken ct)
    {
        // 库重置为 rows 行——查询类操作要求表内恰好 rows 行
        await impl.SetupAsync(conn, rows, ct).ConfigureAwait(false);

        // 中性播种 seeder（只播 perf_s1）：2026-10-03 起 perf_s1 族的重置统一走它——三臂经
        // 同一路径得到同一张同规模的表（各臂自己的 SetupAsync 表布局不同，且会顺带把
        // bench_tenant 播到 rows×轮数 行）；需要 bench_tenant 的项（租户族）走 fullReset。
        var neutralSeeder = new AdoNetImpl(info);
        Task reset(DbConnection c)
        {
            return neutralSeeder.SetupPerfS1Async(c, rows, ct);
        }

        // 需要 bench_tenant 一并重置的项（租户/OwnedJson/SessionBatch 读它）走全量播种
        Task fullReset(DbConnection c)
        {
            return impl.SetupAsync(c, rows, ct);
        }

        // 不变量：**每个读 perf_s1 的测量都从「表内恰好 rows 行」开始**。
        // 这条注释原本只写在上面那一行，但没有任何机制保证它——写类测量会改行数，
        // 而其后的读类测量若不带 reset，就在**被上一个测量改过的表**上测：
        // 实测（2026-09-23）BulkDelete 播种 4 倍于实际消费，删完仍残留约 300 万行，
        // 于是其后的 KeysetPage/WhereIn/Count 在机器速度决定的表规模上测量
        //（消费多少轮由 clamp(预算 ÷ 单次耗时) 决定，单次耗时是机器相关的）——
        // Count 的分配量因此在两批之间从 232 KB/op 掉到 985 B/op（99.6%），
        // 而 SQLite 档基线里记的 Count t20000 是 114 ms（t2000 是 17 µs，6700 倍）。
        // 故 BulkDelete 之后的三项显式带 reset；它之前的项（GetByKey/QueryAll/StreamAll/
        // Update/BulkUpdate）的残留漂移另计，见各项处的注释与规范 §5 待办。
        long[] keys = Dataset.KeySet(rows);
        long[] whereInIds = BuildWhereInIds(rows);
        int bulkDeleteIters = BulkDeleteRounds(rows);
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

        // Update 不带 reset：前一项 Insert 每轮插 1 行，表涨到 rows + iterations（≤+200 行，
        // 相对 20000 档是 +1%）。实测（2026-09-23）给它加 reset 后，**其后 ADO 臂的 BulkInsert
        // 单次从 165ms 跳到 1571ms（9.5×，Dapper/PalORM 臂不变）**，机制未查明；
        // 按"不引入无法解释的行为"原则撤掉，残留漂移（+1%）登记为已知限制。
        await MeasAsync(info, impl, "Update", "CRUD", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.UpdateAsync(c, Dataset.Seed(i * 7919 % rows), ct)
                .ConfigureAwait(false), ct).ConfigureAwait(false);

        // BulkUpdate 排在 BulkInsert 之前——两者都写 perf_s1，而 BulkInsert 每轮把表撑大 rows 行，
        // 其迭代数又由时间预算自适应（逐臂不同）：BulkUpdate 紧随其后时，三臂就在规模不同的表上测
        //（2026-10-03 实测 PG/20000 档起始表 324 万 / 34 万 / 330 万行），比值无可比性。
        // 前置后它跑在 Update 留下的表上（rows + Insert 的 200 行，三臂的 Insert 迭代数都是 200），
        // 行数与内容三臂一致。**不新增 reset**：规范 §5 已登记"给写项加 reset 的尝试回退过
        //（其后 BulkInsert 9.5× 未解异常），机制查明前不得重加"，故本轮用排序消除漂移。
        // 代价如实登记：数万行批更新进数十万行表的大表形态不再由本项覆盖（规范 §5）。
        await MeasAsync(info, impl, "BulkUpdate", "CRUD", rows, conn, results, scale, null,
            async (im, c, i) => _ = await im.BulkUpdateAsync(c, Dataset.SeedRows(rows, 0), ct)
                .ConfigureAwait(false), ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "BulkInsert", "CRUD", rows, conn, results, scale, reset,
            async (im, c, i) => _ = await im.BulkInsertAsync(
                c, Dataset.SeedRows(rows, (long)rows * (i + 1)), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        // 批量删除的播种走中性路径（2026-10-03 起 perf_s1 族统一，见 RunOneImplAsync 顶部 reset 注释）
        Task bulkDeleteReset(DbConnection c)
        {
            return neutralSeeder.SetupPerfS1Async(c, rows * bulkDeleteIters, ct);
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

        // ── Query ──（三项都带 reset：前一项 BulkDelete 会把 perf_s1 删到接近空表，
        // 不带 reset 就在空表上测分页/IN/计数，而空的程度取决于 BulkDelete 消费了多少轮）
        await MeasAsync(info, impl, "KeysetPage", "Query", rows, conn, results, scale, reset,
            async (im, c, i) => _ = await im.KeysetPageAsync(c, i % 20 * 100, 100, ct)
                .ConfigureAwait(false), ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "WhereIn", "Query", rows, conn, results, scale, reset,
            async (im, c, i) => _ = await im.WhereInAsync(c, whereInIds, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "Count", "Query", rows, conn, results, scale, reset,
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
        // 1 条 / 100 条两点已能分离「事务固定开销」与「每条约边际成本」，故不测 10 条那一点
        //（2026-09-23 精简：三点系列的中间点信息量最小）。
        await MeasAsync(info, impl, "TxSingleInsert", "Transaction", rows, conn, results, scale, reset,
            async (im, c, i) => await im.TxSingleInsertAsync(c, Dataset.Seed(rows + i), ct)
                .ConfigureAwait(false), ct).ConfigureAwait(false);

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

        // ── 租户会话 / OwnedJson / SessionBatch（覆盖面补齐批次）──
        // 前三项与 OwnedJsonQuery 都带 reset：测量前表必须恰好 rows 行（10% 软删由种子
        // 决定，租户过滤后可见行是 rows/5 量级）。等价断言照 IncludeJoin 模式——期望值
        // 在此算一次，不进被测路径。
        int expectedTenantVisible = Dataset.TenantVisibleCount(rows);
        int expectedTenantValueVisible = Dataset.TenantVisibleValueCount(rows);
        int expectedOwnedJson = Math.Min(50, expectedTenantVisible);

        await MeasAsync(info, impl, "TenantCount", "Tenant", rows, conn, results, scale, fullReset,
            async (im, c, i) =>
            {
                long n = await im.TenantCountAsync(c, ct).ConfigureAwait(false);
                if (n != expectedTenantVisible)
                    throw new InvalidOperationException(
                        $"TenantCount 结果集不等价：{n}（期望 {expectedTenantVisible}）");
            }, ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "TenantCountWhere", "Tenant", rows, conn, results, scale, fullReset,
            async (im, c, i) =>
            {
                long n = await im.TenantCountWhereAsync(c, rows, ct).ConfigureAwait(false);
                if (n != expectedTenantValueVisible)
                    throw new InvalidOperationException(
                        $"TenantCountWhere 结果集不等价：{n}（期望 {expectedTenantValueVisible}）");
            }, ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "TenantGetAll", "Tenant", rows, conn, results, scale, fullReset,
            async (im, c, i) =>
            {
                List<BenchTenantPost> list = await im.TenantGetAllAsync(c, ct).ConfigureAwait(false);
                if (list.Count != expectedTenantVisible)
                    throw new InvalidOperationException(
                        $"TenantGetAll 结果集不等价：{list.Count} 行（期望 {expectedTenantVisible}）");
            }, ct).ConfigureAwait(false);

        await MeasAsync(info, impl, "OwnedJsonQuery", "Tenant", rows, conn, results, scale, fullReset,
            async (im, c, i) =>
            {
                List<BenchTenantPost> list = await im.OwnedJsonQueryAsync(c, ct).ConfigureAwait(false);
                if (list.Count != expectedOwnedJson)
                    throw new InvalidOperationException(
                        $"OwnedJsonQuery 结果集不等价：{list.Count} 行（期望 {expectedOwnedJson}）");
            }, ct).ConfigureAwait(false);

        // SessionBatchInserts 每轮插 20 行、主键段互不重叠（prepare 不在每轮前调，写操作
        // 在计时循环内累积；种子主键是 1..rows，故段基址从 rows+1 起——warmup/探针的 i=0
        // 与计时轮 i≥1 天然错开：探针前有 reset，计时首段不与探针段重叠）。
        await MeasAsync(info, impl, "SessionBatchInserts", "SessionBatch", rows, conn, results, scale, fullReset,
            async (im, c, i) => _ = await im.SessionBatchInsertsAsync(
                c, rows + 1 + (i * Dataset.SessionBatchRows), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    /// <summary>带操作名上下文的测量包装——失败时报出是哪个操作，便于定位。
    /// 行数不进测量的项在非最小档直接跳过（见 <see cref="RunsAtTier"/>）。</summary>
    private static async Task MeasAsync(
        DialectInfo info, IPerfImplementation impl, string operation, string group, int rows,
        DbConnection conn, List<Measurement> results, double scale,
        Func<DbConnection, Task>? prepare,
        Func<IPerfImplementation, DbConnection, int, Task> action, CancellationToken ct)
    {
        if (!RunsAtTier(operation, rows))
        {
            return;
        }

        try
        {
            // 单测量墙钟耗时——只进控制台，不进结果信封（信封 schema 与门禁都读不到它）。
            // 它回答"哪一项吃掉了跑测时间"，是调预算/砍项的唯一依据：只报指标不报耗时，
            // 优化就只能靠猜。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Measurement m = await Measure.SingleAsync(info, impl, operation, group, rows, action, conn,
                MaxIterations(operation, rows), ct, prepare, BudgetSeconds(operation), scale,
                _requireWarmupFloor).ConfigureAwait(false);
            sw.Stop();
            results.Add(m);
            PrintRow([m], sw.Elapsed);
        }
        catch (Exception ex)
        {
            // 操作名上下文——失败时直接知道是哪个 (实现, 操作, 方言, 行数) 组合
            throw new InvalidOperationException(
                $"{impl.Name} / {operation} / {info.DisplayName} / {rows} 行 失败", ex);
        }
    }

    /// <summary>本轮的最小档位——"行数不进测量"的项只在它上面跑一次（<see cref="RunAsync"/> 设置）。</summary>
    private static int _minTier = Dataset.Tiers[0];

    /// <summary>本轮该档是否要求预热时间下限（JIT 分层偏置只在每方言首档存在，故默认只在那里要求；
    /// <c>--warmup-floor all</c> 强制全档，供交替 A/B 诊断）。由 <see cref="RunDialectAsync"/> 在每档开跑前设置。</summary>
    private static bool _requireWarmupFloor = true;

    /// <summary>进度条的分子/分母——实时标示"跑到哪了、用了多久"。</summary>
    private static int _done;
    private static int _planned;
    private static readonly Stopwatch _progressClock = Stopwatch.StartNew();

    /// <summary>行数是否真的进入该项的测量。判据是操作的签名与动作里有没有用到行数：
    /// <list type="bullet">
    /// <item><c>Build*</c>：不碰库，签名里只有 id / 没有参数——两档是**同一个测量的复制品**</item>
    /// <item><c>InsertReturningId</c>：用每次清空的独立自增表，行数不进测量</item>
    /// <item><c>IncludeJoin</c>：固定 50 父 × 每父 3 子</item>
    /// <item><c>TxSingleInsert</c>/<c>TxHundredInserts</c>：事务内条数固定（1 / 100 条）</item>
    /// <item><c>TxRollback</c>：撤销量上限固定 500（规范已登记"回滚成本由撤销量决定"）</item>
    /// <item><c>TxBulkInsert</c>：与 <c>BulkInsert</c> 近重复（地板臂的 BulkInsert 本身就自开事务包整批），
    /// 保留它是为覆盖"批量装载器在显式事务内"这条产品路径，属语义检查而非规模问题</item>
    /// </list>
    /// <para>2026-09-23 精简：这些项原本两档都跑，等于把同一个测量做两遍——按实测逐项耗时，
    /// 砍掉它们的第二档只省约 1.6% 时间，但省下 7 项 × 3 臂 = 每方言 21 个重复测量。</para>
    /// <para><b>2026-10-03 再精简</b>（本轮全量批逐项两档对照）：单行与索引访问类的规模放大倍数
    /// 仅 1.0~1.6×（GetByKey 1.6/1.0/1.0、Insert 1.2/0.9/1.0、Update 1.6/1.0/1.0、KeysetPage 1.1、
    /// WhereIn 1.1，按 SQLite/MySQL/PG），第二档是重复测量，故把 Insert/Update/KeysetPage/WhereIn
    /// 也移入本表（省第二档墙钟 0.51 分钟、45 个测量）。
    /// <b>GetByKey 例外保留两档</b>：其 2000 档 P/ADO 稳定 0.73~0.79×、20000 档 1.04×，
    /// 第二档正是暴露该异常的参照，查清前不撤（用户 2026-10-03 决定）。</para></summary>
    private static bool RowCountSensitive(string operation) => operation is not (
        "BuildGetByKeySql" or "BuildComplexQuerySql" or "InsertReturningId" or "IncludeJoin"
        or "TxSingleInsert" or "TxHundredInserts" or "TxRollback" or "TxBulkInsert"
        or "OwnedJsonQuery" or "SessionBatchInserts"
        or "Insert" or "Update" or "KeysetPage" or "WhereIn");

    /// <summary>该项是否在给定档位测量。</summary>
    private static bool RunsAtTier(string operation, int rows)
        => rows <= _minTier || RowCountSensitive(operation);

    /// <summary>本轮的测试项名——只用于算进度条的分母，**与各 MeasAsync 调用点一一对应**。
    /// 结束时会把实际测量数与计划数对账，不一致就打警告（进度条说谎比没有进度条更糟）。
    /// 新增或删除测试项时必须同步这里，否则分母失真。</summary>
    private static readonly string[] OperationNames =
    [
        "BuildGetByKeySql", "BuildComplexQuerySql",
        "GetByKey", "QueryAll", "StreamAll", "Insert", "Update",
        "BulkInsert", "BulkUpdate", "BulkDelete",
        "KeysetPage", "WhereIn", "Count",
        "UpsertBatch", "InsertReturningId", "WideQueryAll", "IncludeJoin",
        "TxSingleInsert", "TxHundredInserts", "TxBulkInsert", "TxRollback",
        "TenantCount", "TenantCountWhere", "TenantGetAll", "OwnedJsonQuery", "SessionBatchInserts"
    ];

    /// <summary>PL-4：比值不计比的项。地板这两项直接返回插值字面量（见
    /// <c>AdoNetImpl.BuildGetByKeySql</c>/<c>BuildComplexQuerySql</c>），不做任何 SQL 构造，
    /// 拿它当分母量的是"生成一条 SQL 文本"本身——那是产品相对裸 ADO.NET 的价值而非税
    /// （实测 PG 7.10×/6.59×、MySQL 7.10×/3.22×、SQLite 6.51×/3.66×，全部无判别力）。
    /// 记 0 比值让它们不进索引基线、不进最差比值表、不被门禁卡；绝对值与分配仍照登，
    /// Note 里注明原因。想真比这项就把地板也接到生成器上——那就测不到差异了。</summary>
    private static readonly HashSet<string> NonComparableOperations =
    [
        "BuildGetByKeySql",
        "BuildComplexQuerySql",
    ];

    /// <summary>本轮计划的测量数——进度条的分母。跳过项与未启用的并发档不计入。
    /// <para><b>数据生成基线只在最后加一次</b>：它的循环在方言循环**之外**（每档一条，
    /// 与方言无关）。曾把它算进 per-dialect 再乘方言数，于是三方言时多算 4 条——
    /// 该错误由 <see cref="RunAsync"/> 末尾的对账警告抓出（计划 366 / 实际 362）。</para></summary>
    private static int PlannedMeasurements(
        List<DialectInfo> dialects, List<int> tiers, bool concurrency, List<int> threadTiers)
    {
        int perDialect = tiers.Sum(rows => OperationNames.Count(op => RunsAtTier(op, rows)) * ImplCount);
        if (concurrency)
        {
            // 并发只在最高档跑（见 RunDialectAsync 的门）——按 dialects 计一次，不乘 tiers.Count
            perDialect += threadTiers.Count * ImplCount;
        }

        return (perDialect * dialects.Count) + tiers.Count;
    }

    /// <summary>三臂（ADO.NET / Dapper / PalORM）。</summary>
    private const int ImplCount = 3;

    /// <summary>BulkDelete 的播种轮数——**等于它的迭代上限**（两者必须严格相等，见
    /// <see cref="BulkDeleteMaxRounds"/> 与 <see cref="BulkDeleteSeedRowBudget"/> 的注释）。
    /// <para>每轮删除一段互不重叠的主键窗口（轮 i 删 ((i-1)×rows, i×rows]），故计时轮 i
    /// 需要主键空间到 i×rows。探针占 i=0、计时轮取 i=1..iterations，iterations ≤ 上限
    /// → 播种上限轮即恰好够。曾按 <c>--quick</c> 的 scale 缩放而上限不缩，尾部轮次会删到
    /// 不存在的键（空删最快，中位数不受影响，但分配量按「整轮总分配 ÷ 轮数」算会被低估，
    /// 而分配量正是门禁卡的确定性指标）。</para></summary>
    private static int BulkDeleteRounds(int rows)
        => Math.Clamp(BulkDeleteSeedRowBudget / rows, 3, BulkDeleteMaxRounds);

    /// <summary>BulkDelete 的迭代轮数上限（档位 2000 时的值）。</summary>
    private const int BulkDeleteMaxRounds = 50;

    /// <summary>BulkDelete 播种的总行数预算——**这条是修掉"超配 16.7 倍"的关键**。
    /// <para><b>实测依据</b>（2026-09-23，`.ai/perf-probe/MySqlSeedDiag.cs`，远程 MySQL 8.4）：
    /// 每轮删 20000 个键 = 20 条 DELETE（`BulkSql.BatchRows` = 1000）单轮 **2.02 s**
    ///（每条语句 101 ms，远程 RTT 主导）→ 4 s 预算下只跑 **3 轮**，即实际只需要
    /// 3 × 20000 = **6 万行**；而按上限 50 轮播种要 **100 万行——超配 16.7 倍**。
    /// 播种是纯开销：100 万行的客户端插入 93.6 s + 快照拷贝 94.6 s，
    /// 而每次测量重置两遍、三臂共 6 遍（`DELETE` 107.3 s + 重置 114.0 s），
    /// 合计约 24 分钟只为一个测量项，且三条单命令都超过驱动默认 30 秒超时 → 方言失败。</para>
    /// <para><b>为什么用行数预算而不是直接压上限</b>：上限同时决定档位 2000 的样本数
    /// （那里每轮只删 2000 键、单轮 0.20 s，4 s 预算能跑 19 轮，压上限会白丢样本）。
    /// 行数预算让档位 2000 保持 50 轮不变、档位 20000 降到 10 轮——
    /// 而档位 20000 的轮数本来就由预算决定（3 轮），降上限不损失样本。</para>
    /// <para><b>代价（已实测、需随基线重录）</b>：SQLite 档位 20000 的样本数由 49 降到 10
    ///（其删除快，预算不再是瓶颈）。10 个样本对中位数仍够（BDN 默认 15 轮），
    /// 分配量的复现性从 0.07% 量级降到约 0.3%，仍远优于规范的 1% 门槛。</para></summary>
    private const int BulkDeleteSeedRowBudget = 200_000;

    /// <summary>测项的时间预算（秒）——阶段 3.1 的自适应迭代上限基准。
    /// 迭代数 = clamp(预算 ÷ 预热后单次耗时, 3, 上限)，单测量耗时结构性有界。
    /// <c>--quick</c> 由调用方按 scale 收缩该预算（usage 声明的"迭代次数降到 30%"）。</summary>
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
    /// 无意义的十万次迭代（计时循环自身的开销会污染测量）。
    /// <c>BulkDelete</c> 的上限随档位变化，因为它与播种行数成正比（见 <see cref="BulkDeleteRounds"/>）。</summary>
    private static int MaxIterations(string operation, int rows) => operation switch
    {
        "BuildGetByKeySql" or "BuildComplexQuerySql" => 2_000,
        "BulkDelete" => BulkDeleteRounds(rows),
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

    /// <summary>打印一行测量结果。前缀是进度（<c>[k/n] 已用 Xm Ys</c>），后缀是本测量
    /// （预热 + 探针 + 计时）的墙钟耗时——两者都只进控制台，不进结果信封，不参与任何判定。
    /// <para>进度与耗时回答"跑到哪了、还要多久、时间花在哪"，是调预算与砍项的依据；
    /// 只报指标不报这两项，优化就只能靠猜。ETA 按"已完成项的平均耗时 × 剩余项数"估算，
    /// 而各项成本差三个量级（亚微秒的 Build 到秒级的批量），故它只作量级参考、会跳变。</para></summary>
    private static void PrintRow(Measurement[] rows, TimeSpan elapsed = default)
    {
        foreach (Measurement m in rows)
        {
            _done++;
            string warn = m.ErrorRatio > 0.05 ? " !" : "";
            string line = ProgressPrefix() + " " + m.Implementation.PadRight(9) + " " + m.Operation.PadRight(20)
                + " " + Fmt.Time(m.MedianNs).PadLeft(12) + "  "
                + Fmt.Bytes(m.AllocatedBytesPerOp).PadLeft(10) + "/op  堆峰 "
                + Fmt.Bytes(m.PeakHeapBytes).PadLeft(10) + "  Gen0=" + m.Gen0Collections.ToString().PadLeft(3)
                + (elapsed > TimeSpan.Zero ? "  " + elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s" : "")
                + warn;
            Console.WriteLine(line);
        }
    }

    /// <summary>进度前缀：<c>[ 47/118] 已用 3m12s 余约 5m40s</c>。分母为 0 时（未规划）只报已用时长。</summary>
    private static string ProgressPrefix()
    {
        TimeSpan used = _progressClock.Elapsed;
        if (_planned <= 0)
        {
            return $"[    已用 {Fmt.Duration(used)}]";
        }

        string eta = "—";
        if (_done > 0 && used.TotalSeconds > 2)
        {
            double perItem = used.TotalSeconds / _done;
            int remain = Math.Max(0, _planned - _done);
            eta = Fmt.Duration(TimeSpan.FromSeconds(perItem * remain));
        }

        return $"[{_done,4}/{_planned}] 已用 {Fmt.Duration(used)} 余约 {eta}";
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

    private static void Save(
        List<Measurement> results, string label, string version, TimeSpan elapsed,
        List<(string Dialect, string Reason)> dialectFailures,
        List<(string Dialect, int Rows, string Context, string Cause)> itemFailures)
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
        // latest 只指向"可引用的最近状态"（规范 §6 + B86）——判据与信封侧
        // PerfResultWriter.IsSubsetLabel 同一真源。此处原先只排 quick，两个缺口都实测踩到：
        //   ①子集批次（ab/、filtered、gate-set…）会把"当前数字"换成单方言单档的读数，
        //     与规范 §6「带这些标记的批次不顶 latest」**文字冲突**（三方一致缺口）
        //   ②零真测量批次（方言全失败）会顶上去——2026-09-25 实测 PG+MySQL 连接握手超时，
        //     只剩 DataGen 两项却顶了 latest.json；B86 的"空批次顶 latest"当时只修了信封侧
        // 历史文件照旧落盘：跑过什么、包括失败，都是事实，只是不该被读成"当前状态"。
        bool hasRealMeasurement = results.Exists(static m => m.Implementation != "DataGen");
        // 覆盖面回退（2026-10-02）：label 是"显式声明"，但同一族缺陷已三度复发（ab/ → gate-set →
        // verify-），每次都是"跑了单方言却没声明成子集"。判定不能只信 label：**新批次的方言集
        // 若是上一个可引用批次的真子集，说明覆盖面缩水，无论 label 写了什么都不得顶 latest**。
        // 只挡真子集（更少的方言），同方言重跑不受影响；上一个 latest 不存在/不可解析时放行。
        string? coverageReason = DetectCoverageRegression(dir, json);
        if (!PerfResultWriter.IsSubsetLabel(label) && hasRealMeasurement && coverageReason is null)
        {
            File.WriteAllText(Path.Combine(dir, "latest.json"), json);
        }
        else
        {
            string why = !hasRealMeasurement
                ? $"零真测量 label={label}"
                : coverageReason ?? $"子集 label={label}";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[PerfHub] 跳过 latest（{why}）：保留上一个可引用批次"));
        }

        Console.WriteLine($"[PerfHub] 原始数据已写入 bench/perfhub/results/history-{stamp}.json");

        // 结果库信封（规范 v2 §6）：跨夹具可查询的最小集 + 口径登记 + 健康度
        WriteEnvelope(results, label, version, elapsed,
            Path.Combine("bench", "perfhub", "results", $"history-{stamp}.json"), dialectFailures, itemFailures);
    }

    /// <summary>覆盖面回退检测：本批次的方言集是否为上一个可引用批次的**真子集**。
    /// <para><b>为什么按方言判</b>：方言是最低成本、最不易误判的覆盖面维度——档位与项数会随
    /// "行数不进测量的项只在 2000 档跑"等既有规则波动，方言集只由 <c>--dialects</c> 决定。
    /// 一次 <c>--dialects sqlite</c> 的跑测必然丢掉 MySQL/PG 的读数，这正是要挡的形态
    ///（2026-10-02 实测：434 项被 128 项顶掉，两方言整体消失且无提示）。</para>
    /// <para><b>只挡真子集</b>：同方言重跑、方言超集（补跑）都放行；上一个 latest 不存在或
    /// 不可解析（历史文件格式漂移、被删）时返回 null 放行——该守卫是增量护栏，
    /// 不做"读不到就不许写"的失败关闭（那会让一次格式演进永久锁死 latest 更新）。</para>
    /// <para>返回 null 表示放行，否则返回人类可读的拦截原因。</para></summary>
    private static string? DetectCoverageRegression(string dir, string newJson)
    {
        string latestPath = Path.Combine(dir, "latest.json");
        if (!File.Exists(latestPath))
        {
            return null;
        }

        try
        {
            PerfRun? incoming = JsonSerializer.Deserialize(newJson, PerfJsonContext.Default.PerfRun);
            PerfRun? previous = JsonSerializer.Deserialize(
                File.ReadAllBytes(latestPath), PerfJsonContext.Default.PerfRun);
            if (incoming is null || previous is null)
            {
                return null;
            }

            var prevDialects = new HashSet<string>(StringComparer.Ordinal);
            foreach (Measurement m in previous.Measurements)
            {
                if (m.Implementation != "DataGen")
                {
                    _ = prevDialects.Add(m.Dialect);
                }
            }

            var newDialects = new HashSet<string>(StringComparer.Ordinal);
            foreach (Measurement m in incoming.Measurements)
            {
                if (m.Implementation != "DataGen")
                {
                    _ = newDialects.Add(m.Dialect);
                }
            }

            if (prevDialects.Count == 0 || newDialects.Count == 0)
            {
                return null;
            }

            // 真子集判定：新方言集被旧集合完全覆盖且严格更少
            if (newDialects.Count < prevDialects.Count && newDialects.IsSubsetOf(prevDialects))
            {
                return $"方言覆盖面缩水（本批 {string.Join('/', newDialects)} ⊂ 上一批 {string.Join('/', prevDialects)}）"
                    + "，请给跑测加子集 label（如 --label verify-…）或补跑其余方言";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 读不到旧批次不是本次写入的错误——放行并说明，不阻塞 latest 更新
            Console.WriteLine($"[PerfHub] 覆盖面回退检测跳过（上一个 latest 不可解析）：{ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>把本批次映射成结果库信封。Ratio 以同方言同档位的 ADO_NET 行为地板现算
    /// （与报告口径一致）；健康度取**地板行散布的中位数**（取最大值会被 sub-µs 项的调度抖动
    /// 支配，取最小值会漏掉只影响慢项的争用），阈值为本夹具实测噪声底上界。
    /// <para>失败进 <c>sections</c>：<c>dialect-failure</c>（整个方言不可达）与
    /// <c>item-failure</c>（单个"实现 × 操作"组合失败，含内因）——部分失败时已测项照常落库。</para></summary>
    private static void WriteEnvelope(
        List<Measurement> results, string label, string version, TimeSpan elapsed, string detailPath,
        List<(string Dialect, string Reason)> dialectFailures,
        List<(string Dialect, int Rows, string Context, string Cause)> itemFailures)
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
            // PL-4：Build* 两项的比值记 0（"不计比"）——地板实现（Implementations.cs:588-593）
            // 直接 return 插值字面量，不构造任何 SQL；拿它当分母量的是"生成一条 SQL 文本"
            // 这件事本身，而那是产品相对裸 ADO.NET 的价值而不是税。绝对值与分配仍照登。
            bool comparable = !NonComparableOperations.Contains(m.Operation);
            envelope.Items.Add(new PerfResultItem
            {
                // 并发行的唯一标识必须含线程档：同一 (operation, 方言, 档位) 下有 1/4/8 三行，
                // 名字不带线程会让结果库与门禁的"同名键"相撞（实测 ToDictionary 抛 Key 重复）
                Name = m.ConcurrencyThreads > 0
                    ? $"{m.Operation} (t{m.ConcurrencyThreads})"
                    : m.Operation,
                Dialect = m.Dialect,
                Arm = m.Implementation,
                Tier = m.Rows,
                MedianUs = m.MedianNs / 1000.0,
                MeanUs = m.MeanNs / 1000.0,
                AllocBytes = (long)m.AllocatedBytesPerOp,
                RoundTripsPerOp = m.RoundTripsPerOp,
                PreparedReuse = m.PreparedReuse,
                Note = comparable ? m.Group : m.Group + "｜地板返回字面量，比值不计比（PL-4）",
                // 基数用中位数（2026-10-02）：均值对计时离群值极敏感，且与报告侧口径不一致。
                // 实测 Insert/SQLite/2000 的 ADO 臂中位 17.1µs / 均值 45.6µs ——用均值当分母
                // 会把基线录成 0.46（中位口径 1.13），下一批必然假报 FAIL。详见 PerfResultItem 文档。
                Ratio = !comparable || floor is null || floor.MedianNs <= 0 ? 0 : m.MedianNs / floor.MedianNs,
            });
        }

        foreach ((string dialect, string reason) in dialectFailures)
        {
            envelope.Sections.Add(new PerfResultSection
            {
                Kind = "dialect-failure",
                Dialect = dialect,
                Label = "connect/open",
                Note = reason
            });
        }

        foreach ((string dialect, int rows, string context, string cause) in itemFailures)
        {
            envelope.Sections.Add(new PerfResultSection
            {
                Kind = "item-failure",
                Dialect = dialect,
                Label = rows.ToString(CultureInfo.InvariantCulture),
                Note = context + "｜内因：" + cause,
                Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["rows"] = rows
                }
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
