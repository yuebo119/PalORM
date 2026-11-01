using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PalORM.PerfHub;

/// <summary>捕获到的一条 SQL 语句——原文 + 捕获通道（便于报告标注来源）。</summary>
internal sealed record CapturedSql(string Sql, string Channel);

/// <summary>SQL 文本审计的进程级捕获缓冲。
/// <para><b>为什么是进程级</b>：九组的钩子安装点不同（构造期 AOP、操作后读 LastQuery、连接包装），
/// 但都往同一个缓冲写；审计模式串行执行，一次只测一个 (方言 × 操作 × 组)，缓冲在每次
/// 操作前清空。</para>
/// <para><b>计时路径零影响</b>：<see cref="Enabled"/> 由 <c>--sql-audit</c> 置位，默认 false；
/// 各臂的钩子安装代码在构造时判断该位，计时模式根本不装钩子（AOP 订阅 / 日志过滤 / LastQuery 读取都不发生）。</para></summary>
internal static class SqlAuditCapture
{
    private static readonly object Gate = new();
    private static readonly List<CapturedSql> Buffer = [];

    /// <summary>是否启用捕获——必须在臂构造之前设置（臂在构造时决定装不装钩子）。</summary>
    internal static bool Enabled { get; set; }

    /// <summary>记录一条语句；未启用或空白文本直接丢弃。</summary>
    internal static void Add(string? sql, string channel)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        lock (Gate)
        {
            Buffer.Add(new CapturedSql(sql, channel));
        }
    }

    /// <summary>当前操作开始：清空缓冲。</summary>
    internal static void BeginOp()
    {
        lock (Gate)
        {
            Buffer.Clear();
        }
    }

    /// <summary>当前操作结束：取走缓冲内容。</summary>
    internal static List<CapturedSql> EndOp()
    {
        lock (Gate)
        {
            List<CapturedSql> copy = [.. Buffer];
            Buffer.Clear();
            return copy;
        }
    }

    /// <summary>当前操作已捕获的条数（供只暴露「最后一条语句」的库判断是否需要补交）。</summary>
    internal static int Count
    {
        get
        {
            lock (Gate)
            {
                return Buffer.Count;
            }
        }
    }
}

/// <summary>臂侧捕获接口——各库的钩子生命周期不同，审计驱动在固定点位调用。
/// <para>默认实现为空：只有需要补做动作的库才覆写（C# 默认接口方法，实施方不必被迫写空方法）。</para></summary>
internal interface ISqlAuditArm
{
    /// <summary>每次操作开始前调用——用于重装「会被库内部重建」的钩子。
    /// <para><b>为什么需要</b>：SqlSugar 的 <c>Aop</c> 挂在当前 provider 实例上，长批次里
    /// provider 被内部重建后旧钩子失效（2026-11-02 全量审计实测：PostgreSQL 段从 BulkDelete 起
    /// 连续 0 条，同操作单独跑则正常）。</para></summary>
    void OnSqlAuditBegin()
    {
    }

    /// <summary>操作结束后调用一次——只暴露「最后一条语句」的库在此补交文本（linq2db 的 LastQuery）。
    /// <paramref name="opFailed"/> 为真表示本操作抛了异常，此时库的「最后一条语句」可能是上一条操作的残留。</summary>
    void OnSqlAuditFlush(bool opFailed)
    {
    }
}

/// <summary>审计用连接包装层——把 <see cref="DbCommand.CommandText"/> 的赋值记进捕获缓冲。
/// <para><b>适用面</b>：ADO.NET / Dapper / Dapper.AOT / PalORM 在只依赖 <see cref="DbConnection"/>
/// 抽象的路径上语义不变。不适用面（均为按 provider 具体类型强转的批量协议，包装层在那里抛异常）：
/// RepoDB 的 helper 解析按连接类型查表（MissingMappingException）、ADO 的 MySQL/PG 批量与 PalORM 的 PG
/// 批量（NpgsqlConnection / MySqlConnection）、Dapper.AOT 的事务内 `Command&lt;T&gt;` 构造。
/// 这些路径在报告第二节逐条登记为「通道限制」。PalORM 的 MySQL 批量在服务端 `local_infile` 关闭时
/// 回退多值 INSERT，可正常捕获。</para>
/// <para><b>Dispose 语义</b>：包装层不释放内部连接（连接归 Runner 所有，审计期间跨操作复用）；
/// 包装命令则必须释放内部命令，否则逐行执行的组（Dapper/EF 家族 2000 次/操作）会漏掉命令释放。</para></summary>
internal sealed class SqlAuditConnection(DbConnection inner, string source) : DbConnection
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => inner.ConnectionString;
        set => inner.ConnectionString = value;
    }

    public override string Database => inner.Database;

    public override string DataSource => inner.DataSource;

    public override string ServerVersion => inner.ServerVersion;

    public override ConnectionState State => inner.State;

    public override void Open() => inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);

    public override void Close() => inner.Close();

    public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() => new SqlAuditCommand(inner.CreateCommand(), source, this, inner);

    protected override void Dispose(bool disposing)
    {
        // 刻意不释放 inner：连接由 Runner 统一管理（见类注释的 Dispose 语义）。
        base.Dispose(disposing);
    }
}

/// <summary>审计用命令包装层——<see cref="CommandText"/> 赋值即记录，其余全部转发。
/// <para><b>连接回映</b>：Dapper.AOT 生成代码会显式 <c>cmd.Connection = cnn</c>（cnn 是包装层），
/// 而驱动（如 Microsoft.Data.Sqlite）执行时会把它强转回具体类型——所以 setter 要把包装层
/// 映射回内部连接，否则 <c>InvalidCastException</c>。事务同理：事务是在内部连接上开的。</para></summary>
internal sealed class SqlAuditCommand(DbCommand inner, string source, DbConnection outer, DbConnection innerConnection) : DbCommand
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => inner.CommandText;
        set
        {
            SqlAuditCapture.Add(value, source);
            inner.CommandText = value;
        }
    }

    public override int CommandTimeout
    {
        get => inner.CommandTimeout;
        set => inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => inner.CommandType;
        set => inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => inner.DesignTimeVisible;
        set => inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => inner.UpdatedRowSource;
        set => inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => inner.Connection;
        set => inner.Connection = ReferenceEquals(value, outer) ? innerConnection : value;
    }

    protected override DbParameterCollection DbParameterCollection => inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => inner.Transaction;
        set => inner.Transaction = value;
    }

    public override void Cancel() => inner.Cancel();

    public override int ExecuteNonQuery() => inner.ExecuteNonQuery();

    public override object? ExecuteScalar() => inner.ExecuteScalar();

    public override void Prepare() => inner.Prepare();

    protected override DbParameter CreateDbParameter() => inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => inner.ExecuteReader(behavior);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        => inner.ExecuteReaderAsync(behavior, cancellationToken);

    /// <summary>释放内部命令——调用方（Dapper/Dapper.AOT/PalORM）按常规释放包装命令，
    /// 不转发会让内部命令与其参数滞留到连接关闭。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>一次 (方言 × 操作 × 组) 的审计记录。
/// <para><see cref="Statements"/> 是**去重后**的语句清单（按原文去重，单条超长截断）。
/// <see cref="CaptureCount"/> 是**捕获次数**（命令文本赋值 / 库回调的次数），不是执行次数：
/// 命令复用路径（ADO / PalORM 的复用槽）一次捕获对应多次执行，逐行新建命令的组两者相同。</para></summary>
internal sealed record SqlAuditEntry(
    string Dialect,
    string DisplayName,
    int Rows,
    string Op,
    string Arm,
    IReadOnlyList<CapturedSql> Statements,
    int CaptureCount,
    string? Error);

/// <summary>SQL 文本审计（方案 B）——驱动 + 归一化 + 报告。
/// <para><b>审计判据</b>：测试对象是 ORM 的 SQL 生成能力。逐组取出「实际执行的 SQL 文本」，
/// 同方言内做归一化比较：文本完全相同的组，或共用同一 SQL 真源（手写模板/同一驱动路径），
/// 或各自生成了同形 SQL——两者都要求连别名与参数命名一致，故报告把同文本簇并列展示供判读。</para>
/// <para><b>不参与计时</b>：每条操作只执行一次，走独立分支（<c>--sql-audit</c>），与
/// PerfHub/CrossBench 的预热-计时-采样协议无关；审计批不写 <c>crossbench-*.json</c>。</para></summary>
internal static partial class Program
{
    /// <summary>审计使用最小档（不参与计时，档位只影响批量语句规模）。</summary>
    private static async Task<int> RunSqlAuditAsync(
        IReadOnlyList<Dialect> dialects, string[] armFilter, string[] opFilter, int rows)
    {
        Console.WriteLine(
            $"SQL 审计：方言={string.Join(',', dialects.Select(d => DialectInfo.Of(d).DisplayName))} "
            + $"行数={rows:N0} 臂={(armFilter.Length == 0 ? "全部" : string.Join(',', armFilter))}");

        List<SqlAuditEntry> entries = [];
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        foreach (Dialect dialect in dialects)
        {
            DialectInfo info = DialectInfo.Of(dialect);
            string cs = Connections.Resolve(info);
            await using DbConnection conn = info.OpenConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);
            if (dialect == Dialect.Sqlite)
            {
                await ExecAsync(conn, Pragma).ConfigureAwait(false);
            }

            int bulkDeleteRounds = Math.Clamp(BulkDeleteSeedRowBudget / rows, 3, BulkDeleteMaxRounds);
            await BuildSnapshotsAsync(conn, dialect, rows, bulkDeleteRounds).ConfigureAwait(false);

            long[] keys = Dataset.KeySet(rows);
            long[] whereInIds = BuildWhereInIds(rows);

            List<IPerfImplementation> arms = BuildArms(info, armFilter);
            try
            {
                Console.WriteLine($"── SQL 审计 {info.DisplayName} rows={rows:N0} ──");
                foreach (Op op in BuildOperations(dialect, rows, keys, whereInIds, bulkDeleteRounds))
                {
                    if (opFilter.Length > 0 && !opFilter.Contains(op.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (IPerfImplementation arm in arms)
                    {
                        // 每条 (操作 × 组) 前复位到种子态：审计只取一条语句集合，不依赖上一组的残留状态。
                        await RestoreForArmAsync(conn, dialect, "perf_s1_seed", arm).ConfigureAwait(false);
                        if (op.Prepare is not null)
                        {
                            await op.Prepare(conn, arm).ConfigureAwait(false);
                        }

                        DbConnection actionConn = UsesHostConnection(arm)
                            ? new SqlAuditConnection(conn, arm.Name)
                            : conn;

                        SqlAuditCapture.BeginOp();
                        (arm as ISqlAuditArm)?.OnSqlAuditBegin();
                        string? error = null;
                        List<CapturedSql> captured;
                        try
                        {
                            await op.Action(arm, actionConn, 0).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // 只记「类型 + 消息」：完整堆栈里的 Dapper.AOT 生成类型名是 32 位十六进制哈希，
                            // 会命中 secret-guard 的 Twilio Account SID 规则（实测误报）；失败机制已在
                            // 报告第二节按通道归类说明，原始堆栈可由复跑复现。
                            error = ex.GetType().Name + ": " + ex.Message;
                        }
                        finally
                        {
                            (arm as ISqlAuditArm)?.OnSqlAuditFlush(error is not null);
                            captured = FilterHarnessNoise(SqlAuditCapture.EndOp());
                        }

                        entries.Add(new SqlAuditEntry(
                            dialect.ToString(), info.DisplayName, rows, op.Name, arm.Name,
                            DistinctStatements(captured), captured.Count, error));

                        int distinct = captured.Select(s => NormalizeSql(s.Sql)).Distinct(StringComparer.Ordinal).Count();
                        Console.WriteLine(
                            $"    {op.Name,-16} {arm.Name,-9} {captured.Count,3} 条/{distinct} 种"
                            + (error is null ? "" : $"  [捕获异常] {Trim(error, 90)}"));
                    }
                }
            }
            finally
            {
                foreach (IPerfImplementation impl in arms)
                {
                    (impl as IDisposable)?.Dispose();
                }
            }
        }

        Directory.CreateDirectory(ResultsDir());
        string jsonPath = Path.Combine(ResultsDir(), $"sql-audit-{stamp}.json");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        // 报告与原始语句同目录（results/ 入库，与 crossbench-12arm-*.md 同惯例）；
        // bench/reports/ 是 perf 报告的本地产物目录（.gitignore），审计报告要进仓库。
        string reportPath = Path.Combine(ResultsDir(), $"sql-audit-{stamp}.md");
        await File.WriteAllTextAsync(reportPath, BuildSqlAuditReport(entries, rows)).ConfigureAwait(false);

        Console.WriteLine($"WROTE {jsonPath}");
        Console.WriteLine($"WROTE {reportPath}");
        return 0;
    }

    /// <summary>审计批的臂构造——与计时批同源（<see cref="BuildArms"/>），差异只在本方法返回的
    /// 臂子集已按 <c>--arms</c> 过滤。钩子由各臂在构造时按 <see cref="SqlAuditCapture.Enabled"/> 安装。</summary>
    private static List<IPerfImplementation> BuildArms(DialectInfo info, string[] armFilter)
    {
        bool Want(string token) =>
            armFilter.Length == 0 || armFilter.Contains(token, StringComparer.OrdinalIgnoreCase);

        List<IPerfImplementation> all =
        [
            .. When(Want("ado"), new AdoNetImpl(info)),
            .. When(Want("dapper"), new DapperImpl(info)),
            .. When(Want("repodb"), new RepoDbArm(info)),
            .. When(Want("freesql"), new FreeSqlImpl(info)),
            .. When(Want("palorm"), new PalormImpl(info)),
            .. When(Want("dapper-aot"), new DapperAotImpl(info)),
            .. When(Want("linq2db"), new Linq2DbImpl(info)),
            .. When(Want("efcore"), new EfCoreImpl(info)),
            .. When(Want("sqlsugar"), TryArm(() => new SqlSugarImpl(info), "sqlsugar")!),
        ];
        return armFilter.Length == 0
            ? all
            : [.. all.Where(a => armFilter.Contains(ArmToken(a.Name), StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>落盘用语句清单：按原文去重 + 超长截断。
    /// <para><b>为什么</b>：审计批里逐行执行的组（Dapper/EF 的 2000 次/操作）原文大量重复，
    /// 全量落盘 459 条记录会到 14 MB（实测），去重后约 2500 条不同文本；单条上限 8000 字符覆盖
    /// 多值 INSERT / 大 IN 列表的头部，超出部分以「…（截断）」标记，报告与 JSON 均可判读。</para></summary>
    private static List<CapturedSql> DistinctStatements(List<CapturedSql> statements)
    {
        const int maxChars = 8000;
        List<CapturedSql> distinct = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CapturedSql statement in statements)
        {
            string text = statement.Sql;
            if (text.Length > maxChars)
            {
                text = text[..maxChars] + " …（截断）";
            }

            if (seen.Add(text))
            {
                distinct.Add(statement with { Sql = text });
            }
        }

        return distinct;
    }

    /// <summary>该组是否只通过传入的宿主连接执行（可用连接包装层捕获）。
    /// <para>RepoDB 不在列：它的 DbHelper/DbSetting 按连接的具体 provider 类型解析，包装层会被判为
    /// 「无映射」（MissingMappingException）；全局 <c>ITrace</c> 又只对同步路径成立——1.17.0 的
    /// <c>Tracer.InvokeBeforeExecutionAsync</c> 在「无 per-call trace + 全局 trace 开启」时对 null Task
    /// 执行 await，异步路径必抛 NRE。故本批 RepoDB 只登记「通道不可用」，见报告第二节。</para></summary>
    private static bool UsesHostConnection(IPerfImplementation arm)
        => arm.Name is "ADO_NET" or "Dapper" or "DapperAot" or "PalORM";

    /// <summary>组的捕获通道限制说明（无通道的组在第二节逐条注明，避免「0 条」被误读为操作失败）。</summary>
    private static string? CaptureLimitation(string arm, string op) => (arm, op) switch
    {
        ("RepoDb", _) => "RepoDB 无可用捕获通道：连接包装层被 provider 类型解析拒绝（MissingMappingException），"
            + "全局 ITrace 对异步 API 在 1.17.0 上抛 NRE；该组 SQL 形态以源码审计为准"
            + "（QueryFirstAsync/InsertAllAsync/MergeAllAsync 等 API 调用）",
        ("FreeSql", "BulkInsert") =>
            "FreeSql 的批量插入走 provider 私有通道（SQLite 的 ExecuteSqliteBulkInsert / MySQL 的 "
            + "ExecuteMySqlBulkCopy / PG 的 ExecutePgCopy），AOP 不触发，无语句文本",
        _ => null,
    };

    /// <summary>把「未捕获 / 异常」归并成一句原因——同组同类限制合并成一行，避免报告出现几十行同文。</summary>
    private static string DescribeProblem(SqlAuditEntry entry)
    {
        if (entry.Error is null)
        {
            return CaptureLimitation(entry.Arm, entry.Op)
                ?? $"{entry.Arm} 在该操作上未产出语句（通道无输出）";
        }

        if (entry.Error.Contains("InvalidCastException", StringComparison.Ordinal)
            || entry.Error.Contains("requires an NpgsqlConnection", StringComparison.Ordinal)
            || entry.Error.Contains("requires a MySqlConnection", StringComparison.Ordinal))
        {
            return "驱动级批量协议（MySqlBulkCopy / NpgsqlBinaryImporter）：该路径按 provider 具体类型强转，"
                + "包装后不可执行、也无 ADO 层语句文本——属捕获通道限制，非该组操作失败";
        }

        if (entry.Error.Contains("not associated with the specified connection", StringComparison.Ordinal))
        {
            return "Dapper.AOT 的 Command<T> 构造期校验 `transaction.Connection == connection`，包装层必然不等——"
                + "属捕获通道限制（该三条操作的 SQL 与对应的非事务操作同源）";
        }

        return "异常：" + Trim(entry.Error, 200);
    }

    /// <summary>剔除非操作 SQL：连接初始化 PRAGMA 与 MySQL local_infile 能力探测。
    /// 两者都属会话/臂初始化，不属被审计操作的 SQL 生成结果。</summary>
    private static List<CapturedSql> FilterHarnessNoise(List<CapturedSql> statements)
    {
        List<CapturedSql> filtered = [];
        foreach (CapturedSql statement in statements)
        {
            string text = statement.Sql.TrimStart();
            if (text.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("SELECT @@local_infile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered.Add(statement);
        }

        return filtered;
    }

    /// <summary>归一化——只用于「两组是否共用同一 SQL 文本」的比较；报告语句列展示归一化结果，原始语句在 JSON。
    /// <list type="bullet">
    /// <item>标识符引号剥离（<c>"x"</c> / <c>`x`</c> / <c>[x]</c> → <c>x</c>）——三方言转义差异是必然噪声；</item>
    /// <item>字符串与数字字面量 → <c>?</c>（FreeSql 臂关命令参数，字面量内联）；</item>
    /// <item>参数占位 <c>@x</c> / <c>$1</c> / <c>:x</c> → <c>?</c>（EF 的 <c>@__ids_0</c> 与 Dapper 的 <c>@ids</c> 是同一语义）；</item>
    /// <item>空白折叠、去注释、去尾分号。</item>
    /// </list>
    /// <para>别名不剥离：EF 的 <c>AS "s"</c>、linq2db 的 <c>[x]</c> 恰是各组生成特征，剥离会掩盖差异。</para></summary>
    internal static string NormalizeSql(string sql)
    {
        StringBuilder sb = new(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    _ = sb.Append(' ');
                }

                i++;
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, sql.Length);
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                AppendToken(sb, "?");
                continue;
            }

            if (c is '"' or '`' or '[')
            {
                char close = c == '[' ? ']' : c;
                i++;
                StringBuilder identifier = new();
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        if (c != '[' && i + 1 < sql.Length && sql[i + 1] == close)
                        {
                            _ = identifier.Append(close);
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    _ = identifier.Append(sql[i]);
                    i++;
                }

                AppendToken(sb, identifier.ToString());
                continue;
            }

            if (c == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                // PostgreSQL 类型转换 col::int——不是参数占位
                _ = sb.Append("::");
                i += 2;
                continue;
            }

            if (c is '@' or ':' or '$')
            {
                int start = i;
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    i++;
                }

                if (i > start + 1)
                {
                    AppendToken(sb, "?");
                    continue;
                }

                _ = sb.Append(c);
                continue;
            }

            // 标识符整体吞掉（字母/下划线开头，可含数字与 $）——否则 perf_s1 会被数字分支
            // 拆成 "perf_s ?"、INT8/INT4 归一成同一串，把语义不同的 SQL 判成同文本（审计首轮实测）。
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$'))
                {
                    i++;
                }

                // 字符串字面量的前缀（N'…' / E'…' / X'…' / B'…'）不单独成 token——交给引号分支
                // 按字面量归一为 ?，否则 "N ?" 会与其它组的 "?" 判成不同文本。
                string identifier = sql[start..i];
                if (identifier.Length == 1 && i < sql.Length && sql[i] == '\''
                    && identifier[0] is 'N' or 'n' or 'E' or 'e' or 'X' or 'x' or 'B' or 'b')
                {
                    continue;
                }

                AppendToken(sb, identifier);
                continue;
            }

            if (char.IsDigit(c))
            {
                while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.'))
                {
                    i++;
                }

                AppendToken(sb, "?");
                continue;
            }

            _ = sb.Append(c);
            i++;
        }

        // 折叠连续空格（token 追加会留下首尾空格），保持可读且不改变等价类。
        string flat = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return flat.Trim().TrimEnd(';').Trim();
    }

    /// <summary>追加一个 token，与前后文保一个空格（让 <c>t(?</c> 与 <c>t ( ?</c> 归一为同形）。</summary>
    private static void AppendToken(StringBuilder sb, string token)
    {
        if (sb.Length > 0 && sb[^1] != ' ')
        {
            _ = sb.Append(' ');
        }

        _ = sb.Append(token);
        _ = sb.Append(' ');
    }

    /// <summary>语句形态标签——只做机械分类，供汇总矩阵用（INSERT 的 UPSERT 变体单独标）。</summary>
    private static string SqlShape(string sql)
    {
        string normalized = NormalizeSql(sql);
        if (normalized.Length == 0)
        {
            return "?";
        }

        if (normalized.StartsWith("insert", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.StartsWith("insert or replace", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("insert or ignore", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("on conflict", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("on duplicate key", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("insert overwrite", StringComparison.OrdinalIgnoreCase))
            {
                return "INSERT+UPSERT";
            }

            return normalized.Contains(" select ", StringComparison.OrdinalIgnoreCase) ? "INSERT..SELECT" : "INSERT";
        }

        if (normalized.StartsWith("replace into", StringComparison.OrdinalIgnoreCase))
        {
            return "INSERT+UPSERT";
        }

        if (normalized.StartsWith("select", StringComparison.OrdinalIgnoreCase))
        {
            return "SELECT";
        }

        if (normalized.StartsWith("update", StringComparison.OrdinalIgnoreCase))
        {
            return "UPDATE";
        }

        if (normalized.StartsWith("delete", StringComparison.OrdinalIgnoreCase))
        {
            return "DELETE";
        }

        if (normalized.StartsWith("merge", StringComparison.OrdinalIgnoreCase))
        {
            return "MERGE";
        }

        if (normalized.StartsWith("copy", StringComparison.OrdinalIgnoreCase))
        {
            return "COPY";
        }

        if (normalized.StartsWith("load data", StringComparison.OrdinalIgnoreCase))
        {
            return "LOAD DATA";
        }

        int space = normalized.IndexOf(' ');
        string first = (space > 0 ? normalized[..space] : normalized).ToUpperInvariant();

        // 事务控制词是合法 SQL，但「Begin BulkCopy name:…」这类库自有日志也以 Begin 开头——
        // 只有整句就是事务语句时才按 SQL 记（实测样例见 SqlKeywords 注释）。
        if (first is "BEGIN" or "COMMIT" or "ROLLBACK" or "SAVEPOINT")
        {
            string rest = normalized[first.Length..].Trim().TrimStart(';').Trim();
            if (rest.Length > 0 && !rest.Equals("transaction", StringComparison.OrdinalIgnoreCase)
                && !rest.Equals("work", StringComparison.OrdinalIgnoreCase)
                && !rest.Equals("transaction work", StringComparison.OrdinalIgnoreCase))
            {
                return "非 SQL 日志";
            }
        }

        return SqlKeywords.Contains(first) ? first : "非 SQL 日志";
    }

    /// <summary>SQL 起始关键字白名单——不在表内的一律记为「非 SQL 日志」
    /// （实测样例：SqlSugar 的 BulkCopy 通道只写 <c>Begin BulkCopy name:…</c> 这类自有日志，无语句文本）。</summary>
    private static readonly HashSet<string> SqlKeywords = new(StringComparer.Ordinal)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "COPY", "LOAD", "WITH",
        "PRAGMA", "SET", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT",
        "CREATE", "DROP", "ALTER", "TRUNCATE", "REPLACE",
        "EXEC", "EXECUTE", "CALL", "DESCRIBE", "DESC", "SHOW", "EXPLAIN", "VALUES",
    };

    /// <summary>语句集合签名——去重后的归一化文本按字典序拼接；两组签名相同 = 执行了同一批 SQL。</summary>
    private static string SqlSignature(IReadOnlyList<CapturedSql> statements)
        => string.Join(
            " ‖ ",
            statements.Select(s => NormalizeSql(s.Sql)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static string Trim(string value, int max)
    {
        string flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>报告生成——三节：方法与边界 / 逐方言矩阵与变体 / 未捕获与异常 + 读法。</summary>
    private static string BuildSqlAuditReport(IReadOnlyList<SqlAuditEntry> entries, int rows)
    {
        StringBuilder sb = new(1 << 18);
        _ = sb.AppendLine($"# SQL 文本审计（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        _ = sb.AppendLine();
        _ = sb.AppendLine($"> 审计对象：CrossBench 九组（ADO.NET 地板 + 八个 ORM）× 方言 "
            + $"{string.Join(" / ", entries.Select(e => e.DisplayName).Distinct(StringComparer.Ordinal))} × "
            + $"{entries.Select(e => e.Op).Distinct(StringComparer.Ordinal).Count()} 操作，行数档 {rows:N0}。");
        _ = sb.AppendLine("> 判据：测试对象是 ORM 的 SQL 生成能力。取「实际执行过的 SQL 文本」为证据——"
            + "同方言内归一化后文本完全相同的组，要么共用同一 SQL 真源（手写模板或同一驱动路径），"
            + "要么各自生成了同形 SQL；跨组同文本表把两者并列，判读见第三节。");
        _ = sb.AppendLine("> 本批不参与计时：每条操作只执行一次，与预热/计时/采样协议无关。");
        _ = sb.AppendLine();

        _ = sb.AppendLine("## 一、方法与边界");
        _ = sb.AppendLine();
        _ = sb.AppendLine("| 组 | 捕获通道 | 覆盖 |");
        _ = sb.AppendLine("|---|---|---|");
        _ = sb.AppendLine("| ADO_NET | 宿主连接包装层（`DbCommand.CommandText` 赋值即记录） | 除 MySQL/PostgreSQL"
            + " 的批量插入外全部操作——该地板的批量走驱动级协议（`MySqlBulkCopy` / Npgsql binary COPY），"
            + "按 provider 具体类型强转，包装层不可用，见第二节 |");
        _ = sb.AppendLine("| Dapper | 同左 | 全部操作（批量走 `BulkSql` 多值 INSERT，不经驱动批量协议） |");
        _ = sb.AppendLine("| Dapper.AOT | 同左 | 非事务操作为主；事务内走 `Command<T>` 的三条操作"
            + "（TxSingleInsert / TxHundredInserts / EnumInserts）不可包装——Dapper.AOT 构造 `Command<T>` 时校验"
            + "`transaction.Connection == connection`，包装层必然不等（上游约束，见第二节） |");
        _ = sb.AppendLine("| PalORM | 同左 | 除 PostgreSQL 批量插入（要求具体 `NpgsqlConnection`）外全部操作；"
            + "MySQL 批量在服务端 `local_infile` 开启时走 `MySqlBulkCopy`，同样不可包装——"
            + "本批该开关为关闭，产品回退多值 INSERT，已捕获 |");
        _ = sb.AppendLine("| RepoDB | 未接入 | 该组无可用通道：连接包装层被 provider 类型解析拒绝（MissingMappingException）；"
            + "全局 `ITrace` 只覆盖同步路径——1.17.0 的 `Tracer.InvokeBeforeExecutionAsync` 在无 per-call trace 时"
            + "对 null Task 执行 await，异步 API 必抛 NRE（2026-11-02 单操作探针复现，脚本未随批入库）。SQL 形态以源码审计为准，见第二节 |");
        _ = sb.AppendLine("| FreeSql | `fsql.Aop.CommandAfter` | 除批量插入外全部操作（批量插入走 provider 私有通道，"
            + "AOP 不触发，见第二节） |");
        _ = sb.AppendLine("| SqlSugar | `db.Aop.OnLogExecuting`（每次操作前重装） | 全部操作；`Fastest`/BulkCopy 路径"
            + "（BulkInsert / BulkUpdate）只留库自有日志（矩阵标「非 SQL 日志」） |");
        _ = sb.AppendLine("| EF Core | `DbContextOptionsBuilder.LogTo`（`CommandExecuted` 事件） | 全部操作 |");
        _ = sb.AppendLine("| linq2db | `DataConnection.LastQuery`（操作后读取） | 全部操作；只保留最后一条语句"
            + "（分块批删/批改只呈现末块形态） |");
        _ = sb.AppendLine();
        _ = sb.AppendLine("归一化口径（只用于「是否同一文本」的比较；报告里的语句列展示归一化结果，原始语句见同名 JSON）："
            + "标识符引号剥离、字符串/数字字面量替换为 `?`、参数占位（`@x` / `$1` / `:x`）统一为 `?`、"
            + "标识符整体保留（`perf_s1` / `INT8` 不被数字规则拆开）、注释与空白折叠、去尾分号。"
            + "别名不剥离——各组的别名风格恰是生成特征。");
        _ = sb.AppendLine();
        _ = sb.AppendLine("剔除项：连接初始化 `PRAGMA`（SQLite 会话初始化）与 `SELECT @@local_infile`"
            + "（MySQL 批量能力探测），两者属臂/会话初始化而非操作 SQL。");
        _ = sb.AppendLine();
        _ = sb.AppendLine("落盘口径：JSON 的 `Statements` 按原文去重（同文只留一条）、单条超 8000 字符截断，"
            + "捕获次数另存 `CaptureCount`。报告里的「条数」是**捕获次数**（命令文本赋值 / 库回调的次数），"
            + "不是执行次数——命令复用路径（ADO / PalORM 的复用槽）一次捕获对应多次执行，"
            + "逐行新建命令的组（Dapper / EF）两者相同。");
        _ = sb.AppendLine();

        foreach (IGrouping<string, SqlAuditEntry> dialectGroup in
                 entries.GroupBy(e => e.Dialect, StringComparer.Ordinal))
        {
            List<SqlAuditEntry> dialectEntries = [.. dialectGroup];
            string displayName = dialectEntries[0].DisplayName;
            List<string> ops = [.. dialectEntries.Select(e => e.Op).Distinct(StringComparer.Ordinal)];
            List<string> arms = [.. dialectEntries.Select(e => e.Arm).Distinct(StringComparer.Ordinal)];

            _ = sb.AppendLine($"## {displayName} — 汇总矩阵（语句数 × 形态）");
            _ = sb.AppendLine();
            _ = sb.Append("| 操作 |");
            foreach (string arm in arms)
            {
                _ = sb.Append(' ').Append(arm).Append(" |");
            }

            _ = sb.AppendLine();
            _ = sb.Append("|---|");
            for (int i = 0; i < arms.Count; i++)
            {
                _ = sb.Append("---|");
            }

            _ = sb.AppendLine();
            foreach (string op in ops)
            {
                _ = sb.Append('|').Append(op).Append('|');
                foreach (string arm in arms)
                {
                    SqlAuditEntry? entry = dialectEntries.FirstOrDefault(
                        e => e.Op == op && e.Arm == arm);
                    _ = sb.Append(' ').Append(MatrixCell(entry)).Append(" |");
                }

                _ = sb.AppendLine();
            }

            _ = sb.AppendLine();

            // ── 跨组同文本：签名相同且组数 ≥2 ──
            List<(string Op, string Signature, List<string> Arms)> shared = [];
            foreach (string op in ops)
            {
                foreach (IGrouping<string, SqlAuditEntry> bucket in dialectEntries
                             .Where(e => e.Op == op && e.CaptureCount > 0)
                             .GroupBy(e => SqlSignature(e.Statements), StringComparer.Ordinal))
                {
                    List<string> armsInBucket = [.. bucket.Select(e => e.Arm).Distinct(StringComparer.Ordinal)];
                    if (armsInBucket.Count >= 2)
                    {
                        shared.Add((op, bucket.Key, armsInBucket));
                    }
                }
            }

            _ = sb.AppendLine($"### {displayName} — 跨组同文本（归一化后完全相同的语句集合，≥2 组）");
            _ = sb.AppendLine();
            if (shared.Count == 0)
            {
                _ = sb.AppendLine("无——各组 SQL 文本互不相同。");
                _ = sb.AppendLine();
            }
            else
            {
                _ = sb.AppendLine("| 操作 | 组 | 归一化语句（首 220 字） |");
                _ = sb.AppendLine("|---|---|---|");
                foreach ((string op, string signature, List<string> armsInBucket) in shared)
                {
                    _ = sb.Append('|').Append(op).Append('|')
                        .Append(string.Join('、', armsInBucket)).Append('|')
                        .Append(Cell(Trim(signature, 220))).AppendLine(" |");
                }

                _ = sb.AppendLine();
            }

            // ── 逐操作变体 ──
            _ = sb.AppendLine($"### {displayName} — 逐操作文本变体");
            _ = sb.AppendLine();
            foreach (string op in ops)
            {
                _ = sb.AppendLine($"#### {op}");
                _ = sb.AppendLine();
                List<SqlAuditEntry> opEntries = [.. dialectEntries.Where(e => e.Op == op)];
                List<(string Signature, List<SqlAuditEntry> Entries)> variants = [];
                foreach (IGrouping<string, SqlAuditEntry> bucket in opEntries.GroupBy(
                             // 未捕获的条目按「组 + 原因」分桶：不同组的失败原因不同（驱动批量 / 无通道 / AOP 不触发），
                             // 合并成一桶会让所有组共用第一条的错误文本。
                             e => e.CaptureCount == 0
                                 ? "\u0000" + e.Arm + "：" + DescribeProblem(e)
                                 : SqlSignature(e.Statements),
                             StringComparer.Ordinal))
                {
                    variants.Add((bucket.Key, [.. bucket]));
                }

                _ = sb.AppendLine("| 组（捕获次数） | 归一化 SQL |");
                _ = sb.AppendLine("|---|---|");
                foreach ((string signature, List<SqlAuditEntry> bucket) in variants)
                {
                    // 捕获次数逐组标注：同一变体下不同组的次数可能相差三个量级
                    //（ADO 复用单命令 1 次捕获 vs Dapper 逐行 2000 次），合并写一个数字会误导读表。
                    string armsText = string.Join(
                        '、', bucket.Select(e => $"{e.Arm}({e.CaptureCount.ToString(CultureInfo.InvariantCulture)})"));
                    string sqlText = signature.StartsWith('\u0000')
                        ? "（未捕获：" + Trim(signature[(signature.IndexOf('：', StringComparison.Ordinal) + 1)..], 240) + "）"
                        : Trim(signature, 420);
                    _ = sb.Append('|').Append(armsText).Append('|')
                        .Append(Cell(sqlText)).AppendLine(" |");
                }

                _ = sb.AppendLine();
            }
        }

        _ = sb.AppendLine("## 二、未捕获与异常");
        _ = sb.AppendLine();
        List<SqlAuditEntry> problems = [.. entries.Where(e => e.CaptureCount == 0 || e.Error is not null)];
        if (problems.Count == 0)
        {
            _ = sb.AppendLine("无——每组每个操作都取到了 SQL 文本。");
        }
        else
        {
            _ = sb.AppendLine("按「方言 × 组 × 原因」聚合（同一组的同类限制合并成一行，逐操作的原始报错见 JSON）：");
            _ = sb.AppendLine();
            _ = sb.AppendLine("| 方言 | 组 | 操作 | 说明 |");
            _ = sb.AppendLine("|---|---|---|---|");
            foreach (IGrouping<string, SqlAuditEntry> dialectProblems in problems.GroupBy(e => e.DisplayName, StringComparer.Ordinal))
            {
                foreach (IGrouping<string, SqlAuditEntry> armProblems in dialectProblems.GroupBy(e => e.Arm, StringComparer.Ordinal))
                {
                    foreach (IGrouping<string, SqlAuditEntry> reasonGroup in armProblems.GroupBy(DescribeProblem, StringComparer.Ordinal))
                    {
                        string opsText = string.Join('、', reasonGroup.Select(e => e.Op).Distinct(StringComparer.Ordinal));
                        _ = sb.Append('|').Append(dialectProblems.Key).Append('|')
                            .Append(armProblems.Key).Append('|')
                            .Append(opsText).Append('|')
                            .Append(Cell(reasonGroup.Key)).AppendLine(" |");
                    }
                }
            }
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("## 三、读法");
        _ = sb.AppendLine();
        _ = sb.AppendLine("- **手写三组（ADO_NET / Dapper / Dapper.AOT）同文本是设计如此**：单语句操作共用同一份字面 SQL，"
            + "批量操作共用 `BulkSql` 模板。它们等于同一个「手写 SQL」样本的三次测量，横评里不构成三个独立的"
            + " SQL 生成能力样本——报告说明列应按此注明（既知口径偏差）。");
        _ = sb.AppendLine("- **ORM 组与地板同文本**（如 PalORM 的主键直查/更新与 ADO 同簇）说明该组生成的 SQL 与手写最优形态同形，"
            + "属正面证据；判据是「它的别名风格与参数命名是否也与手写组一致」——若一致到连别名都相同，才说明疑似抄模板。");
        _ = sb.AppendLine("- **单组独有文本 + 形态符合该库惯用写法**（EF 的 `INSERT … VALUES … RETURNING`、linq2db 的 `MERGE`、"
            + "FreeSql 的临时表 + UPDATE）是该组确实在用自身 API 生成 SQL 的直接证据。");
        _ = sb.AppendLine("- **「非 SQL 日志」格**（如 SqlSugar 的 `Begin BulkCopy name:…`）表示该路径不经 SQL 文本通道"
            + "（驱动级批量协议），库只留下自有日志——这本身是该路径不走 SQL 生成的证据。");
        _ = sb.AppendLine("- **EF Core 的 `UpsertBatch` 只出现 SELECT**：本夹具的种子行值未变，EF 的「查出已存在行 → 改属性 → "
            + "SaveChanges」路径判定无改动、不发写语句。该格不代表 EF 的写路径成本，横评时需注明——"
            + "审计只呈现「实际执行了什么」，不为各组补 SQL。");
        _ = sb.AppendLine("- 各组的捕获通道（连接包装层 / AOP / 日志事件 / LastQuery）都只旁路读取，不回写 SQL："
            + "捕获到的文本即被测代码实际交给驱动的内容。");
        return sb.ToString();
    }

    /// <summary>矩阵单元格：语句数 × 形态（多形态时取首个并标 <c>+n</c>）。</summary>
    private static string MatrixCell(SqlAuditEntry? entry)
    {
        if (entry is null)
        {
            return "—";
        }

        if (entry.CaptureCount == 0)
        {
            return entry.Error is null ? "0" : "✗";
        }

        List<string> shapes = [.. entry.Statements.Select(s => SqlShape(s.Sql)).Distinct(StringComparer.Ordinal)];
        string shape = string.Join('+', shapes);
        return entry.CaptureCount.ToString(CultureInfo.InvariantCulture) + "×" + shape;
    }
}
