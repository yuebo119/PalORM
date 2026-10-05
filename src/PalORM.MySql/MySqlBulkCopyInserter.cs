using System.Data.Common;
using MySqlConnector;

namespace PalORM.MySql;

/// <summary>v5.0 阶段 4.2：MySQL 批量插入——MySqlBulkCopy（LOAD DATA LOCAL INFILE 协议）。
/// <para><b>设计</b>：MySqlBulkCopy 走 LOAD DATA INFILE / 二进制行协议，批量插入比多值 INSERT
/// 快约 4.84 倍（roadmap 基准）。</para>
/// <para><b>调用判据</b>：由 MySqlProvider.BulkInsertAsync 检测 local_infile=ON 后调用（无阈值）。
/// local_infile=OFF 时走多值 INSERT 路径。</para>
/// <para><b>取值路径（v5.6 起为 <see cref="EntityDataReader"/>）</b>：用生成器发射的
/// <c>BindInsertValues</c> 把每行实体写进<b>复用</b>的参数对象池，读取器按序号直接读池值。
/// <b>原实现在此填 DataTable</b>，并注明"MySqlBulkCopy 对 DataTable 路径有专门优化"——该判断
/// 经实测证伪：同一批 10K 行 × 4 列，DataTable 每行 372.0/360.0 B、114.2/112.7 ms，
/// 读取器每行 57.0 B、100.9/100.4 ms（分配 −84%、时间 −11%，两次采样）。</para>
/// <para><b>事务语义</b>：调用方传入的 transaction 一并使用；未传时内部开新事务包整批。
/// BulkCopy 失败整批回滚。</para></summary>
internal static class MySqlBulkCopyInserter
{
    /// <summary>列映射缓存（PROV-002，2026-09-23）：键 = (目标表, 列集)，值 = 不可变映射数组。
    /// 键空间 = 实体 × 列集（有限，与 Provider 侧既有静态缓存同纪律）；映射对象跨批共享。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string Table, string Columns), MySqlBulkCopyColumnMapping[]> ColumnMappingCache = new();

    private static MySqlBulkCopyColumnMapping[] BuildColumnMappings(string[] columns)
    {
        var mappings = new MySqlBulkCopyColumnMapping[columns.Length];
        for (int i = 0; i < columns.Length; i++)
            mappings[i] = new MySqlBulkCopyColumnMapping(i, columns[i]);
        return mappings;
    }

    /// <summary>执行批量插入。</summary>
    public static async Task<long> ExecuteAsync<T>(
        MySqlConnection conn,
        MySqlTransaction? transaction,
        IReadOnlyList<T> entities,
        MySqlBulkCopyContext ctx,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);

        // ITM-710(r20)：按 batchSize 分批物化 + 提交——此前把整表一次性灌进 DataTable 单条
        // LOAD DATA（batchSize 未进入本方法签名，契约静默失效），百万行场景内存峰值可达 OOM。
        // 与 MultiValueBulkInsert 的分批语义对齐（每批一次 WriteToServer）。
        // ITM-779(r21)：与回退路径（MultiValueBulkInsert.ThrowIfNegativeOrZero）契约一致——
        // 原 Math.Max(1,...) 静默钳制非正值，同参数两路径两种语义。
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ctx.BatchSize);
        int batchSize = ctx.BatchSize;
        // B1/L4：列布局只由 ctx 决定，原实现每批重算一次 LINQ + 两个集合分配
        // （千批 = 千次无谓分配，且 Contains 是 O(pk×cols) 线性扫描）。此处算一次，逐批复用。
        ColumnLayout layout = ColumnLayout.Build(ctx);
        // B16（2026-10-01 全 API 逐项轮）：列映射解析从"每批"提升到"每次调用"——
        // 原实现每批 1 个 string.Join 键串 + GetOrAdd 查找（千批 = 千次键串 + 千次哈希）；
        // 映射数组跨批/跨 MySqlBulkCopy 实例共享安全（不可变值对象，PROV-002）。
        MySqlBulkCopyColumnMapping[] mappings = ColumnMappingCache.GetOrAdd(
            (ctx.QuotedTable, string.Join('\u0001', layout.AllColumns)),
            static (_, columns) => BuildColumnMappings(columns),
            layout.AllColumns);
        // B15（2026-10-01 全 API 逐项轮）：参数池/行命令/probe 从"每批"提升到"每次调用"——
        // 原实现每批 new DbParameter[columnCount] + CreateCommand + 首行 probe binder
        // （千批 = 千次池数组 + 千个命令对象 + 每批 columnCount 个参数新建与装箱）。
        // 池与 bindRow 跨批复用（ValuesBinder 只写 Value；fallback 每行 Clear 重绑，
        // 与 MultiValueBulkInsert 的跨批复用范式对齐）；命令随本方法释放（await using）。
        if (entities.Count == 0) return 0;   // probe 需要首行实体；空输入短路（入口已判，防御在此）
        await using DbCommand rowCommand = conn.CreateCommand();
        DbParameter[] pool = new DbParameter[layout.ParameterCount];
        Action<int> bindRow = PrepareRowBinder(
            rowCommand, pool, entities, ctx, layout.ParameterCount, entities[0]);
        var batchLayout = new BatchLayout(layout, mappings, pool, bindRow);
        long totalInserted = 0;
        for (int start = 0; start < entities.Count; start += batchSize)
        {
            // C4：批间取消检查点——ct 已取消时不再进入下一批的建连/写盘
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + batchSize, entities.Count);
            totalInserted += await ExecuteBatchAsync(
                conn, transaction, new BatchRange(start, end), ctx, batchLayout, ct)
                .ConfigureAwait(false);
        }
        return totalInserted;
    }

    /// <summary>本批实体的起止区间（左闭右开）。</summary>
    private readonly record struct BatchRange(int Start, int End);

    /// <summary>B16/B15（2026-10-01 全 API 逐项轮）：批执行输入打包（列布局 + 列映射 +
    /// 参数池 + 行绑定器）——全部由 ExecuteAsync 每次调用解析/构建一次后下传、逐批复用；
    /// 打包同时避免 ExecuteBatchAsync 的 S107 参数列表。</summary>
    private readonly record struct BatchLayout(
        ColumnLayout Layout, MySqlBulkCopyColumnMapping[] Mappings,
        DbParameter[] Pool, Action<int> BindRow);

    /// <summary>目标表列布局 + 参数池宽度——由 ctx 一次性推出，逐批复用（B1/L4）。
    /// <para>复检轮发现（预存缺陷）：非自增 PK（Guid/string 键）实体的 PK 已含于 InsertColumns
    /// ——原构造重复添加列名致 DataTable 抛 DuplicateNameException（读取器路径下改用
    /// Array.IndexOf 定位，同一约束仍然适用）。仅补 InsertColumns 缺席的 PK（= 自增 PK，
    /// 生成器已将其排除于 InsertColumns）。补位列仍在最前（对齐 MySQL 表定义：
    /// AUTO_INCREMENT 通常首列），DBNull 占位与参数取值的偏移配对不变。</para>
    /// <para>ITM-615：MySqlConnector 不指定 ColumnMappings 时按序号匹配目标表
    /// （官方 issue #1375 确认）——故此处必须显式按列名映射，列序不再是契约。</para></summary>
    private readonly record struct ColumnLayout(int ParameterCount, string[] AllColumns, int PrimaryKeyPrefixLength)
    {
        public static ColumnLayout Build(MySqlBulkCopyContext ctx)
        {
            int primaryKeyPrefixLength = ctx.PrimaryKeyColumns
                .Count(pk => !ctx.InsertColumns.Contains(pk, StringComparer.Ordinal));
            string[] allColumns = [.. ctx.PrimaryKeyColumns
                .Where(pk => !ctx.InsertColumns.Contains(pk, StringComparer.Ordinal)),
                .. ctx.InsertColumns];
            return new ColumnLayout(ctx.InsertColumns.Count, allColumns, primaryKeyPrefixLength);
        }
    }

    /// <summary>B15（2026-10-01）：行绑定器与参数池的一次性准备（每调用一次，跨批复用其实例）——
    /// probe 验证 binder 输出参数数与列数一致（与 MultiValueBulkInsert 对齐）；ValuesBinder
    /// 存在时以 probe 建出的参数对象为池（零 CreateParameter 取值，v5.6 机制），否则回退
    /// 逐行 Binder（旧模型程序集；每行重建参数对象，本方法只负责把引用抄进池）。</summary>
    private static Action<int> PrepareRowBinder<T>(
        DbCommand rowCommand, DbParameter[] pool, IReadOnlyList<T> entities,
        MySqlBulkCopyContext ctx, int columnCount, T firstEntity)
        where T : class
    {
        ctx.Binder(rowCommand, firstEntity, 0);
        if (rowCommand.Parameters.Count != columnCount)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                $"{rowCommand.Parameters.Count} parameters.");

        if (ctx.ValuesBinder is { } valuesBinder)
        {
            // probe 已把首行绑到 rowCommand——直接取其参数对象作池（不再新建）
            for (int c = 0; c < columnCount; c++)
                pool[c] = rowCommand.Parameters[c];
            return index => valuesBinder(pool, entities[index], 0);
        }

        // 回退路径：Binder 每行重建参数对象（旧模型程序集无从零分配取值），
        // 这里只把新建出的参数对象引用抄进池，供读取器统一按 ordinal 取值。
        return index =>
        {
            // PARAM-REUSE-OK[noautoprep] 该回退仅旧模型程序集（无 ValuesBinder）可达；MySQL
            // 无 PostgreSQL 式语句准备缓存，参数对象每行重建无 stale 值面
            rowCommand.Parameters.Clear();
            ctx.Binder(rowCommand, entities[index], 0);
            for (int c = 0; c < columnCount; c++)
                pool[c] = rowCommand.Parameters[c];
        };
    }

    private static async Task<long> ExecuteBatchAsync(
        MySqlConnection conn,
        MySqlTransaction? transaction,
        BatchRange range,
        MySqlBulkCopyContext ctx,
        BatchLayout batchLayout,
        CancellationToken ct)
    {
        int start = range.Start;
        int end = range.End;
        ColumnLayout layout = batchLayout.Layout;
        string[] allColumns = layout.AllColumns;

        var bulk = new MySqlBulkCopy(conn, transaction)
        {
            DestinationTableName = ctx.QuotedTable,
            // ITM-655(r4)：0=无限（全库契约，非 30 秒）；正数透传
            BulkCopyTimeout = ctx.CommandTimeoutSeconds,
        };
        // ITM-721(r20) 反证结案：曾疑"裸列名与仓库其余路径 QuoteIdentifier 不对称"。
        // 核对驱动源码（MySqlConnector MySqlBulkCopy.cs）：ColumnMappings 的
        // DestinationColumn 在非表达式形态下由驱动执行 QuoteIdentifier（反引号包裹 +
        // 内嵌反引号翻倍）；此处传入已引用名会导致双重引用（`` `order` `` 被当作字面量）。
        // 故裸名是正确契约，保留。
        // B16（2026-10-01 全 API 逐项轮）：列映射解析提升到 ExecuteAsync（每调用一次）——
        // 原实现每批 1 个 string.Join 键串 + GetOrAdd 查找；mappings 由调用方传入，
        // PROV-002 的共享纪律不变（不可变值对象、跨 MySqlBulkCopy 实例共享安全）。
        foreach (MySqlBulkCopyColumnMapping mapping in batchLayout.Mappings)
            bulk.ColumnMappings.Add(mapping);
        // v5.6：DataTable → EntityDataReader（每行 372/360 B → 57 B，−84%；时间 −11%）
        // B15：池与 bindRow 由 ExecuteAsync 每次调用建一次、跨批复用（reader 仅在
        // WriteToServerAsync 期间读池，批间无残留引用）。
        using var reader = new EntityDataReader(start, end, batchLayout.Pool, batchLayout.BindRow,
            allColumns, layout.PrimaryKeyPrefixLength);
        MySqlBulkCopyResult result = await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
        // ITM-709(r20)：驱动文档明示"MySqlBulkCopy 用户应检查 Warnings 为非空，
        // 否则可能因数据类型转换失败静默丢数据"。当前只取行数会把"截断/转换失败"
        // 报成成功——非空即显式失败，避免静默数据损坏。
        if (result.Warnings.Count > 0)
        {
            // ITM-777(r21)：不回显 Warnings[0].Message 原文——MySQL 1366 等告警内嵌
            // 被拒原始值（"Incorrect integer value: '<value>'"），若该列是 [SensitiveData]，
            // 真实值会随异常消息进日志（掩码只作用于参数面，不覆盖此路径）。
            // 只报数量/错误码/Level，内容留给调用方按需自查。
            var first = result.Warnings[0];
            throw new InvalidOperationException(
                $"MySqlBulkCopy reported {result.Warnings.Count} warning(s); data may have been " +
                $"truncated or converted incorrectly. First warning: ErrorCode={first.ErrorCode}, Level={first.Level} " +
                "(message content redacted to avoid echoing rejected values).");
        }
        // ITM-656(r4)：MySqlConnector 服务端不报行数时返 -1——规范化为本批实体数
        long inserted = result.RowsInserted;
        return inserted >= 0 ? inserted : end - start;
    }

}

/// <summary>MySqlBulkCopy 上下文——参数打包避免 S107（>7 参数方法）。</summary>
internal readonly struct MySqlBulkCopyContext(
    string quotedTable,
    IReadOnlyList<string> insertColumns,
    IReadOnlyList<string> primaryKeyColumns,
    Action<DbCommand, object, int> binder,
    int commandTimeoutSeconds,
    int batchSize,
    Action<DbParameter[], object, int>? valuesBinder = null)
{
    public readonly string QuotedTable = quotedTable;
    public readonly IReadOnlyList<string> InsertColumns = insertColumns;
    /// <summary>主键列（通常为 AUTO_INCREMENT），读取器中返回 DBNull 让 MySQL 生成。</summary>
    public readonly IReadOnlyList<string> PrimaryKeyColumns = primaryKeyColumns;
    public readonly Action<DbCommand, object, int> Binder = binder;
    public readonly int CommandTimeoutSeconds = commandTimeoutSeconds;
    /// <summary>ITM-710：每批最多物化的实体数——与回退多值路径同口径。</summary>
    public readonly int BatchSize = batchSize;
    /// <summary>v5.6：仅设置预分配 INSERT 参数 Value 的委托（零 CreateParameter）。
    /// 旧版生成器模型程序集为 null——行循环回退逐行 <see cref="Binder"/>。</summary>
    public readonly Action<DbParameter[], object, int>? ValuesBinder = valuesBinder;
}
