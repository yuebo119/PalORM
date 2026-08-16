using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PalORM;

public sealed partial class DataSession<TProvider>
    where TProvider : IDbProvider
{
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
        foreach (var type in PalORM_Runtime.CreateTableSql.Keys)
        {
            // ITM-569：拒绝回退 legacy 单方言 DDL（与 GetCommandSqls 对称）——旧生成器片段的
            // CreateTableSql 恒为 SQLite 风格双引号，MySQL 上报语法错而非清晰的"请重新编译"。
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
            foreach (string indexDdl in indexSqls.Get(TProvider.Dialect))
            {
                await using DbCommand indexCmd = CreateCommand();
                indexCmd.CommandText = indexDdl;
                indexCmd.CommandTimeout = _options.CommandTimeoutSeconds;
                try
                {
                    await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (DbException exception) when (TProvider.IsDuplicateSchemaObject(exception))
                {
                    // MySQL 重名索引（1061）通常 = 已建过（幂等跳过）；但同名异构索引（另一实体
                    // 占用同名）也触发 1061——记录警告以免唯一约束静默缺失（ITM-203）
                    _logger.LogWarning(
                        "Index DDL skipped as duplicate; verify no cross-entity index name collision: {IndexDdl}",
                        indexDdl);
                }
            }
        }
    }

    // ─── 聚合方法 ────────────────────────────────────────

    /// <summary>COUNT 聚合。</summary>

    public async ValueTask<HealthResult> HealthCheckAsync(CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        var sw = Stopwatch.StartNew();
        try
        {
            await using DbCommand cmd = CreateCommand();
            cmd.CommandText = "SELECT 1";
            // ITM-557：健康检查最需快速失败——不设超时会按驱动默认（约 30s）挂起
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
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
