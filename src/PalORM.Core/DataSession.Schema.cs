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
    /// <summary>ITM-824（r23）：返回快照——直接暴露内部 List 可被强转改写，且迁移进行中
    /// 并发枚举会撕裂（"集合已修改"）；快照化后调用方安全枚举，写侧单线契约不变。</summary>
    public IReadOnlyList<string> LastMigrationSkippedIndexes => [.. _lastMigrationSkippedIndexes];

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

    // ─── 迁移 ────────────────────────────────────────────

    /// <summary>从编译时生成的 DDL 执行迁移——零运行时反射。
    /// 建表后执行 [Index]/[Unique] 索引 DDL（ADR-B）；SQLite/PG 走 IF NOT EXISTS，
    /// MySQL 靠 IsDuplicateSchemaObject 识别重名索引实现幂等。
    /// <para><b>L4（v5.6.0）两阶段</b>：先全集校验（表/索引方言 DDL 键齐全）再执行——
    /// 原实现边校验边执行，type B 缺键在 type A 的 DDL 已执行后才抛，留半成品 schema；
    /// 现在缺键时零副作用。建表 DDL 经 <see cref="CreateBatch"/> 单次往返
    /// （PG 真 DbBatch / MySQL 驱动侧批处理 / SQLite 顺序回退），N 表 N 次往返 → 1 次；
    /// 索引 DDL 保持逐条——MySQL 1061 幂等跳过是逐条 catch 语义，批内单条失败无法定位跳过项。</para>
    /// <para><b>schema 变更后刷新计划器统计（2026-09-26）</b>：SQLite 跑 <c>PRAGMA optimize</c>、
    /// PostgreSQL 跑 <c>ANALYZE</c>（同构对应物，见 <c>docs/性能优化方案-step5.md</c> §九-E）；
    /// MySQL 不自动执行（InnoDB ANALYZE TABLE 是显式运维操作而非迁移副作用，经
    /// <see cref="DbOptions.SessionSetupSql"/> 按需执行）。</para></summary>
    public async ValueTask MigrateAsync(CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        _lastMigrationSkippedIndexes = [];  // ITM-765：每次迁移重置跳过清单
        await AcquireMigrationLockAsync(ct).ConfigureAwait(false);
        try
        {
            await MigrateCoreAsync(operation, ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseMigrationLockAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>迁移互斥锁键（PG 会话级 advisory lock 的任意固定 int64——无语义，只求
    /// 跨进程/跨实例恒定；2026-10-06 V601 诊断轮设立，CI 实验证实 4 路并发 MigrateAsync
    /// 在 PG 17 上稳定复现 42P07/pg_type 目录级竞态，现有 IsDuplicateSchemaObject 兜底
    /// 接不住目录错误）。</summary>
    private const long MigrationAdvisoryLockKey = 721894423635590;

    /// <summary>迁移互斥（会话级锁，连接关闭自动释放=崩溃安全）：多实例/多会话并发迁移
    /// 时 CREATE TABLE IF NOT EXISTS 仍存在目录级竞态（PG pg_type duplicate 23505 +
    /// 42P07 越过 IsDuplicateSchemaObject 兜底）。PG 走 advisory lock；MySQL 走
    /// GET_LOCK（60s 超时）；SQLite 无并发目录（库级写锁串行 + IF NOT EXISTS 原子），跳过。
    /// 调用方必须已持操作租约（本方法直用 _conn，不再入门禁）。</summary>
    private async ValueTask AcquireMigrationLockAsync(CancellationToken ct)
    {
        DbCommand cmd = CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            switch (TProvider.Dialect)
            {
                case SqlDialect.PostgreSql:
                    // S2077 报备（与 GetSoftDeleteUpdateSql 同口径）：插值成分=const long 键，
                    // 非用户输入，注入面不存在
#pragma warning disable S2077
                    cmd.CommandText = $"SELECT pg_advisory_lock({MigrationAdvisoryLockKey})";
#pragma warning restore S2077
                    await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    break;
                case SqlDialect.MySql:
                    // 分片重试而非单次长等（CI 实证：多作业共享一库时连续循环持锁会把
                    // 单次 GET_LOCK(60) 饿死——5s×60 次≈5 分钟上限，GET_LOCK FIFO 下必然轮到）
                    bool acquiredMySql = false;
                    for (int attempt = 0; attempt < 60 && !acquiredMySql; attempt++)
                    {
                        cmd.CommandText = "SELECT GET_LOCK('palorm_migrate', 5)";
                        // Convert 而非 is 装箱模式（CI 实测：驱动返回装箱 long，`is 1` 只匹配
                        // int——恒判"未获取"，实持锁假失败；Convert 跨 long/int/string 形态）
                        acquiredMySql = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;
                    }

                    if (!acquiredMySql)
                        throw new InvalidOperationException(
                            "MigrateAsync: GET_LOCK not acquired within ~5 minutes (60×5s) — concurrent migration storm did not drain.");

                    break;
            }
        }
    }

    private async ValueTask ReleaseMigrationLockAsync(CancellationToken ct)
    {
        DbCommand cmd = CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            switch (TProvider.Dialect)
            {
                case SqlDialect.PostgreSql:
#pragma warning disable S2077
                    cmd.CommandText = $"SELECT pg_advisory_unlock({MigrationAdvisoryLockKey})";
#pragma warning restore S2077
                    await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    break;
                case SqlDialect.MySql:
                    cmd.CommandText = "SELECT RELEASE_LOCK('palorm_migrate')";
                    await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>迁移执行体（互斥锁内）——原 MigrateAsync 主体。</summary>
    private async ValueTask MigrateCoreAsync(
        SessionOperationState.SessionOperationLease operation, CancellationToken ct)
    {
        // 评审 2026-09-02 第二批（ADR-J）：实体全集以 TableNames 为键源——legacy CreateTableSql
        // 已从生成物移除，方言 DDL（CreateTableSqlByDialect）是唯一执行真源。
        int entityCount = PalORM_Runtime.TableNames.Count;
        var tableEntries = new List<(Type Type, string Ddl, IReadOnlyList<string> IndexDdls)>(entityCount);
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
            tableEntries.Add((type, sqls.Get(TProvider.Dialect), indexSqls.Get(TProvider.Dialect)));
        }

        // ADR-B 限制④：按生成期 FK 依赖拓扑序建表（被引用表先建，空库前向引用可建）。
        // 旧片段无序时按注册字典枚举序兜底；order 未覆盖的实体（跨片段合并的旧片段）
        // 排尾并保持原相对序（稳定排序）。
        IReadOnlyList<Type>? migrationOrder = PalORM_Runtime.TableMigrationOrder;
        Dictionary<Type, int>? orderIndex = migrationOrder is null
            ? null
            : migrationOrder.Select((type, index) => (type, index))
                .ToDictionary(pair => pair.type, pair => pair.index);
        if (orderIndex is not null)
        {
            tableEntries = tableEntries
                .Select((entry, original) => (entry, original))
                .OrderBy(pair => orderIndex.TryGetValue(pair.entry.Type, out int i) ? i : int.MaxValue)
                .ThenBy(pair => pair.original)
                .Select(pair => pair.entry)
                .ToList();
        }

        var tableDdls = new List<string>(entityCount);
        var indexDdlGroups = new List<IReadOnlyList<string>>(entityCount);
        foreach (var (_, ddl, indexDdls) in tableEntries)
        {
            tableDdls.Add(ddl);
            indexDdlGroups.Add(indexDdls);
        }

        // L4：建表 DDL 一次往返（owner 重入外层迁移租约）
        if (tableDdls.Count > 0)
            await ApplyTableDdlAsync(tableDdls, operation, ct).ConfigureAwait(false);

        foreach (IReadOnlyList<string> indexDdls in indexDdlGroups)
            await ApplyIndexDdlAsync(indexDdls, ct).ConfigureAwait(false);

        // schema 变更后刷新计划器统计（各服务端/引擎的官方建议动作）：
        //   SQLite（2026-09-26）：PRAGMA optimize——官方对 schema 变更的建议；本引擎无 STAT4，
        //     sqlite_stat1 是计划器唯一统计来源，直接影响 keyset/大 IN 查询计划。采样成本由
        //     连接初始化预设的 analysis_limit=400 约束。
        //   PostgreSQL（2026-09-26 平移）：ANALYZE——同构对应物（见 step5 §九-E）。空库/空表
        //     ANALYZE 近零成本（无采样页），建库首次迁移与后续幂等重迁都安全。
        //   MySQL：不自动执行——InnoDB ANALYZE TABLE 触发持久化统计重采样，大表上是显式运维
        //     操作而非迁移副作用；经 SessionSetupSql 按需手工执行（README 配方段载明）。
        // 方言静态判定（BATCH-002 同范式）。
        string? analyzeSql = TProvider.Dialect switch
        {
            SqlDialect.Sqlite => "PRAGMA optimize;",
            SqlDialect.PostgreSql => "ANALYZE;",
            _ => null,
        };
        if (analyzeSql is not null)
        {
            await using DbCommand analyzeCmd = CreateCommand();
            analyzeCmd.CommandText = analyzeSql;
            await analyzeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>ITM-723：零超时配置（= ADO.NET 无限等待）下 DDL/探活的有限兜底上限。</summary>
    private int ProbeCommandTimeoutSeconds
        => _options.CommandTimeoutSeconds == 0 ? DefaultProbeTimeoutSeconds : _options.CommandTimeoutSeconds;

    /// <summary>清空本会话生效的查询缓存（ADR-O C3：写后显式失效 API）。
    /// <para>解析口径与查询路径同源（<see cref="DbOptions.QueryCache"/> 注入优先，未注入时为
    /// 进程级默认实例 <see cref="CacheStore.Default"/>，对照 QueryBuilder 构造的
    /// <c>ctx.QueryCache ?? CacheStore.Default</c>）。注意清的是"缓存实例"而非"本会话创建的
    /// 条目"：注入实例被多会话共享时全部失效（语义即"该缓存整体已过期"）；默认实例影响全部
    /// 未注入会话（与 <see cref="CacheStore.Clear"/> 同语义）。</para>
    /// <para><b>ADR-O 定位</b>：TTL 最终一致（C1）语义不变，本方法是显式收窄失效窗口的
    ///  opt-in——写后调用即写后读一致，忘调 = 等价旧行为，无更差。典型 recipe：
    /// <c>await db.InsertAsync(e); db.EvictQueryCache();</c></para></summary>
    public void EvictQueryCache()
        => (_options.QueryCache ?? CacheStore.Default).Clear();

    /// <summary>建表 DDL 批执行（L4 单次往返）+ 并发建表竞态兜底。
    /// <para><b>竞态兜底（2026-09-28，PG 实证）</b>：CREATE TABLE IF NOT EXISTS 的存在性检查
    /// 与 pg_type 随行复合类型插入非原子——多会话并发 MigrateAsync 建同名表时，后到者撞
    /// <c>pg_type_typname_nsp_index</c>（由 <see cref="IDbProvider.IsDuplicateSchemaObject"/> 识别），
    /// 批内单条失败即整批报废。回退逐条执行：竞态过的表由 IF NOT EXISTS 自然跳过
    ///（MySQL 1061 索引兜底同族；慢路径仅在竞态异常发生时进入，正常路径保持批单往返）。</para></summary>
    private async ValueTask ApplyTableDdlAsync(
        List<string> tableDdls, SessionOperationState.SessionOperationLease operation, CancellationToken ct)
    {
        using SessionBatch<TProvider> batch = CreateBatch();
        foreach (string ddl in tableDdls)
            _ = batch.AppendRaw(ddl);
        try
        {
            _ = await batch.ExecuteNonQueryAsync(operation.Owner, ct).ConfigureAwait(false);
        }
        catch (DbException exception) when (TProvider.IsDuplicateSchemaObject(exception))
        {
            foreach (string ddl in tableDdls)
            {
                await using DbCommand fallback = CreateCommand();
                fallback.CommandText = ddl;
                fallback.CommandTimeout = _options.CommandTimeoutSeconds;
                try
                {
                    await fallback.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (DbException duplicate) when (TProvider.IsDuplicateSchemaObject(duplicate))
                {
                    // 已存在 = IF NOT EXISTS 的期望结果（并发对方已建成）
                }
            }
        }
    }

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
