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
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName))
            return issues;
        if (!PalORM_Runtime.ColumnNames.TryGetValue(typeof(T), out IReadOnlyList<string>? expectedColumns))
            return issues;

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

    /// <summary>Schema 差异检测（CI 仅检查不执行）。</summary>
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

            if (!PalORM_Runtime.CreateIndexSqlByDialect.TryGetValue(
                    type, out CreateIndexSqlSet indexSqls))
            {
                continue;
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

    /// <summary>逃生舱 —— 获取原生 DbConnection（第三方工具集成）。原生操作不受会话并发门禁保护。</summary>
    public DbConnection GetRawConnection()
    {
        _operationState.EnsureAvailable();
        return _conn;
    }

    /// <summary>创建独立的只读会话。调用方必须释放返回的会话；新代码请使用 <c>From&lt;T&gt;().ForRead()</c>。</summary>
    [Obsolete("返回值具有独立连接所有权。请使用 From<T>().ForRead() 让查询执行管线管理连接。3.0 移除。")]
    public async ValueTask<DataSession<TProvider>> ForRead(CancellationToken ct = default)
    {
        // 保留 $ENV: 间接引用原样传递，不物化为明文——CreateAsync 内部按需解析。
        // 物化会让刻意用环境变量避免明文驻留的配置在新 DbOptions 实例中出现明文密码。
        string? readConnectionString = _options.ReadConnectionString ?? _options.ConnectionString;
        DbOptions readOptions = _options with { ConnectionString = readConnectionString, ReadConnectionString = null };
        return await CreateAsync(readOptions, ct).ConfigureAwait(false);
    }

    /// <summary>返回当前主库会话。新代码请使用 <c>From&lt;T&gt;().ForWrite()</c> 明确查询路由。</summary>
    [Obsolete("DataSession 始终持有主连接。请使用 From<T>().ForWrite() 明确查询路由。3.0 移除。")]
    public DataSession<TProvider> ForWrite()
    {
        return this;
    }
}
