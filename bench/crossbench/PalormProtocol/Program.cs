using System.Data.Common;
using System.Text.Json;
using FreeSql;
using PalORM.PerfHub;

namespace PalORM.PerfHub;

/// <summary>交叉基准 Runner——操作集、prepare 语义、迭代上限与操作顺序逐位对齐
/// PerfHub 的 RunOneImplAsync（PalORM 原测试协议），仅把 Dapper 臂换成 FreeSql 臂。
/// 测量引擎（Measure.SingleAsync：预热时间预算 + 自适应迭代 + 分配/堆采样）与 PerfHub 同源。
/// 每方言每档两遍反序（pass 0 正序、pass 1 反序），取两遍中位数聚合，消除臂顺序相位差。</summary>
internal static partial class Program
{
    internal const string Pragma =
        "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL; PRAGMA synchronous = NORMAL; "
        + "PRAGMA cache_size = -65536; PRAGMA temp_store = MEMORY; PRAGMA wal_autocheckpoint = 1000; "
        + "PRAGMA mmap_size = 268435456; PRAGMA analysis_limit = 400; PRAGMA optimize = 0x10002";

    /// <summary>仓库根定位（向上找 PalORM.slnx，与 PerfHub.Program.FindRepoRoot 同法）。</summary>
    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("未找到仓库根（PalORM.slnx）——从仓内运行 dotnet run。");
    }

    /// <summary>五臂交叉基准——ADO.NET/Dapper/RepoDB/FreeSql/PalORM（2026-10-26: RepoDB v1.17.0 新增）。
    /// <para><b>RepoDB 集成教训吸收</b>：
    /// ①实体映射走 DynamicMapping（零特性，FieldMapper 静态缓存预热）；
    /// ②批量操作自动路由驱动级 API（MySQL LOAD DATA/PG COPY/SQLite 行预编译）；
    /// ③事务传 DbTransaction 参数（无需 WithTransaction 包装，与 ADO 基准同形态）；
    /// ④AOT 兼容需 TrimmerRootAssembly（与 FreeSql 同模式），本夹具显式预热映射表；
    /// ⑤SQLite PRAGMA 独立连接（池连接无法注入 PRAGMA，与 FreeSql 同坑）。</para></summary>
    internal static string ResultsDir() => Path.Combine(FindRepoRoot(), "bench", "crossbench", "results");

    internal static string CurrentCommit() => "crossbench";

    private const int BulkDeleteMaxRounds = 50;
    private const int BulkDeleteSeedRowBudget = 200_000;

    private static async Task<int> Main(string[] args)
    {
        int[] tiers = args.Where(static a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
        if (tiers.Length == 0)
        {
            tiers = [2_000, 20_000];
        }

        string[] dialectFilter = [];
        int dialectIndex = Array.IndexOf(args, "--dialects");
        if (dialectIndex >= 0 && dialectIndex + 1 < args.Length)
        {
            dialectFilter = args[dialectIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        List<Dialect> dialects = dialectFilter.Length == 0
            ? [Dialect.Sqlite, Dialect.MySql, Dialect.PostgreSql]
            : [.. dialectFilter.Select(ParseDialect)];

        string[] opFilter = [];
        int opsIndex = Array.IndexOf(args, "--ops");
        if (opsIndex >= 0 && opsIndex + 1 < args.Length)
        {
            opFilter = args[opsIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        // --arms ado,dapper,freesql,palorm：臂子集（快速复核路径；缺臂的批不进基线）
        string[] armFilter = [];
        int armsIndex = Array.IndexOf(args, "--arms");
        if (armsIndex >= 0 && armsIndex + 1 < args.Length)
        {
            armFilter = args[armsIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        // --fast：全局预算缩放 0.4（冒烟专用——基线/主张必须全预算）
        double globalScale = args.Contains("--fast") ? 0.4 : 1.0;

        SQLitePCL.Batteries_V2.Init();

        // 环境预检（2026-11-02）：三方言认证级连通 + 容器自愈 + MySQL local_infile。
        // 必须在任何测量之前——此前容器掉线时会在跑到最后一个方言才连接失败，整批 20 分钟作废。
        if (!args.Contains("--no-preflight"))
        {
            List<DialectInfo> preflightTargets = [.. dialects.Select(DialectInfo.Of)];
            if (!await Preflight.CheckAllAsync(preflightTargets, autoFix: true, Console.Out).ConfigureAwait(false))
            {
                Console.Error.WriteLine("CrossBench: 环境预检未通过，未开始测量");
                return 1;
            }
        }

        // --sql-audit：SQL 文本审计（方案 B，2026-11-02）——只执行不计量，
        // 逐 (方言 × 操作 × 组) 记录实际执行的 SQL 文本并产出对照报告。
        // 必须早于臂构造设置 Enabled：各臂在构造时按该位决定装不装捕获钩子。
        if (args.Contains("--sql-audit"))
        {
            SqlAuditCapture.Enabled = true;
            return await RunSqlAuditAsync(dialects, armFilter, opFilter, tiers.Min()).ConfigureAwait(false);
        }

        var results = new List<Measurement>();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        // 断点续跑（2026-11-02）：每完成一个方言就把已测数据落一份 .partial，并记一行 .partial.done。
        // 容器掉线一类的环境故障会让整批 20 分钟作废；有断点后只需补跑未完成的方言
        // （--resume <partial 路径>）。.partial 后缀不匹配 crossbench-*.json，不会被完整批扫描误收。
        string outputDir = ResultsDir();
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, $"crossbench-{stamp}.json");
        string? resumePath = OptionValue(args, "--resume");
        string partialPath = resumePath ?? outputPath + ".partial";
        string donePath = partialPath + ".done";
        var completedDialects = new HashSet<string>(StringComparer.Ordinal);
        if (resumePath is not null)
        {
            if (!File.Exists(resumePath))
            {
                Console.Error.WriteLine($"CrossBench: --resume 指定的断点文件不存在：{resumePath}");
                return 1;
            }

            List<Measurement>? prior = JsonSerializer.Deserialize<List<Measurement>>(
                await File.ReadAllTextAsync(resumePath).ConfigureAwait(false));
            if (prior is null)
            {
                Console.Error.WriteLine($"CrossBench: 断点文件无法解析：{resumePath}");
                return 1;
            }

            results.AddRange(prior);
            if (File.Exists(donePath))
            {
                foreach (string line in await File.ReadAllLinesAsync(donePath).ConfigureAwait(false))
                {
                    if (line.Trim().Length > 0)
                    {
                        _ = completedDialects.Add(line.Trim());
                    }
                }
            }

            Console.WriteLine(
                $"[CrossBench] 续跑：载入 {prior.Count} 条既有测量，已完成方言 [{string.Join(',', completedDialects)}]");
        }

        foreach (Dialect dialect in dialects)
        {
            DialectInfo info = DialectInfo.Of(dialect);
            if (completedDialects.Contains(info.DisplayName))
            {
                Console.WriteLine($"[CrossBench] 跳过 {info.DisplayName}（断点里已完成）");
                continue;
            }

            string cs = Connections.Resolve(info);
            await using DbConnection conn = info.OpenConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);
            if (dialect == Dialect.Sqlite)
            {
                await ExecAsync(conn, Pragma).ConfigureAwait(false);
            }

            // 跨库横评（2026-10-30 立项；2026-11-01 按用户指令移除 Insight.Database 与 SmartSql——
            // 两者在性能测试里长期构造失败，只产出 N/A 行，不再参测）。
            // 新臂构造失败（驱动缺依赖等）仍走安全构造跳过并登记，不拖垮整批。
            // 臂按 --arms 令牌惰性构造（2026-10-30）：不测的臂不构造——跨库静态初始化
            //（跨库静态初始化如 EF 模型、SqlSugar 客户端等）只应为参测组付出。
            // 构造与 SQL 审计共用（BuildArms，见 SqlAudit.cs）。
            IPerfImplementation[] impls = [.. BuildArms(info, armFilter)];
            if (impls.Length == 0)
            {
                throw new ArgumentOutOfRangeException(
                    "--arms", string.Join(',', armFilter),
                    "有效臂名：ado、dapper、dapper-aot、repodb、freesql、palorm、linq2db、efcore、sqlsugar");
            }
            try
            {
                foreach (int rows in tiers)
                {
                    int bulkDeleteRounds = Math.Clamp(BulkDeleteSeedRowBudget / rows, 3, BulkDeleteMaxRounds);
                    // 每档开始：重建 perf_s1 + 两张快照（rows 档 / BulkDelete 的 rows×rounds 档）
                    await BuildSnapshotsAsync(conn, dialect, rows, bulkDeleteRounds).ConfigureAwait(false);

                    long[] whereInIds = BuildWhereInIds(rows);
                    long[] keys = Dataset.KeySet(rows);

                    Console.WriteLine();
                    Console.WriteLine($"── {info.DisplayName} rows={rows:N0} ──");

                    for (int pass = 0; pass < 2; pass++)
                    {
                        // 读不变量：每个读 perf_s1 的测量都从「表内恰好 rows 行」开始——
                        // 两遍的读操作前各重置一次（PerfHub 的交错读同语义）
                        await RestoreAsync(conn, dialect, "perf_s1_seed").ConfigureAwait(false);
                        if (dialect == Dialect.PostgreSql)
                        {
                            // 独立小写表组不在共享表上留状态，pass 起点同样要复位
                            await RestoreLowercaseAsync(
                                conn, dialect, "perf_s1_seed", Dataset.S1LowercaseTableName).ConfigureAwait(false);
                        }

                        IPerfImplementation[] order = pass == 0 ? impls : [.. impls.Reverse()];

                        // 读一致性守卫（每方言一次，不计入测量，2026-11-01 扩面）：全组参与，
                        // 期望值由 Dataset.Seed 公式确定性推导（不依赖任何一组当 oracle）——
                        // 同一份种子数据、同一批逻辑操作，任何组返回不同结果都说明 SQL 或映射有错。
                        // 覆盖 GetByKey / QueryAll（行数 + 主键集合 + 逐行字段）/ KeysetPage /
                        // WhereIn / Count 五个读操作。
                        if (pass == 0 && rows == tiers[0])
                        {
                            List<string> validationMessages = new();
                            foreach (IPerfImplementation arm in impls)
                            {
                                List<string> problems = await ValidateArmReadsAsync(dialect, conn, arm, rows)
                                    .ConfigureAwait(false);
                                if (problems.Count == 0)
                                {
                                    Console.WriteLine(
                                        $"    {arm.Name} 读一致校验 OK（GetByKey/QueryAll/KeysetPage/WhereIn/Count）");
                                }
                                else
                                {
                                    validationMessages.Add($"{arm.Name}: {string.Join("; ", problems)}");
                                }
                            }

                            if (validationMessages.Count > 0)
                            {
                                throw new InvalidOperationException(string.Join(" | ", validationMessages));
                            }

                            // 写效果校验（每方言一次，不计入测量，全组参与）：逐操作断言
                            // 行数/键集/回滚/枚举列的净效果，非仅值回读。全组都参与——
                            // 基准与老臂的写语义同样必须与协议一致。
                            foreach (IPerfImplementation arm in impls)
                            {
                                await ValidateArmWritesAsync(dialect, conn, arm, rows).ConfigureAwait(false);
                            }
                        }

                        foreach (Op op in BuildOperations(dialect, rows, keys, whereInIds, bulkDeleteRounds))
                        {
                            if (op.MinTierOnly && rows != tiers.Min())
                            {
                                continue;
                            }

                            if (opFilter.Length > 0 && !opFilter.Contains(op.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            foreach (IPerfImplementation impl in order)
                            {
                                // prepare 随臂适配：独立表组的复位目标是它自己的物理表，
                                // 其余组仍是共享表（测量语义不变，仅换委托签名）
                                Func<DbConnection, Task>? prepare = op.Prepare is null
                                    ? null
                                    : c => op.Prepare!(c, impl);
                                Measurement m = await Measure.SingleAsync(
                                    info, impl, op.Name, op.Group, rows, op.Action, conn, op.MaxIterations,
                                    CancellationToken.None, prepare,
                                    budgetSeconds: op.Budget,
                                    scale: globalScale,
                                    warmupScale: op.WarmupScale,
                                    // JIT 分层下限只对「该方法的首次测量」有意义：它按 (方法, 实现)
                                    // 只发生一次，即 pass 0 的最小档。pass 1 与 20000 档跑的是已在
                                    // tier1 上的同一方法，再付 0.5s 纯属重复（2026-11-02 实测
                                    // 每次 0.5s × 342 个格 ≈ 2.3 分钟）。
                                    requireWarmupFloor: rows == tiers[0] && pass == 0,
                                    // 探针用预热末段中位数替代：单次探针受首调用冷态影响可慢 40%
                                    // （实测 QueryAll p0 迭代数 97 对 p1 的 148），既高估预算又
                                    // 多付一次被测操作。PerfHub 侧同名参数的 A/B 见 Measure 文档。
                                    estimateFromWarmup: true).ConfigureAwait(false);
                                results.Add(m);
                                Console.WriteLine(
                                    $"p{pass} {op.Name,-16} {impl.Name,-9} {m.MedianNs / 1000.0,10:F1} us  "
                                    + $"{m.AllocatedBytesPerOp,10:F0} B  iters={m.Iterations,6}  err={m.ErrorRatio:P1}");
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (IPerfImplementation impl in impls)
                {
                    (impl as IDisposable)?.Dispose();
                }
            }

            // 本方言全部测量写完，落一次断点（容器或进程在后续方言挂掉时不必从头再来）
            _ = completedDialects.Add(info.DisplayName);
            await WriteProgressAsync(partialPath, donePath, results, info.DisplayName).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(results, JsonOptions)).ConfigureAwait(false);
        foreach (string stale in new[] { partialPath, donePath })
        {
            if (File.Exists(stale))
            {
                File.Delete(stale);
            }
        }

        Console.WriteLine($"WROTE {outputPath}");
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>断点落盘：整批结果先写 .partial，再追加一行完成方言名。放在每个方言跑完之后，
    /// 所以进程中途被杀时 .partial 里只有已完成的方言，续跑不会混入半截数据。</summary>
    private static async Task WriteProgressAsync(
        string partialPath, string donePath, List<Measurement> results, string dialect)
    {
        await File.WriteAllTextAsync(partialPath, JsonSerializer.Serialize(results, JsonOptions)).ConfigureAwait(false);
        await File.AppendAllTextAsync(donePath, dialect + Environment.NewLine).ConfigureAwait(false);
        Console.WriteLine(
            $"PARTIAL {partialPath}（{results.Count} 条，已完成 {dialect}；中断后可用 --resume \"{partialPath}\" 续跑）");
    }

    private static string? OptionValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>条件展开助手——want 为真时展开臂数组，否则空（配合 --arms 令牌惰性构造）。</summary>
    private static IEnumerable<IPerfImplementation> When(bool want, params IPerfImplementation?[] arms)
        => want ? arms.OfType<IPerfImplementation>() : [];

    /// <summary>臂安全构造——初始化失败（驱动缺依赖等）时返回 null 并在控制台登记，
    /// 方言级失败不拖垮整批，该组 N/A 进报告。</summary>
    private static IPerfImplementation? TryArm(Func<IPerfImplementation> factory, string armName)
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [WARN] {armName} 臂构造失败（该方言 N/A）：{ex.Message}");
            return null;
        }
    }

    /// <summary>读一致性校验（每方言每臂一次，不计入测量）：以 <see cref="Dataset.Seed"/> 的
    /// 确定性公式为期望值，逐组核对五个读操作。任一组返回不同结果都说明其 SQL 或映射与协议不符。
    /// <para>QueryAll 做全量校验：行数、主键集合恰为 1..rows、逐行字段对公式。此前只校验
    /// count 与末行 Id，中间行错位无法发现。</para></summary>
    private static async Task<List<string>> ValidateArmReadsAsync(
        Dialect dialect, DbConnection conn, IPerfImplementation arm, int rows)
    {
        List<string> problems = [];
        CancellationToken ct = CancellationToken.None;

        foreach (int id in new[] { 1, rows / 2, rows }.Distinct())
        {
            if (id <= 0)
            {
                continue;
            }

            S1Row? got = await arm.GetByKeyAsync(conn, id, ct).ConfigureAwait(false);
            S1Row expected = Dataset.Seed(id - 1);
            if (!SameRow(got, expected))
            {
                problems.Add($"GetByKey({id}) 得到 {DescribeRow(got)}，期望 {DescribeRow(expected)}");
            }
        }

        List<S1Row> all = await arm.QueryAllAsync(conn, ct).ConfigureAwait(false);
        if (all.Count != rows)
        {
            problems.Add($"QueryAll 返回 {all.Count} 行，期望 {rows}");
        }
        else
        {
            HashSet<long> seen = new(all.Count);
            int mismatched = 0;
            string? firstMismatch = null;
            foreach (S1Row row in all)
            {
                if (!seen.Add(row.Id))
                {
                    problems.Add($"QueryAll 主键重复 {row.Id}");
                    break;
                }

                S1Row expected = Dataset.Seed(row.Id - 1);
                if (!SameRow(row, expected))
                {
                    mismatched++;
                    firstMismatch ??= $"Id={row.Id} 得到 {DescribeRow(row)}，期望 {DescribeRow(expected)}";
                }
            }

            if (seen.Count == rows && (seen.Min() != 1 || seen.Max() != rows))
            {
                problems.Add($"QueryAll 主键范围 {seen.Min()}..{seen.Max()}，期望 1..{rows}");
            }

            if (mismatched > 0)
            {
                problems.Add($"QueryAll 有 {mismatched} 行字段不符（首个：{firstMismatch}）");
            }
        }

        List<S1Row> page = await arm.KeysetPageAsync(conn, 0, 100, ct).ConfigureAwait(false);
        bool pageOk = page.Count == 100
            && page[0].Id == 1
            && page[^1].Id == 100
            && page.Select(r => r.Id).SequenceEqual(Enumerable.Range(1, 100).Select(i => (long)i));
        if (!pageOk)
        {
            problems.Add(
                $"KeysetPage 返回 {page.Count} 行 "
                + $"[{(page.Count > 0 ? page[0].Id : 0)}..{(page.Count > 0 ? page[^1].Id : 0)}]，期望 100 行 [1..100]");
        }

        long[] keys = new[] { 1L, 2L, rows / 2L, rows - 1L, rows }.Distinct().Order().ToArray();
        List<S1Row> inRows = await arm.WhereInAsync(conn, keys, ct).ConfigureAwait(false);
        long[] gotIds = [.. inRows.Select(r => r.Id).Order()];
        if (!gotIds.SequenceEqual(keys))
        {
            problems.Add($"WhereIn 命中 [{string.Join(',', gotIds)}]，期望 [{string.Join(',', keys)}]");
        }

        long count = await arm.CountAsync(conn, ct).ConfigureAwait(false);
        if (count != rows)
        {
            problems.Add($"Count={count}，期望 {rows}");
        }

        return problems;
    }

    private static bool SameRow(S1Row? actual, S1Row expected)
        => actual is not null && actual.Id == expected.Id && actual.Name == expected.Name
            && actual.Qty == expected.Qty && actual.Price == expected.Price
            && actual.Marker == expected.Marker;

    private static string DescribeRow(S1Row? row)
        => row is null
            ? "null"
            : $"Id={row.Id}/{row.Name}/{row.Qty}/{row.Price}/{row.Marker}";

    /// <summary>写后映射校验（每方言每臂一次，不计入测量）：用改过值的种子行走
    /// BulkUpdate 与 UpsertBatch 两条写路径，读回逐字段比对后复原。
    /// <para>为什么需要：读校验只覆盖 SELECT；批量写的列序/文档序由各臂映射与语句构建决定，
    /// 错位会静默写入错误数据而计时照常。BulkUpdate 与 UpsertBatch 走不同代码路径
    ///（前者 UPDATE，后者 INSERT … ON CONFLICT / ON DUPLICATE KEY / 文档覆写），两条都要验。</para>
    /// <para>2026-10-30 从 RepoDb 专用泛化为全部参与守卫的臂（读守卫名单同源）；
    /// ADO/Dapper/PalORM 的写后校验仍是历史批次的已知缺口（ADR-S「已知边界」）。</para></summary>
    /// <summary>校验间复原——按组目标的快照 Restore 回种子态。</summary>
    private static Task RestoreForValidationAsync(
        Dialect dialect, DbConnection conn, int rows, IPerfImplementation arm)
        => RestoreForArmAsync(conn, dialect, "perf_s1_seed", arm);

    private static Task ValidateArmWritesAsync(
        Dialect dialect, DbConnection conn, IPerfImplementation arm, int rows)
        => ValidateArmWritesCoreAsync(dialect, conn, arm, rows);

    /// <summary>写路径效果校验（每方言每臂一次，不计入测量，2026-11-02 扩面）：不只看值回读，
    /// 还断言每个写操作对表状态的净效果——影响行数、键集增减、回滚不变。
    /// <para><b>为什么需要</b>：结果一致性守卫只覆盖读路径。写路径若生成错 SQL（多删、少插、
    /// 回滚失效、键集错位），读守卫看不见，测量照常出数。当前覆盖十一个写操作：
    /// Insert / Update / BulkInsert / BulkDelete / BulkUpdate / UpsertBatch /
    /// TxSingleInsert / TxHundredInserts / TxBulkInsert / TxRollback / EnumInserts。</para>
    /// <para><b>2026-11-02 起 ADO/Dapper/PalORM 也纳入</b>（原为「ADR-S 已知边界」），
    /// 基准与老臂的写语义同样必须与协议一致，否则比值基线本身失真。</para></summary>
    private static async Task ValidateArmWritesCoreAsync(
        Dialect dialect, DbConnection conn, IPerfImplementation arm, int rows)
    {
        CancellationToken ct = CancellationToken.None;
        List<string> problems = [];
        int probeRows = Math.Min(rows, 200);

        async Task ResetAsync() => await RestoreForValidationAsync(dialect, conn, rows, arm)
            .ConfigureAwait(false);

        async Task<long> CountAsync() => await arm.CountAsync(conn, ct).ConfigureAwait(false);

        async Task CheckCountAsync(string op, Task<long> actualTask, long expected)
        {
            long actual = await actualTask.ConfigureAwait(false);
            if (actual != expected)
            {
                problems.Add($"{op} 后行数 {actual}，期望 {expected}");
            }
        }

        async Task CheckRowAsync(string op, S1Row expected)
        {
            S1Row? actual = await arm.GetByKeyAsync(conn, expected.Id, ct).ConfigureAwait(false);
            if (!SameRow(actual, expected))
            {
                problems.Add($"{op} 值不符：得到 {DescribeRow(actual)}，期望 {DescribeRow(expected)}");
            }
        }

        // ── 1. Insert：净增 1 行且新行值可读回 ──
        await ResetAsync().ConfigureAwait(false);
        S1Row inserted = Dataset.Seed(rows);
        await arm.InsertAsync(conn, inserted, ct).ConfigureAwait(false);
        await CheckCountAsync("Insert", CountAsync(), rows + 1).ConfigureAwait(false);
        await CheckRowAsync("Insert", inserted).ConfigureAwait(false);

        // ── 2. Update：行数不变、目标行值改动 ──
        await ResetAsync().ConfigureAwait(false);
        S1Row updated = Dataset.Seed(rows / 2);
        updated.Name = "audit-upd";
        updated.Qty = 42;
        updated.Price = 42.5m;
        updated.Marker = 4242;
        await arm.UpdateAsync(conn, updated, ct).ConfigureAwait(false);
        await CheckCountAsync("Update", CountAsync(), rows).ConfigureAwait(false);
        await CheckRowAsync("Update", updated).ConfigureAwait(false);

        // ── 3. BulkInsert：净增 probeRows 行（主键段错开到 100 万起，避开种子键）──
        await ResetAsync().ConfigureAwait(false);
        List<S1Row> bulkInserted = Dataset.SeedRows(probeRows, 1_000_000);
        await arm.BulkInsertAsync(conn, bulkInserted, ct).ConfigureAwait(false);
        await CheckCountAsync("BulkInsert", CountAsync(), rows + probeRows).ConfigureAwait(false);
        await CheckRowAsync("BulkInsert 首行", bulkInserted[0]).ConfigureAwait(false);
        await CheckRowAsync("BulkInsert 末行", bulkInserted[^1]).ConfigureAwait(false);

        // ── 4. BulkDelete：恰好删掉指定键集，其余行仍在 ──
        await ResetAsync().ConfigureAwait(false);
        long[] victims = [.. new long[] { 1, 2, rows / 2, rows - 1, rows }.Distinct()];
        long survivor = victims.Contains(3) ? 4 : 3;
        await arm.BulkDeleteAsync(conn, [.. victims.Cast<object>()], ct).ConfigureAwait(false);
        await CheckCountAsync("BulkDelete", CountAsync(), rows - victims.Length).ConfigureAwait(false);
        foreach (long victim in victims)
        {
            if (await arm.GetByKeyAsync(conn, victim, ct).ConfigureAwait(false) is not null)
            {
                problems.Add($"BulkDelete 未删除键 {victim}");
            }
        }

        await CheckRowAsync($"BulkDelete 存活行 {survivor}", Dataset.Seed(survivor - 1)).ConfigureAwait(false);

        // ── 5. BulkUpdate：整段键值改写（值回读，沿用原有口径）──
        await ResetAsync().ConfigureAwait(false);
        List<S1Row> modified = AuditRows(probeRows);
        await arm.BulkUpdateAsync(conn, modified, ct).ConfigureAwait(false);
        await CheckCountAsync("BulkUpdate", CountAsync(), rows).ConfigureAwait(false);
        await CheckRowAsync("BulkUpdate", modified[0]).ConfigureAwait(false);
        await CheckRowAsync("BulkUpdate 末行", modified[^1]).ConfigureAwait(false);

        // ── 6. UpsertBatch：对已存在主键段整批 UPSERT（行数不变、值改写）──
        await ResetAsync().ConfigureAwait(false);
        await arm.UpsertBatchAsync(conn, modified, ct).ConfigureAwait(false);
        await CheckCountAsync("UpsertBatch", CountAsync(), rows).ConfigureAwait(false);
        await CheckRowAsync("UpsertBatch", modified[0]).ConfigureAwait(false);

        // ── 7. 事务族：单插 +1、百插 +100、批插 +probeRows、回滚不变 ──
        await ResetAsync().ConfigureAwait(false);
        await arm.TxSingleInsertAsync(conn, Dataset.Seed(rows), ct).ConfigureAwait(false);
        await CheckCountAsync("TxSingleInsert", CountAsync(), rows + 1).ConfigureAwait(false);

        await ResetAsync().ConfigureAwait(false);
        await arm.TxHundredInsertsAsync(conn, Dataset.SeedRows(100, 1_000_000), ct).ConfigureAwait(false);
        await CheckCountAsync("TxHundredInserts", CountAsync(), rows + 100).ConfigureAwait(false);

        await ResetAsync().ConfigureAwait(false);
        await arm.TxBulkInsertAsync(conn, Dataset.SeedRows(probeRows, 1_000_000), ct).ConfigureAwait(false);
        await CheckCountAsync("TxBulkInsert", CountAsync(), rows + probeRows).ConfigureAwait(false);

        await ResetAsync().ConfigureAwait(false);
        await arm.TxRollbackAsync(conn, Dataset.SeedRows(Math.Min(rows, 500), 0), ct).ConfigureAwait(false);
        await CheckCountAsync("TxRollback", CountAsync(), rows).ConfigureAwait(false);

        // ── 8. EnumInserts：枚举表恰好 100 行且 Status 字符串循环正确 ──
        await SetupEnumTableAsync(dialect, conn, arm).ConfigureAwait(false);
        await arm.EnumInsertsAsync(conn, Dataset.SeedEnumRows(100, 0), ct).ConfigureAwait(false);
        await CheckEnumRowsAsync(dialect, conn, arm, problems).ConfigureAwait(false);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"{arm.Name} 写效果校验失败：{string.Join("; ", problems)}");
        }

        Console.WriteLine(
            $"    {arm.Name,-9} 写效果校验 OK（11 个写操作：行数/键集/回滚/枚举列）");
    }

    /// <summary>审计行——前 probeRows 行的值改成可识别的审计值（BulkUpdate 与 UpsertBatch 共用）。</summary>
    private static List<S1Row> AuditRows(int probeRows)
    {
        List<S1Row> modified = Dataset.SeedRows(probeRows, 0);
        foreach (S1Row row in modified)
        {
            row.Name = "audit-" + row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            row.Qty = (int)(row.Id % 97);
            row.Price = row.Id * 0.75m;
            row.Marker = row.Id * 3;
        }

        return modified;
    }

    /// <summary>枚举列效果核对：行数与 Status 字符串多重集（枚举表无臂级读 API，直接读表——
    /// 独立小写表组走它自己的表名与列名）。</summary>
    private static async Task CheckEnumRowsAsync(
        Dialect dialect, DbConnection conn, IPerfImplementation arm, List<string> problems)
    {
        bool separateTable = (arm as ISeparateTableArm)?.EnumTableFor(dialect) is not null;
        string table = separateTable ? Dataset.EnumLowercaseTableName : Dataset.EnumTable(dialect);
        string statusColumn = separateTable ? "status" : Dataset.Q(dialect, "Status");

        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {statusColumn} FROM {table}";
        Dictionary<string, int> actual = [];
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(CancellationToken.None)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            string status = reader.GetString(0);
            actual[status] = actual.GetValueOrDefault(status) + 1;
        }

        Dictionary<string, int> expected = [];
        for (int i = 0; i < 100; i++)
        {
            string name = ((OrderStatus)(i % 6)).ToString();
            expected[name] = expected.GetValueOrDefault(name) + 1;
        }

        if (actual.Count != expected.Count
            || expected.Any(kv => actual.GetValueOrDefault(kv.Key) != kv.Value))
        {
            problems.Add(
                $"EnumInserts 枚举列不符：实得 [{string.Join(',', actual.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}]，"
                + $"期望 [{string.Join(',', expected.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}]");
        }
    }

    private static Dialect ParseDialect(string value) => value.ToLowerInvariant() switch
    {
        "sqlite" => Dialect.Sqlite,
        "mysql" => Dialect.MySql,
        "pg" or "postgres" or "postgresql" => Dialect.PostgreSql,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "dialect 只支持 sqlite/mysql/pg")
    };

    /// <summary>--arms 令牌 → 实现名（ado 令牌对应实现名 ADO_NET；复合名臂显式映射）。</summary>
    private static string ArmToken(string implementationName) => implementationName switch
    {
        "ADO_NET" => "ado",
        "DapperAot" => "dapper-aot",
        "EfCore" => "efcore",
        "Linq2Db" => "linq2db",
        _ => implementationName.ToLowerInvariant(),
    };

    private static List<Op> BuildOperations(
        Dialect dialect, int rows, long[] keys, long[] whereInIds, int bulkDeleteRounds)
    {
        // 计时前状态复位 = 按快照 Restore（各组同一份种子数据）；
        // 独立表组（SqlSugar+PG）由 RestoreForArmAsync 落到自己的小写表
        Func<DbConnection, IPerfImplementation, Task> Prep(string snapshot)
            => (c, arm) => RestoreForArmAsync(c, dialect, snapshot, arm);

        List<Op> ops =
        [
            // ── 读（pass 开始时表恰好 rows 行，三臂同状态）──
            new("GetByKey", "CRUD", 200,
                (impl, c, i) => impl.GetByKeyAsync(c, keys[i * 7919 % keys.Length], CancellationToken.None)),
            new("GetAllAsync", "CRUD", 200,
                (impl, c, _) => impl.GetAllAsync(c, CancellationToken.None)),
            new("QueryAll", "CRUD", 200,
                (impl, c, _) => impl.QueryAllAsync(c, CancellationToken.None)),

            // ── 写（PerfHub 顺序：Insert → Update → BulkUpdate → BulkInsert → BulkDelete）──
            new("Insert", "CRUD", 200,
                (impl, c, i) => impl.InsertAsync(c, Dataset.Seed(rows + i), CancellationToken.None),
                Prep("perf_s1_seed")),
            new("Update", "CRUD", 200,
                (impl, c, i) => impl.UpdateAsync(c, Dataset.Seed(i * 7919 % rows), CancellationToken.None)),
            new("BulkUpdate", "CRUD", 200,
                (impl, c, _) => impl.BulkUpdateAsync(c, Dataset.SeedRows(rows, 0), CancellationToken.None)),
            new("BulkInsert", "CRUD", 200,
                (impl, c, i) => impl.BulkInsertAsync(
                    c, Dataset.SeedRows(rows, (long)rows * (i + 1)), CancellationToken.None),
                Prep("perf_s1_seed")),
        new("BulkDelete", "CRUD", bulkDeleteRounds,
            (impl, c, i) =>
            {
                object[] batch = new object[rows];
                for (int k = 0; k < rows; k++)
                {
                    batch[k] = (long)((i * rows) + k + 1);
                }

                return impl.BulkDeleteAsync(c, batch, CancellationToken.None);
            },
            Prep("perf_s1_seed_bd")),

            // ── Query（BulkDelete 之后都带 reset，恢复「恰好 rows 行」）──
            new("KeysetPage", "Query", 200,
                (impl, c, i) => impl.KeysetPageAsync(c, i % 20 * 100, 100, CancellationToken.None),
                Prep("perf_s1_seed")),
            new("WhereIn", "Query", 200,
                (impl, c, _) => impl.WhereInAsync(c, whereInIds, CancellationToken.None),
                Prep("perf_s1_seed")),
            new("Count", "Query", 200,
                (impl, c, _) => impl.CountAsync(c, CancellationToken.None),
                Prep("perf_s1_seed")),

            // ── Upsert（对已存在主键段整批 UPSERT）──
            new("UpsertBatch", "CRUD", 200,
                (impl, c, _) => impl.UpsertBatchAsync(c, Dataset.SeedRows(rows, 0), CancellationToken.None),
                Prep("perf_s1_seed")),

            // ── 事务族（仅最小档，PerfHub RunsAtTier 语义）──
            new("TxSingleInsert", "Transaction", 200,
                (impl, c, i) => impl.TxSingleInsertAsync(c, Dataset.Seed(rows + i), CancellationToken.None),
                Prep("perf_s1_seed"),
                MinTierOnly: true),
            new("TxHundredInserts", "Transaction", 200,
                (impl, c, i) => impl.TxHundredInsertsAsync(
                    c, Dataset.SeedRows(100, rows + ((long)i * 100)), CancellationToken.None),
                Prep("perf_s1_seed"),
                MinTierOnly: true),
            new("TxBulkInsert", "Transaction", 200,
                (impl, c, i) => impl.TxBulkInsertAsync(
                    c, Dataset.SeedRows(rows, (long)rows * (i + 1)), CancellationToken.None),
                Prep("perf_s1_seed"),
                MinTierOnly: true),
            new("TxRollback", "Transaction", 200,
                (impl, c, i) => impl.TxRollbackAsync(
                    c, Dataset.SeedRows(Math.Min(rows, 500), 0), CancellationToken.None),
                Prep("perf_s1_seed"),
                MinTierOnly: true),
            new("EnumInserts", "Transaction", 200,
                (impl, c, i) => impl.EnumInsertsAsync(
                    c, Dataset.SeedEnumRows(100, (long)i * 100), CancellationToken.None),
                (c, arm) => SetupEnumTableAsync(dialect, c, arm),
                MinTierOnly: true),
        ];

        return ops;
    }

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

    /// <summary>建两张快照：perf_s1_seed（rows 行，常规 reset）与 perf_s1_seed_bd
    /// （rows×rounds 行，BulkDelete 专用）。快照带主键 DDL，恢复是 DELETE + INSERT..SELECT，
    /// 主键约束不丢（CREATE TABLE AS SELECT 会丢 PK，故显式建表再灌数）。</summary>
    private static async Task BuildSnapshotsAsync(DbConnection conn, Dialect dialect, int rows, int bulkDeleteRounds)
    {
        string ddlBase = Dataset.CreateTableSql(dialect);
        await ExecAsync(conn, $"DROP TABLE IF EXISTS {Q(dialect, "perf_s1_seed_bd")}").ConfigureAwait(false);
        await ExecAsync(conn, $"DROP TABLE IF EXISTS {Q(dialect, "perf_s1_seed")}").ConfigureAwait(false);
        await ExecAsync(conn, Dataset.DropTableSql(dialect)).ConfigureAwait(false);
        await ExecAsync(conn, ddlBase).ConfigureAwait(false);

        string seedDdl = ddlBase.Replace("perf_s1", "perf_s1_seed");
        string seedBdDdl = ddlBase.Replace("perf_s1", "perf_s1_seed_bd");
        await ExecAsync(conn, seedDdl).ConfigureAwait(false);
        await ExecAsync(conn, seedBdDdl).ConfigureAwait(false);

        await InsertRowsAsync(conn, dialect, "perf_s1_seed", Dataset.SeedRows(rows)).ConfigureAwait(false);
        await InsertRowsAsync(
            conn, dialect, "perf_s1_seed_bd", Dataset.SeedRows(rows * bulkDeleteRounds)).ConfigureAwait(false);

        // 2026-11-01 精简：独立小写表组改为从共享快照按列名映射复位，不再另建快照；
        // 早期版本留下的 lc 快照表（每档 10~22 万行）在此幂等清理。
        if (dialect == Dialect.PostgreSql)
        {
            foreach (string stale in new[]
                     {
                         Dataset.S1LowercaseTableName + "_seed",
                         Dataset.S1LowercaseTableName + "_seed_bd",
                     })
            {
                await ExecAsync(conn, $"DROP TABLE IF EXISTS {stale}").ConfigureAwait(false);
            }
        }

        await RestoreAsync(conn, dialect, "perf_s1_seed").ConfigureAwait(false);
    }

    /// <summary>恢复 = DROP + CREATE（带主键 DDL）+ INSERT..SELECT。不能用
    /// DELETE + INSERT..SELECT：pass 0 的批量写把表撑大后，DELETE 留下的死元组
    /// 会让 pass 1 的 PG 顺序扫描慢 40~56×（2026-10-24 实测三臂同慢），物理重建无此问题。</summary>
    private static Task RestoreAsync(DbConnection conn, Dialect dialect, string snapshot)
        => RestoreFromAsync(conn, dialect, snapshot, Dataset.Table(dialect));

    /// <summary>按组复位：独立表组（SqlSugar+PG）把共享快照按列名映射灌进自己的小写表，
    /// 不另建一套快照表（2026-11-01 精简：省 BuildSnapshots 每档 10~22 万行重复插入，
    /// 也少一套需与共享快照同步维护的数据面）。</summary>
    private static Task RestoreForArmAsync(
        DbConnection conn, Dialect dialect, string snapshot, IPerfImplementation arm)
        => arm is ISeparateTableArm separate && separate.S1TableFor(dialect) is { } table
            ? RestoreLowercaseAsync(conn, dialect, snapshot, table)
            : RestoreAsync(conn, dialect, snapshot);

    /// <summary>小写表复位：DDL 用不加引号的小写列，数据从共享快照按列名映射取。</summary>
    private static async Task RestoreLowercaseAsync(
        DbConnection conn, Dialect dialect, string snapshot, string target)
    {
        await ExecAsync(conn, $"DROP TABLE IF EXISTS {target}").ConfigureAwait(false);
        await ExecAsync(conn, Dataset.CreateS1LowercaseTableSql(dialect, target)).ConfigureAwait(false);
        await ExecAsync(
            conn,
            $"INSERT INTO {target} ({string.Join(", ", LowercaseColumns)}) "
            + $"SELECT {string.Join(", ", LowercaseColumns.Select(c => Q(dialect, char.ToUpperInvariant(c[0]) + c[1..])))} "
            + $"FROM {Q(dialect, snapshot)}").ConfigureAwait(false);
    }

    /// <summary>枚举表复位：独立表组建自己的小写枚举表。</summary>
    private static Task SetupEnumTableAsync(Dialect dialect, DbConnection conn, IPerfImplementation arm)
        => arm is ISeparateTableArm separate && separate.EnumTableFor(dialect) is { } table
            ? AdoNetImpl.SetupPerfEnumTableAsync(dialect, conn, CancellationToken.None, table)
            : AdoNetImpl.SetupPerfEnumTableAsync(dialect, conn, CancellationToken.None);

    private static async Task RestoreFromAsync(
        DbConnection conn, Dialect dialect, string snapshot, string target)
    {
        string ddl = Dataset.CreateTableSql(dialect).Replace("perf_s1", target, StringComparison.Ordinal);

        await ExecAsync(conn, $"DROP TABLE IF EXISTS {target}").ConfigureAwait(false);
        await ExecAsync(conn, ddl).ConfigureAwait(false);
        await ExecAsync(
            conn,
            $"INSERT INTO {target} SELECT * FROM {Q(dialect, snapshot)}").ConfigureAwait(false);
    }

    private static readonly string[] LowercaseColumns = ["id", "name", "qty", "price", "marker"];

    /// <summary>快照播种的每语句行数：5 列 × 200 = 1000 个参数，低于 SQLite 3.32+ 的
    /// 32766 上限，也远低于 MySQL/PG 的单语句参数上限。</summary>
    private const int SeedRowsPerStatement = 200;

    /// <summary>快照播种（计时区外）：按主键升序分块写多值 INSERT，整批同一事务。
    /// <para><b>为什么分块</b>：32 万行逐行 = 32 万次往返，MySQL 与 PG 各约 50 秒，两档合计
    /// 占每批约 2.4 分钟；分块后降到约 1600 次往返（2026-11-02 实测 0.24ms/行）。</para>
    /// <para><b>必须保持的两个不变量</b>：行内容（Dataset.Seed 公式）与行序（主键升序）。
    /// 快照的物理行序决定恢复时 INSERT..SELECT 的写入顺序，进而决定被测表 perf_s1 的页面
    /// 布局与读缓存局部性；改顺序就是改测量条件。事务边界同样保持整批一个事务。</para>
    /// <para>被测表本身不由本方法灌入：perf_s1 仍由 <see cref="RestoreFromAsync"/> 的
    /// DROP+CREATE+INSERT..SELECT 重建，恢复路径未改动。</para></summary>
    private static async Task InsertRowsAsync(
        DbConnection conn, Dialect dialect, string table, IReadOnlyList<S1Row> rows)
    {
        string columns = string.Join(
            ", ",
            new[] { "Id", "Name", "Qty", "Price", "Marker" }.Select(c => Dataset.Q(dialect, c)));
        await using DbTransaction tran = await conn.BeginTransactionAsync().ConfigureAwait(false);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        var sql = new System.Text.StringBuilder(SeedRowsPerStatement * 96);
        for (int start = 0; start < rows.Count; start += SeedRowsPerStatement)
        {
            int count = Math.Min(SeedRowsPerStatement, rows.Count - start);
            cmd.Parameters.Clear();
            sql.Clear();
            sql.Append("INSERT INTO ").Append(Q(dialect, table)).Append(" (").Append(columns).Append(") VALUES ");
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                string prefix = ParamPrefix(i);
                sql.Append('(')
                    .Append(prefix).Append("Id,")
                    .Append(prefix).Append("Name,")
                    .Append(prefix).Append("Qty,")
                    .Append(prefix).Append("Price,")
                    .Append(prefix).Append("Marker)");
            }

            cmd.CommandText = sql.ToString();
            for (int i = 0; i < count; i++)
            {
                S1Row row = rows[start + i];
                string prefix = ParamPrefix(i);
                AddParameter(cmd, prefix + "Id").Value = row.Id;
                AddParameter(cmd, prefix + "Name").Value = row.Name;
                AddParameter(cmd, prefix + "Qty").Value = row.Qty;
                AddParameter(cmd, prefix + "Price").Value = row.Price;
                AddParameter(cmd, prefix + "Marker").Value = row.Marker;
            }

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await tran.CommitAsync().ConfigureAwait(false);
    }

    private static string ParamPrefix(int index)
        => "@p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_";

    private static DbParameter AddParameter(DbCommand cmd, string name)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    private static string Q(Dialect dialect, string identifier) => Dataset.Q(dialect, identifier);

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed record Op(
        string Name,
        string Group,
        int MaxIterations,
        Func<IPerfImplementation, DbConnection, int, Task> Action,
        Func<DbConnection, IPerfImplementation, Task>? Prepare = null,
        bool MinTierOnly = false)
    {
        /// <summary>计时窗预算（秒）。慢操作表（2026-10-25 起）：事务族与三项批量
        /// （BulkInsert/BulkUpdate/UpsertBatch）的每操作中位在 8~104ms，1.5s 窗产出 14~190 次
        /// 迭代，其中位数的批间稳定性由 fsync/服务端主导（批内 err 普遍 &lt;2%），0.6s 窗
        /// （≥5 次采样，引擎下限 3 次兜底）不损失判别力。快操作不受预算影响（迭代数在
        /// maxIterations 钳制处收敛，预算从不生效）。
        /// <para><b>2026-11-02 修正死分支</b>：原写法是 <c>Group switch { "Bulk" =&gt; 0.6 }</c>，
        /// 但三项批量的 Group 自建文件起一直是 CRUD，该分支永不命中，0.6s 从未生效
        /// （实测 20000 档隐含计时窗中位 1.46s）。改为按 Name 判定后回到文档与 A/A 验证的 0.6s。
        /// 同轮把 QueryAll/GetAllAsync/BulkDelete 从 1.5s 收到 0.75s：三者中位 9~35ms，
        /// 1.5s 窗产出 40~160 次迭代，0.75s 仍有 20~80 次。</para></summary>
        public double Budget => Group switch
        {
            "Transaction" => 0.6,
            _ when Name is "BulkInsert" or "BulkUpdate" or "UpsertBatch" => 0.6,
            _ when Name is "QueryAll" or "GetAllAsync" or "BulkDelete" => 0.75,
            _ => 1.5,
        };

        /// <summary>慢操作族的预热缩放：全局 0.375s × 0.8 = 0.3s，即 2026-10-25 A/A 已验证的时长。
        /// 不跟着全局减半一起缩，避免在未验证的点上继续压低。首档 JIT 下限 0.5s 不受本系数影响。</summary>
        public double WarmupScale => Budget <= 0.6 ? 0.8 : 1.0;
    }
}
