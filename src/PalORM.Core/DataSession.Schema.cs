using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PalORM;

public sealed partial class DataSession<TProvider>
    where TProvider : IDbProvider
{
    /// <summary>ITM-723：探活类命令（HealthCheck/ValidateSchema）在用户配置"零超时"
    /// （= ADO.NET 无限等待）时的兜底上限——探活语义是"快速失败"，无界等待与意图相反。
    /// <para><b>ITM-769(r21) 契约</b>：DDL（建表/索引）<b>不</b>适用本兜底——大表 CREATE INDEX
    /// 可合理超过 30s，显式配置 Zero（无限）的用户意图应被尊重；探活与 DDL 的超时语义不同。</para></summary>
    private const int DefaultProbeTimeoutSeconds = 30;

    /// <summary>ITM-765(r21)：最近一次 MigrateAsync 因重名对象（如 MySQL 1061）跳过的索引 DDL。
    /// 跳过是幂等设计（ITM-528 裁决，不得改为抛异常），但默认会话无日志时静默无痕——
    /// 本属性是无副作用的事后可观测通道：迁移后检查非空即知有索引被跳过（可能存在
    /// 同名异构冲突，参见 MySqlProvider.IsDuplicateSchemaObject 文档）。每次 MigrateAsync 开头清空。</summary>
    public IReadOnlyList<string> LastMigrationSkippedIndexes => _lastMigrationSkippedIndexes;

    private List<string> _lastMigrationSkippedIndexes = [];

    /// <summary>见 DataSession 主文档。</summary>
    public async ValueTask<List<string>> ValidateSchemaAsync<T>(CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        List<string> issues = [];
        // ITM-671：未注册实体/缺元数据必须显式失败——静默空 issues 与"schema 完全匹配"
        // 不可区分，掩盖未注册/旧生成器问题（与 GetAsync/InsertAsync 未注册失败口径对齐）。
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated table metadata.");
        if (!PalORM_Runtime.ColumnNames.TryGetValue(typeof(T), out IReadOnlyList<string>? expectedColumns))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated column metadata.");

        try
        {
            await using DbCommand cmd = CreateCommand();
            // ITM-723(r21)：schema 校验是探活式命令，零超时（= 无限等待）下同样须有限兜底
            // ——原走 CreateCommand 的 _options.CommandTimeoutSeconds，CommandTimeout=Zero
            // 时服务端/网络挂起会让校验永不返回（与 HealthCheckAsync 口径不一致）。
            cmd.CommandTimeout = ProbeCommandTimeoutSeconds;
            int columnNameOrdinal = TProvider.ConfigureSchemaCommand(cmd, tableName);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var dbColumns = new HashSet<string>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                dbColumns.Add(reader.GetString(columnNameOrdinal));

            foreach (string colName in expectedColumns.Where(c => !dbColumns.Contains(c)))
                issues.Add($"Column '{colName}' not found in table '{tableName}'");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // ITM-542: 只透出异常类型名，不透传 ex.Message——Message 常含主机/端口/库名等拓扑信息，
        // 会顺 issues 列表泄露到调用方/日志。
        catch (Exception ex) { issues.Add($"Validation failed: {ex.GetType().Name}"); }
        return issues;
    }

    /// <summary>Schema 差异检测（CI 仅检查不执行）。
    /// <para><b>v4.0 起标 Obsolete</b>——本质是 <see cref="ValidateSchemaAsync{T}"/> 的字符串前缀包装，
    /// 增加了调用方心智负担却无新信息。直接用 <c>ValidateSchemaAsync&lt;T&gt;()</c> 然后按需加前缀。</para></summary>
    [Obsolete("Use ValidateSchemaAsync<T>() and apply prefix manually if needed. This thin wrapper adds no information. Scheduled for removal in v6.0.",
        DiagnosticId = "PALORM901")]
    public async ValueTask<List<string>> DiffAsync<T>(CancellationToken ct = default) where T : class, new()
        => (await ValidateSchemaAsync<T>(ct).ConfigureAwait(false)).Select(d => $"[DIFF] {d}").ToList();

    // ─── 迁移 ────────────────────────────────────────────

    /// <summary>从编译时生成的 DDL 执行迁移——零运行时反射。
    /// 建表后执行 [Index]/[Unique] 索引 DDL（ADR-B）；SQLite/PG 走 IF NOT EXISTS，
    /// MySQL 靠 IsDuplicateSchemaObject 识别重名索引实现幂等。</summary>
    public async ValueTask MigrateAsync(CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        _lastMigrationSkippedIndexes = [];  // ITM-765：每次迁移重置跳过清单
        // 评审 2026-09-02 第二批（ADR-J）：实体全集以 TableNames 为键源——legacy CreateTableSql
        // 已从生成物移除，方言 DDL（CreateTableSqlByDialect）是唯一执行真源。
        foreach (var type in PalORM_Runtime.TableNames.Keys)
        {
            // ITM-569：拒绝回退 legacy 单方言 DDL（与 GetCommandSqls 对称）——旧生成器片段缺
            // CreateTableSqlByDialect 键即明确拒绝；其 legacy CreateTableSql 恒为 SQLite 风格
            // 双引号，MySQL 上报语法错而非清晰的"请重新编译"。
            if (!PalORM_Runtime.CreateTableSqlByDialect.TryGetValue(
                    type, out CreateTableSqlSet sqls))
            {
                throw new InvalidOperationException(
                    $"Type '{type.Name}' has no dialect-specific generated DDL. " +
                    "The model assembly was compiled with an older PalORM source generator; recompile it against the current version.");
            }
            string ddl = sqls.Get(TProvider.Dialect);
            await using DbCommand cmd = CreateCommand();
            cmd.CommandText = ddl;
            // ITM-769(r21)：DDL 尊重显式 Zero（无限）——大表建索引可超任意兜底值
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // ITM-672 定稿：与建表 DDL 缺键（ITM-569）对称——索引元数据缺键必须显式拒绝，
            // 不能建表成功、索引静默缺失（中间版本生成器的表会以"无索引"形态运行）。
            // r18 配套契约：RegistryEmitter 对零索引实体也发射空 CreateIndexSqlSet，
            // 因此"键缺失"只可能是旧生成器或手工片段——当前生成器永不缺键。
            if (!PalORM_Runtime.CreateIndexSqlByDialect.TryGetValue(
                    type, out CreateIndexSqlSet indexSqls))
            {
                throw new InvalidOperationException(
                    $"Type '{type.Name}' has no dialect-specific generated index DDL. " +
                    "The model assembly was compiled with an older PalORM source generator; recompile it against the current version.");
            }
            await ApplyIndexDdlAsync(indexSqls.Get(TProvider.Dialect), ct).ConfigureAwait(false);
        }
    }

    /// <summary>ITM-723：零超时配置（= ADO.NET 无限等待）下 DDL/探活的有限兜底上限。</summary>
    private int ProbeCommandTimeoutSeconds
        => _options.CommandTimeoutSeconds == 0 ? DefaultProbeTimeoutSeconds : _options.CommandTimeoutSeconds;

    /// <summary>逐条执行索引 DDL，重名对象按幂等跳过（ITM-203）。
    /// <para><b>为什么跳过而非失败（ITM-528 既有裁决，r21 回退）</b>：MySQL 无
    /// <c>CREATE INDEX IF NOT EXISTS</c>，1061（索引名已存在）就是幂等信号——重复迁移是
    /// 正常运维场景（<c>ExternalDatabaseBulkTests</c> 有"二次迁移经 1061 兜底不抛"的既有断言）。
    /// 但 1061 无法区分"同名同构"（真幂等）与"同名异构"（实为冲突），且运行时无安全判别手段
    /// ——故 ITM-528 已裁决此处不改判定逻辑。</para>
    /// <para><b>可观察性契约</b>：跳过以 Warning 记录，需配置 <c>DbOptions.LoggerFactory</c>
    /// 才可见（默认会话是 NullLogger，IsEnabled 恒 false）。此处<b>不得</b>因日志不可见而改为
    /// 抛异常——那会把正常幂等升级为硬失败（r20 曾如此修复并引入回归，r21 撤销）。</para></summary>
    private async ValueTask ApplyIndexDdlAsync(
        IReadOnlyList<string> indexDdlStatements, CancellationToken ct)
    {
        foreach (string indexDdl in indexDdlStatements)
        {
            await using DbCommand indexCmd = CreateCommand();
            indexCmd.CommandText = indexDdl;
            indexCmd.CommandTimeout = _options.CommandTimeoutSeconds;  // ITM-769：同建表命令
            try
            {
                await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (DbException exception) when (TProvider.IsDuplicateSchemaObject(exception))
            {
                // MySQL 重名索引（1061）= 已建过（幂等跳过）；同名异构也触发 1061，
                // 记录警告以便配置了 LoggerFactory 的会话审计（ITM-203）。
                // ITM-765(r21)：同时写入 LastMigrationSkippedIndexes——默认会话（NullLogger）
                // 下这是唯一的可观测通道。
                _logger.LogWarning(
                    "Index DDL skipped as duplicate object; verify no cross-entity index name collision: {IndexDdl}",
                    indexDdl);
                _lastMigrationSkippedIndexes.Add(indexDdl);
            }
        }
    }

    // ─── 健康检查 ────────────────────────────────────────

    /// <summary>SELECT 1 探活——返回耗时与失败类型名（不含拓扑信息，ITM-542）。</summary>
    public async ValueTask<HealthResult> HealthCheckAsync(CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        var sw = Stopwatch.StartNew();
        try
        {
            await using DbCommand cmd = CreateCommand();
            cmd.CommandText = "SELECT 1";
            // ITM-557：健康检查最需快速失败——不设超时会按驱动默认（约 30s）挂起
            // ITM-723(r20)：CommandTimeout.Seconds==0 是 ADO.NET 的"无限等待"语义，与注释意图
            // 相反（服务端/网络挂起时探活永不返回）。Zero 配置下改用有限默认值兜底。
            cmd.CommandTimeout = ProbeCommandTimeoutSeconds;
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return new HealthResult(true, sw.Elapsed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // ITM-542: 只透出异常类型名，不透传 ex.Message（避免泄露主机/端口/库名等拓扑信息）。
            return new HealthResult(false, sw.Elapsed, ex.GetType().Name);
        }
    }

    /// <summary>⚠️ 逃生舱（escape hatch）—— 获取原生 <see cref="DbConnection"/>。
    /// <para><b>危险操作</b>：原生操作不受会话并发门禁保护——绕过 SessionOperationState 的
    /// 「同一 DataSession 同时只允许一个活动操作」契约，调用方完全负责并发安全与事务边界。</para>
    /// <para><b>第三方工具集成</b>场景（如 EF Core 迁移脚本、Dapper 共享连接）可用；普通 CRUD 场景应走
    /// From&lt;T&gt;/InsertAsync 等托管路径。返回的连接仍归 DataSession 持有，不应 Dispose。</para></summary>
    public DbConnection GetRawConnection()
    {
        _operationState.EnsureAvailable();
        return _conn;
    }
}
