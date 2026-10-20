using System.Data.Common;
using System.Text;

namespace PalORM;

// Bulk operations (partial class — 从 DataSession.cs 拆分)
public partial class DataSession<TProvider>
{

    /// <summary>S6：批量插入的方言推荐批宽——PG 的 <c>batchSize</c> 是每次 COPY 会话的行数
    /// （COPY 无参数上限），默认 1000 会让 2 万行付 20 次 BeginBinaryImport/Complete 往返；
    /// 本重载 PG 取整段行数（单次 COPY），MySQL/SQLite 沿用 1000。需要限制单次 COPY/事务
    /// 持锁行数时用带 <c>batchSize</c> 的重载。</summary>
    public ValueTask<long> BulkInsertAsync<T>(IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entities);
        int recommended = TProvider.Dialect == SqlDialect.PostgreSql
            ? Math.Max(1, entities.Count)
            : 1000;
        return BulkInsertAsync(entities, recommended, ct);
    }

    /// <summary>批量插入——委托 Provider 使用源生成 InsertColumns 与 binder，并复用会话事务。
    /// <paramref name="batchSize"/> 为显式批宽（PG 为每次 COPY 行数，其余为每批多值 INSERT/LOAD 行数）。</summary>
    /// <returns><b>数据库受影响行数</b>（驱动 ExecuteNonQuery 口径）——与本家族
    /// BulkUpdate/BulkDelete 同口径；BulkMerge 例外（返回处理实体数，跨方言可预测，见其
    /// returns 说明）。</returns>
    public async ValueTask<long> BulkInsertAsync<T>(IReadOnlyList<T> entities, int batchSize, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // R13：元数据守卫的单一实现点在 Provider（BulkOperationFramework.EnsureInsertMetadata）。
        // 空列表不会进入 Provider，故仅此分支在会话层补一次检查；非空列表由 Provider
        // 在其空短路之前执行同一守卫，避免两次注册表查找。
        if (entities.Count == 0)
        {
            _ = BulkOperationFramework.EnsureInsertMetadata(typeof(T));
            return 0;
        }
        return await TProvider.BulkInsertAsync(_conn, GetActiveTransaction(), entities, batchSize,
            _options.CommandTimeoutSeconds, ct, _isolationLevel).ConfigureAwait(false);  // r6-N2
    }

    /// <summary>批量删除——每 500 个生成主键 IN 批次；软删除实体更新 deleted_at，其他实体物理删除。</summary>
    /// <returns><b>数据库受影响行数</b>（各批次 ExecuteNonQuery 之和，驱动口径）。
    /// 软删除路径返回被标记删除的行数（UPDATE 计数），与物理删除的 DELETE 计数口径一致。</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "批量删除的双路径（软删 UPDATE / 物理 DELETE）+ 事务包装+批次循环是必然复杂度。"
            + "拆分会引入跨方法状态传递，损害可读性。")]
    public async ValueTask<long> BulkDeleteAsync<T>(IReadOnlyList<object> keys, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(keys);
        // S1：表名/主键/键绑定器走每类型静态视图，免每调用一次注册表快照与三次字典查找。
        EntityRuntimeInfo info = GetEntityInfoForCrud<T>();
        if (info.PkColumn is not { } pkCol || info.BindDelete is not { } bindKey)
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        // r13-S1（r12 声称未交付第四例——本批真交付）：空短路后置同族口径
        if (keys.Count == 0) return 0;

        string tableName = info.TableName;
        bool isSoftDelete = (info.Features & EntityFeatures.SoftDelete) != 0;
        string quotedTable = TProvider.QuoteIdentifier(tableName);
        string quotedPrimaryKey = TProvider.QuoteIdentifier(pkCol);
        // 租户过滤与单条 DeleteAsync 对齐（ITM-404）：跨租户主键命中 0 行
        // M1（v5.6.0）：后缀 per-Dialect 缓存（语句随批次占位符变化，仅后缀可缓存）
        bool hasTenant = HasTenantFilter(info.Features);
        string tenantFilter = hasTenant ? GetTenantAppendFragment() : "";
        // L5：标识符与时间表达式集合一次算好——原实现每批重算
        // （QuoteIdentifier("deleted_at") × 2 + CurrentTimestampExpression 取值）
        DeleteIdentifiers identifiers = new(
            quotedTable, quotedPrimaryKey, TProvider.QuoteIdentifier("deleted_at"),
            TProvider.CurrentTimestampExpression, tenantFilter);
        int tenantParamCount = hasTenant ? 1 : 0;

        // UNNEST-1（2026-10-02）：PG 数组形态——`pk = ANY(@ids)` 一条语句取代 N 个 IN 占位符。
        // 能力检测（生成物提供主键数组构造器 + Provider 接受该元素类型的数组参数）全真才走，
        // 否则回退 IN 占位符形态（该形态对复合主键本就正确）。
        // 收益（2026-10-02 探针实测）：DELETE 时延 −52%、客户端分配 −99%（省 per-key 参数对象）。
        bool useArrayForm = IsArrayFormSupported<T>(out Func<IReadOnlyList<object>, int, int, Array?>? buildKeyArray);

        // BULK-001（2026-09-23）：本路径每批一次独立往返，批大小取"方言参数上限"与"单批行数上限"
        // 的较小者。原用 InClauseBatchSize（500，单语句内拼 IN 片段的约束）把 10 万键放大成 200 次往返；
        // PG/MySQL 为 5000（20 次），SQLite 受 999 参数上限约束（100 次；32766 大值经
        // 2026-09-26 同轮 A/B 实测证伪为负优化，见 SqlLimits.MaxBindParametersFor）。
        // ITM-809（r22 登记，r23 实修）：批大小扣减租户过滤参数——每批 BindDefaultFilterParameters
        // 追加 1 个租户参数，不扣则 SQLite+租户实体单语句 999+1=1000 越保守上限（同文件
        // BulkUpdateBatchAsync 的 (driverLimit - tenantParams) 同口径）。
        // UNNEST-1：数组形态不受"语句内参数个数"约束（整批一个参数），批大小只为限制单语句
        // 触及的行数与事务持锁时长——沿用同一档位，行为面不变。
        int batchSize = useArrayForm
            ? SqlLimits.MaxRowsPerBatch
            : Math.Min(
                Math.Max(1, SqlLimits.MaxBindParametersFor(TProvider.Dialect) - tenantParamCount),
                SqlLimits.MaxRowsPerBatch);
        // v5.4 精炼 L1：事务骨架（复用/自开→commit/rollback→Restore→释放）收敛至
        // RunInTransactionScopeAsync 单点。两条形态各自一个作用域——数组形态不需要
        // 中转命令（scratch），共用作用域会为它白建一个命令（正是本次优化要消的固定开销）。
        if (useArrayForm)
        {
            return await RunInTransactionScopeAsync(
                operation.Owner,
                (tran, token) => ExecuteArrayFormDeleteAsync<T>(
                    tran, buildKeyArray!, keys, batchSize, identifiers, isSoftDelete, token),
                ct).ConfigureAwait(false);
        }

        // C3 延伸（EPSL 2026-10-11 审计 A 批）：IN 形态后续满批的键值换算改经 ConvertDeleteKey
        // 直写参数 Value——7caa357 否决"生成器写入器方案"的成本前提（需动 SourceGen+注册链+
        // 快照+旧模型回退）已被 C3 的委托基建整体沉没，此处零生成器改动消费既有委托，消除
        // 每键一次 scratch 参数创建 + 两次集合操作（20K 键约省 1.9 万个短命参数对象）。
        // 复合主键 / 旧生成器程序集为 null——保留 scratch 载体回退，行为不变。
        InFormKeyBinding keyBinding = new(bindKey, info.Crud.ConvertDeleteKey);

        return await RunInTransactionScopeAsync(
            operation.Owner,
            (tran, token) => ExecuteInFormDeleteAsync<T>(
                tran, keyBinding, keys, batchSize, identifiers, isSoftDelete, token),
            ct).ConfigureAwait(false);
    }

    /// <summary>F2（2026-10-13 全量重读）：批量删除的<b>类型化键重载</b>——数组形态可用且键类型
    /// 与生成物数组元素类型等同时，键从调用方到数组参数<b>全程零装箱</b>（对象重载的
    /// <c>IReadOnlyList&lt;object&gt;</c> 迫使调用方对每个值类型键装箱，20K 个 long 键约 480KB
    /// 短命垃圾，随后 <c>BuildDeleteKeyArray</c> 再逐个解箱）。
    /// <para><b>等同性门控</b>：直接拷贝与生成物构造器的逐位等价由
    /// <see cref="CanUseTypedKeyArrayCopy{T,TKey}"/> 的双值探针钉住（以生成物自身为真源，
    /// 探测两个真实键值经构造器往返后类型为 <c>TKey[]</c> 且值相等）；探针结果按
    /// (实体类型, 键类型) 每进程缓存一次。转换器主键（<c>ToProvider</c> 有真实转换语义）
    /// 或类型不匹配（如 long 主键传 int 键）时探针不通过，装箱回退既有对象路径——语义与
    /// 守卫（元数据检查先于空列表短路等口径）完整继承。</para>
    /// <para><b>为什么落在数组形态</b>：IN 占位符形态的每键参数本就以 object 存储（装箱发生在
    /// 参数写入侧，与调用方装箱等量），类型化无净收益；数组形态的元素是类型化数组的槽位，
    /// 装箱可整体消除。</para>
    /// <para>其余语义（软删/租户过滤/事务包装/批大小/返回口径）与
    /// <see cref="BulkDeleteAsync{T}"/> 逐位一致。</para></summary>
    public ValueTask<long> BulkDeleteAsync<T, TKey>(IReadOnlyList<TKey> keys, CancellationToken ct = default)
        where T : class, new()
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count >= 2
            && CanUseTypedKeyArrayCopy<T, TKey>(keys))
        {
            return BulkDeleteTypedArrayAsync<T, TKey>(keys, ct);
        }
        // 回退：装箱后委托既有对象路径（单键/空列表/类型不匹配/数组形态不可用——
        // 单键装箱只 1 次，探针成本反高于收益；空列表保持对象路径的守卫次序语义）
        object[] boxed = new object[keys.Count];
        for (int i = 0; i < boxed.Length; i++)
            boxed[i] = keys[i];
        return BulkDeleteAsync<T>(boxed, ct);
    }

    /// <summary>F2：类型化数组删除主体——与 <see cref="ExecuteArrayFormDeleteAsync{T}"/> 同构，
    /// 差异仅在批数组构造：直接类型化拷贝（等同性已由探针钉住）替代生成物构造器的逐键装箱转换。</summary>
    private async ValueTask<long> BulkDeleteTypedArrayAsync<T, TKey>(
        IReadOnlyList<TKey> keys, CancellationToken ct)
        where T : class, new()
        where TKey : notnull
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        EntityRuntimeInfo info = GetEntityInfoForCrud<T>();
        if (info.PkColumn is not { } typedPkCol)
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        string typedTableName = info.TableName;
        // 调用方已保证 keys.Count >= 2（门控条件），无空列表路径

        bool isSoftDelete = (info.Features & EntityFeatures.SoftDelete) != 0;
        DeleteIdentifiers identifiers = new(
            TProvider.QuoteIdentifier(typedTableName), TProvider.QuoteIdentifier(typedPkCol),
            TProvider.QuoteIdentifier("deleted_at"), TProvider.CurrentTimestampExpression,
            HasTenantFilter(info.Features) ? GetTenantAppendFragment() : "");
        // 深挖轮：数组形态批宽提升（见 UnnestArraysMaxRowsPerBatch 注释）
        int batchSize = UnnestArraysMaxRowsPerBatch;
        return await RunInTransactionScopeAsync(
            operation.Owner,
            (tran, token) => ExecuteTypedArrayDeleteCoreAsync<T, TKey>(
                tran, keys, batchSize, identifiers, isSoftDelete, token),
            ct).ConfigureAwait(false);
    }

    /// <summary>F2：批循环内核——命令/数组参数建一次批间只换 Value（R-UNNESTB 纪律同
    /// <see cref="ExecuteArrayFormDeleteAsync{T}"/>）。
    /// G6 证伪登记（2026-10-13 五轮微项包实施轮）：<c>CreateArrayParameter</c> 的返回值允许
    /// Provider 变换（非 PG 方言需把数组编码为可绑定形态，测试夹具 ArrayFormDialectProvider
    /// 实锤——SQLite 形态把数组编码为 JSON 文本），"临时参数仅当值通道、Value 即原数组"
    /// 只是 Npgsql 的实现细节不是接口契约；批间直赋原数组的优化不成立，维持每批经
    /// Provider 构造（值变换是契约的一部分）。</summary>
    private async Task<long> ExecuteTypedArrayDeleteCoreAsync<T, TKey>(
        DbTransaction? tran,
        IReadOnlyList<TKey> keys,
        int batchSize,
        DeleteIdentifiers identifiers,
        bool isSoftDelete,
        CancellationToken ct)
        where T : class, new()
        where TKey : notnull
    {
        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = BuildBulkDeleteArraySql(identifiers, isSoftDelete);
        DbParameter idsParameter = TProvider.CreateParameter(ArrayParameterName, DBNull.Value);
        cmd.Parameters.Add(idsParameter);
        BindDefaultFilterParameters<T>(cmd);

        long total = 0;
        for (int start = 0; start < keys.Count; start += batchSize)
        {
            int batchLen = Math.Min(batchSize, keys.Count - start);
            TKey[] values = new TKey[batchLen];
            for (int i = 0; i < batchLen; i++)
                values[i] = keys[start + i];
            idsParameter.Value = (TProvider.CreateArrayParameter(ArrayParameterName, values)
                ?? throw new InvalidOperationException(
                    $"Provider '{TProvider.Name}' returned a null array parameter for element type "
                    + $"'{typeof(TKey).Name}' (batch length {batchLen}); the capability probe and the "
                    + "execution path disagree.")).Value;
            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    /// <summary>F2：类型化直拷的等同性门控（含每 (T, TKey) 一次的双值探针缓存）。
    /// <para><b>为什么以生成物为真源做运行时探针</b>：直接拷贝的等价条件是"生成物
    /// BuildDeleteKeyArray 对 TKey 输入的产出恒为 identity"——数值/Guid/string 主键无转换器时
    /// 恒成立；转换器主键的 <c>ToProvider</c> 有真实转换语义时被探针否决。静态信号（"主键无
    /// 转换器"）需要 SourceGen 新增真源并改注册链/快照面；运行时探针以既有生成物自身度量，
    /// 双值（两个不同键值）往返比对后缓存，漏检需要 ToProvider 恰好对两个真实探测值恒等而
    /// 对其余值不恒等——属对抗性形态，与 R2-A2 的 identity 快路径同一风险量级。</para></summary>
    private static bool CanUseTypedKeyArrayCopy<T, TKey>(IReadOnlyList<TKey> keys)
        where T : class, new()
        where TKey : notnull
    {
        if (!IsArrayFormSupported<T>(out Func<IReadOnlyList<object>, int, int, Array?>? buildKeyArray)
            || buildKeyArray is null)
            return false;
        (Type, Type) cacheKey = (typeof(T), typeof(TKey));
        if (DataSessionCache.TypedKeyIdentityCache.TryGetValue(cacheKey, out bool cached))
            return cached;
        // 双值探针：装箱 2 次 + 2 个短数组，每 (T, TKey) 每进程至多一次
        Array probe = buildKeyArray([keys[0], keys[1]], 0, 2)
            ?? throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' generated a null primary-key array during the typed-key probe.");
        bool identity = probe is TKey[] typed
            && EqualityComparer<TKey>.Default.Equals(typed[0], keys[0])
            && EqualityComparer<TKey>.Default.Equals(typed[1], keys[1]);
        DataSessionCache.TypedKeyIdentityCache.TryAdd(cacheKey, identity);
        return identity;
    }

    /// <summary>数组形态的批量删除主体（UNNEST-1）——每批一句 <c>pk = ANY(@ids)</c>，
    /// 语句文本跨批恒定（单参数名与批长度无关），故只建一次命令文本。
    /// <para><b>R-UNNESTB 修正（2026-10-02）</b>：数组参数<b>建一次、批间只改 Value</b>。
    /// 原实现每批 <c>Parameters.Clear()</c> + Add 新建的数组参数——命令跨批复用时，
    /// PG 的 auto-prepare 会在 prepare 那一刻缓存当时的参数对象，后续 Clear+Add 的新对象
    /// 不被读取，第 3 批起静默发送第 2 批的键数组（删错行且无异常）。探针见 CHANGELOG R-UNNESTB 段。</para></summary>
    private async Task<long> ExecuteArrayFormDeleteAsync<T>(
        DbTransaction? tran,
        Func<IReadOnlyList<object>, int, int, Array?> buildKeyArray,
        IReadOnlyList<object> keys,
        int batchSize,
        DeleteIdentifiers identifiers,
        bool isSoftDelete,
        CancellationToken ct)
        where T : class, new()
    {
        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = BuildBulkDeleteArraySql(identifiers, isSoftDelete);
        // 参数对象批间不变（只换 Value）——见方法级 R-UNNESTB 说明。
        DbParameter idsParameter = TProvider.CreateParameter(ArrayParameterName, DBNull.Value);
        cmd.Parameters.Add(idsParameter);
        BindDefaultFilterParameters<T>(cmd);

        long total = 0;
        for (int start = 0; start < keys.Count; start += batchSize)
        {
            int batchLen = Math.Min(batchSize, keys.Count - start);
            idsParameter.Value = BuildArrayParameter<T>(buildKeyArray, keys, start, batchLen).Value;
            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    /// <summary>IN 占位符形态的批量删除主体（UNNEST-1 之前的既有路径）——满批语句文本恒定，
    /// 末批（更短）单独一条短语句。
    /// <para><b>满批命令与参数实例跨批复用，批间只写 Value</b>（2026-10-07 池化恢复）：2026-10-05 审计
    /// 曾因 R-UNNESTB 变体 C 退到"每批新建命令"（B4 的 Clear 后重加 per-key 新建参数实例，在
    /// PG auto-prepare 下第 3 个等长批重发第 2 批的键、静默少删），代价 +71%（20K 键 39→66ms）。
    /// 本形态消除复发窗口的方式不是恢复 B4，而是对齐数组形态（<see cref="ExecuteArrayFormDeleteAsync{T}"/>）
    /// 的纪律：满批参数对象从首批转移进集合后从不替换、不 Clear，后续满批优先经
    /// <see cref="CrudMetadata.ConvertDeleteKey"/> 直写 <c>Value</c>（C3 延伸，2026-10-11：单键
    /// 委托非空时零 scratch 参数创建，与 GetByKey 复用槽同一委托），复合主键/旧生成器回退
    /// scratch 载体读出 provider 值（与 <see cref="BindRowValuesViaProbeAsync{T}"/> 同一模式）——
    /// auto-prepare 缓存的参数引用始终有效，Clear/Add 错配窗口在源头不存在。末批语句文本不同，
    /// 单独新命令 + 全新参数集合（一次性成本，批数占比 1/N），不复用满批集合故无裁剪争议。</para></summary>
    private async Task<long> ExecuteInFormDeleteAsync<T>(
        DbTransaction? tran,
        InFormKeyBinding keyBinding,
        IReadOnlyList<object> keys,
        int batchSize,
        DeleteIdentifiers identifiers,
        bool isSoftDelete,
        CancellationToken ct)
        where T : class, new()
    {
        // ITM-676 等价保持：scratch 在作用域内创建——创建失败时内核 finally
        // 仍执行 Restore+事务释放；await using 覆盖批间清理。
        // R10：scratch 跨批次复用（对齐 MultiValueBulkInsert rowCommand 模式）。
        await using DbCommand scratch = CreateCommand();
        long total = 0;
        int start = 0;

        int fullBatchCount = keys.Count / batchSize;
        if (fullBatchCount > 0)
        {
            await using DbCommand cmd = CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = BuildBulkDeleteSql(batchSize, identifiers, isSoftDelete);
            for (int batch = 0; batch < fullBatchCount; batch++, start += batchSize)
            {
                if (batch == 0)
                {
                    // 首个满批：转移形态建池（scratch 改名转移，参数实例从此稳定）
                    BindInFormParameters<T>(cmd, scratch, keyBinding.Build, keys, start, batchSize);
                    BindDefaultFilterParameters<T>(cmd);
                }
                else
                {
                    // 后续满批：参数实例不变，只写 Value（见方法级 R-UNNESTB 说明）
                    WriteInFormValues<T>(scratch, keyBinding, cmd, keys, start, batchSize);
                }
                total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        int tailLen = keys.Count % batchSize;
        if (tailLen > 0)
        {
            // 末批：短语句 + 新命令（fresh 形态，非跨批复用）
            await using DbCommand tail = CreateCommand();
            tail.Transaction = tran;
            tail.CommandText = BuildBulkDeleteSql(tailLen, identifiers, isSoftDelete);
            BindInFormParameters<T>(tail, scratch, keyBinding.Build, keys, start, tailLen);
            BindDefaultFilterParameters<T>(tail);
            total += await tail.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    /// <summary>数组形态的参数名——与 <see cref="IDbProvider.GetParameterPlaceholder"/> 的
    /// <c>@pN</c> 命名空间不冲突；租户等默认过滤参数由 <see cref="BindDefaultFilterParameters{T}"/>
    /// 按自身命名空间追加，同样不与本名冲突。</summary>
    private const string ArrayParameterName = "@ids";

    /// <summary><c>BulkDeleteAsync</c> 数组形态的能力检测（UNNEST-1，2026-10-02）。
    /// <para>两条件全真才为 true：生成物提供主键数组构造器（非 null——复合主键与旧生成器为 null）、
    /// Provider 支持数组参数（<see cref="IDbProvider.CreateArrayParameter"/> 对该主键的
    /// <b>真实元素类型</b>返回非 null）。<b>能力检测而非方言枚举</b>：自定义 Provider 只要实现
    /// 数组参数即自动获得该路径（对齐 O25 阈值改能力检测的教训）。</para>
    /// <para><b>探测用真实元素类型</b>：以零长度数组试建参数——元素类型取自
    /// <c>BuildDeleteKeyArray</c> 对空区间的产物（类型化数组），故 string 主键与 long 主键
    /// 各自得到正确判定；用固定探测类型（如 <c>long[]</c>）会让"不支持 long 数组"误判为
    /// "不支持数组形态"。探测产物即丢弃，代价一次堆分配 + 一次参数构造。</para></summary>
    private static bool IsArrayFormSupported<T>(
        out Func<IReadOnlyList<object>, int, int, Array?>? buildKeyArray)
        where T : class, new()
    {
        buildKeyArray = null;
        EntityRuntimeInfo info = GetEntityInfo<T>();
        Func<IReadOnlyList<object>, int, int, Array?>? builder = info.Crud.BuildDeleteKeyArray;
        if (builder is null)
        {
            SqlSlots<T>.DeleteArraySupported = false;
            return false;
        }
        // S1：能力只取决于 (TProvider, T) 与生成物元素类型，每类型探测一次。
        if (SqlSlots<T>.DeleteArraySupported is { } known)
        {
            buildKeyArray = known ? builder : null;
            return known;
        }
        if (builder([], 0, 0) is not { } probe)
        {
            SqlSlots<T>.DeleteArraySupported = false;
            return false;
        }
        bool supported = TProvider.CreateArrayParameter(ArrayParameterName, probe) is not null;
        SqlSlots<T>.DeleteArraySupported = supported;
        buildKeyArray = supported ? builder : null;
        return supported;
    }

    /// <summary>构造数组形态的数组参数——元素类型与值取自生成物（与 <c>BindDelete</c> 同一真源）。
    /// <para>Provider 返回 null 属契约破坏：<see cref="IsArrayFormSupported{T}"/> 已用**同一元素类型**
    /// 的零长度数组探测过，真实批次应得同一结论（不一致只可能来自 Provider 对空数组特殊处理）。
    /// 明确抛错而非静默回退——静默回退会掩盖"探测与执行不一致"这一真实缺陷。</para></summary>
    private static DbParameter BuildArrayParameter<T>(
        Func<IReadOnlyList<object>, int, int, Array?> buildKeyArray,
        IReadOnlyList<object> keys,
        int start,
        int count)
        where T : class, new()
    {
        Array values = buildKeyArray(keys, start, count)
            ?? throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' generated a null primary-key array.");
        return TProvider.CreateArrayParameter(ArrayParameterName, values)
            ?? throw new InvalidOperationException(
                $"Provider '{TProvider.Name}' returned a null array parameter for element type "
                + $"'{values.GetType().GetElementType()?.Name}'.");
    }

    /// <summary>IN 占位符形态的参数绑定（UNNEST-1 之前的既有路径，数组形态不适用时回退于此）。
    /// <para>binder 固定产出 <c>@p0</c>——不能直接绑到 <paramref name="cmd"/> 再改名：
    /// MySqlConnector 在 Add 时即拒绝集合内重名（SQLite 容忍瞬时重名掩盖了这点，真库 AOT 实测暴露）。</para>
    /// <para>PG-2（2026-09-26）：中转参数按批内序号**改名后转移**进目标集合，不再每 key 重建一个
    /// 参数对象——Clear/RemoveAt 不移交参数所有权（真库探针实测：改名 Add 到另一命令后执行与复用
    /// 均正确），10 万键省 10 万个 NpgsqlParameter 与同等次数的装箱 + Provider DbType switch。</para>
    /// <para>B5（2026-10-01）：占位符名从预建数组（<c>string[batchSize]</c>，5000 元素约 40KB）
    /// 改为索引直取——<see cref="IDbProvider.GetParameterPlaceholder"/> 默认实现即
    /// <see cref="ParameterNameCache"/> 索引取用（零分配），SQL 文本与参数名同源直取天然一致。</para></summary>
    private static void BindInFormParameters<T>(
        DbCommand cmd,
        DbCommand scratch,
        Action<DbCommand, object> bindKey,
        IReadOnlyList<object> keys,
        int start,
        int batchLen)
        where T : class, new()
    {
        for (int index = 0; index < batchLen; index++)
        {
            // PARAM-REUSE-OK[carrier] scratch 从不执行，仅承载键绑定器（ITM-676 中转参数）
            scratch.Parameters.Clear();
            bindKey(scratch, keys[start + index]);
            if (scratch.Parameters.Count != 1)
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' generated an invalid primary-key binder.");

            var moved = scratch.Parameters[0];
            // PARAM-REUSE-OK[carrier] 移出载体命令后改名转移进目标集合（同一实例，非新对象）
            scratch.Parameters.Clear();
            moved.ParameterName = TProvider.GetParameterPlaceholder(index);
            cmd.Parameters.Add(moved);
        }
    }

    /// <summary>等长批与末批前缀的键值写入——单键转换委托（<see cref="CrudMetadata.ConvertDeleteKey"/>，
    /// C3 延伸 2026-10-11）非空时直写 <c>Value</c>：每键零参数创建、零集合操作；null（复合主键 /
    /// 旧生成器程序集）回退 scratch 载体承载键绑定器的转换产出（与
    /// <see cref="BindRowValuesViaProbeAsync{T}"/> 同一模式），只把 <c>Value</c> 写进目标集合的
    /// 既有参数实例。两条路径不创建、不替换、不 Clear 目标集合（R-UNNESTB 变体 C 的 Clear+重加
    /// 窗口在本形态下不存在；scratch 里的短命参数从不执行、从不进目标命令，与 auto-prepare 无关）；
    /// 键转换语义逐位一致（同一 DeleteKeyValueExpression 真源，KeyConversionTests 契约）。</summary>
    private static void WriteInFormValues<T>(
        DbCommand scratch,
        InFormKeyBinding keyBinding,
        DbCommand cmd,
        IReadOnlyList<object> keys,
        int start,
        int batchLen)
        where T : class, new()
    {
        if (keyBinding.Convert is { } convertKey)
        {
            // 与 GetByKey 复用槽（TryAcquireGetByKeyCommand）同一委托、同一就地写形态
            for (int index = 0; index < batchLen; index++)
                cmd.Parameters[index].Value = convertKey(keys[start + index]);
            return;
        }

        for (int index = 0; index < batchLen; index++)
        {
            // PARAM-REUSE-OK[carrier] scratch 从不执行，仅承载键绑定器读出 provider 值（ITM-676 中转参数）
            scratch.Parameters.Clear();
            keyBinding.Build(scratch, keys[start + index]);
            if (scratch.Parameters.Count != 1)
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' generated an invalid primary-key binder.");
            cmd.Parameters[index].Value = scratch.Parameters[0].Value;
        }
    }

    /// <summary>数组形态的 DELETE / 软删 UPDATE 语句——`pk = ANY(@ids)`，单参数，文本跨批恒定。
    /// <para>与 <see cref="BuildBulkDeleteSql"/> 是同一语句的两个形态：WHERE 语义（主键集合
    /// ∩ 可选租户过滤 ∩ 软删的 deleted_at IS NULL）逐条等价，只有主键谓词的表达方式不同
    /// （<c>= ANY(数组)</c> 取代 <c>IN (占位符列表)</c>）。</para></summary>
    private static string BuildBulkDeleteArraySql(in DeleteIdentifiers identifiers, bool isSoftDelete)
    {
        var sb = new ValueStringBuilder(stackalloc char[256]);
        try
        {
            if (isSoftDelete)
            {
                sb.Append("UPDATE ");
                sb.Append(identifiers.QuotedTable);
                sb.Append(" SET ");
                sb.Append(identifiers.QuotedDeletedAt);
                sb.Append(" = ");
                sb.Append(identifiers.TimestampExpression);
                sb.Append(" WHERE ");
            }
            else
            {
                sb.Append("DELETE FROM ");
                sb.Append(identifiers.QuotedTable);
                sb.Append(" WHERE ");
            }
            sb.Append(identifiers.QuotedPrimaryKey);
            sb.Append(" = ANY(");
            sb.Append(ArrayParameterName);
            sb.Append(')');
            if (isSoftDelete)
            {
                sb.Append(" AND ");
                sb.Append(identifiers.QuotedDeletedAt);
                sb.Append(" IS NULL");
            }
            sb.Append(identifiers.TenantFilter);
            sb.TrimEnd();
            return sb.ToString();
        }
        finally { sb.Dispose(); }
    }

    /// <summary>删除语句的标识符与表达式集合——L5 提取到方法外，避免每批重算
    /// （QuoteIdentifier("deleted_at") × 2 + 时间表达式取值）。</summary>
    private readonly record struct DeleteIdentifiers(
        string QuotedTable, string QuotedPrimaryKey, string QuotedDeletedAt,
        string TimestampExpression, string TenantFilter);

    /// <summary>IN 形态批量删除的键绑定器对（C3 延伸 2026-10-11）——<see cref="Build"/> 经 scratch
    /// 载体建池（首批转移 / 末批短语句）；<see cref="Convert"/> 非空时后续满批直写参数 Value
    /// （零 scratch 参数创建，见 <see cref="WriteInFormValues{T}"/>），null（复合主键 / 旧生成器
    /// 程序集）回退 scratch 读出形态。</summary>
    private readonly record struct InFormKeyBinding(
        Action<DbCommand, object> Build, Func<object, object?>? Convert);

    /// <summary>L5：单批删除/软删语句——单个 <see cref="ValueStringBuilder"/> 顺序写出，
    /// 替代原「string[] + string.Join + 多重插值」的中间串。SQL 文本与旧实现逐字节一致。
    /// <para>B5（2026-10-01 全 API 逐项轮）：占位符名改为索引直取
    /// <see cref="IDbProvider.GetParameterPlaceholder"/>（默认实现 = ParameterNameCache 索引取用，
    /// 零分配）——消去每调用 string[batchSize] 预建数组；参数改名循环同源直取，天然一致。</para></summary>
    private static string BuildBulkDeleteSql(
        int batchLen, in DeleteIdentifiers identifiers, bool isSoftDelete)
    {
        var sb = new ValueStringBuilder(stackalloc char[256]);
        try
        {
            if (isSoftDelete)
            {
                sb.Append("UPDATE ");
                sb.Append(identifiers.QuotedTable);
                sb.Append(" SET ");
                sb.Append(identifiers.QuotedDeletedAt);
                sb.Append(" = ");
                sb.Append(identifiers.TimestampExpression);
                sb.Append(" WHERE ");
            }
            else
            {
                sb.Append("DELETE FROM ");
                sb.Append(identifiers.QuotedTable);
                sb.Append(" WHERE ");
            }
            sb.Append(identifiers.QuotedPrimaryKey);
            sb.Append(" IN (");
            for (int index = 0; index < batchLen; index++)
            {
                if (index > 0) sb.Append(", ");
                sb.Append(TProvider.GetParameterPlaceholder(index));
            }
            sb.Append(')');
            if (isSoftDelete)
            {
                sb.Append(" AND ");
                sb.Append(identifiers.QuotedDeletedAt);
                sb.Append(" IS NULL");
            }
            sb.Append(identifiers.TenantFilter);
            sb.TrimEnd();
            return sb.ToString();
        }
        finally { sb.Dispose(); }
    }

    /// <summary>批量更新。复用源生成 UPDATE 与并发语义，整个输入在同一事务内执行。
    /// <para>自动路由：非版本化实体在 PG/MySQL 走单语句批量；版本化实体在生成物提供并发
    /// 令牌参数/数组能力、PG UNNEST 或 MySQL JOIN VALUES ROW 可用时同样走单语句（逐行
    /// version 谓词 + <c>version = version + 1</c>）。冲突检测为计数级，异常类型与逐条
    /// DbBatch 路径一致；能力不全（SQLite、低版本 MySQL、旧生成器）回退逐条。</para>
    /// <para>ITM-556: [ConcurrencyCheck] 实体的内存 version 回填延迟到事务提交成功后统一执行——
    /// 中途冲突整批回滚时，已成功条目的内存状态与 DB 保持一致，重试不产生假冲突。
    /// 复用外部事务时回填发生在本方法返回前；若调用方随后回滚该外部事务，
    /// 内存 version 需重新查询同步（与单条 UpdateAsync 在外部事务中回滚的既有语义一致）。</para></summary>
    /// <returns><b>数据库受影响行数</b>（驱动 ExecuteNonQuery 口径；单语句多行 UPDATE 形态
    /// 或逐条批次，均按驱动计数累加）。BulkMerge 的返回口径例外，见其 returns 说明。</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "路由分派叠加非版本化与版本化两条单语句路径的能力门禁；拆分需跨方法传递"
            + "注册快照、事务归属与回退标志，状态面更大。")]
    public async ValueTask<long> BulkUpdateAsync<T>(IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        // R8：单次注册表快照贯穿路由判定与后续取值——原实现入口三次独立读
        // CurrentState（三次 Volatile.Read），Register/热重载窗口内可能跨版本混用元数据
        // （与 Insert/Update/Delete 的 r19/ITM-703 单快照纪律对齐）。
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata routeMetadata))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated CRUD.");
        if (entities.Count == 0) return 0;

        // v5.6.0 自动路由（L1）：满足全部条件时走单语句批量（远程 N 行 N 次 RTT → 1 次，
        // 与 BulkMerge 集合化同构收益）。条件不满足时回退逐条（SQLite / 能力不全）。
        //   非版本化实体：PG UNNEST / MySQL JoinValuesRow；
        //   版本化实体：v6.5 起同样走单语句（逐行 version 谓词 + version=version+1），
        //   冲突按受影响计数判定，异常类型与逐条 DbBatch 路径一致；生成物无并发令牌
        //   绑定能力、MySQL < 8.0.19 或 SQLite 时回退逐条。
        //   SQLite → CASE WHEN 在 SQLite 实测慢 6.4×（BulkUpdateBatchAsync 已有同判）。
        // B8（2026-10-01 全 API 逐项轮）：放开租户过滤排除——两路径租户语义已对齐：
        // 逐条池化路径（ExecuteBulkUpdatePooledAsync）与单语句内核（ExecuteBulkUpdateBatchesAsync）
        // 都追加 UPDATE ... AND tenant_id = @__tenant0（池尾租户参数）；由真库用例
        // BulkUpdateTenantRoutingTests（三方言：跨租户行零触碰）锁定，放开为纯性能改善
        //（租户实体 N 次往返 → 1 次）。附带修复：逐条池化的 DbBatch 分支原先漏拷池尾租户
        // 参数（PG 42703 / MySQL 未定义参数），本轮由该用例暴露并修复（BindIntoBatchCommand）。
        // B7（2026-10-12 审读重开落地）：放开软删排除——原"语义待厘清"已厘清：逐条与单语句
        // 批量的 SET 列集同源（生成器 UpdateColumns，含 deleted_at，两路径都从实体绑定该列），
        // 对软删实体行为逐位等价（BulkUpdateSoftDeleteRoutingTests 三方言实测一致，含恒逐条的
        // SQLite 基线）；排除纯属性能损失。调用方契约：实体须携带自身 deleted_at（置 null
        // 即复活该行，两路径同语义）。
        if (TProvider.Dialect != SqlDialect.Sqlite
            && entities.Count > 1)
        {
            if (routeMetadata.IncrementVersion is null)
            {
                // 直接复用 BulkUpdateBatchAsync 的核心逻辑（此处条件已排除其拒绝项）
                // ITM-878（r23）：TryGetValue + 族内统一异常（裸索引器在部分注册的病态片段下
                // 抛 KeyNotFoundException，与 "has no generated CRUD" 口径不一致）
                if (!state._tableNames.TryGetValue(typeof(T), out string? batchTableName))
                    throw new InvalidOperationException(
                        $"Type '{typeof(T).Name}' has no [Table] attribute.");
                string tableName = batchTableName;
                BatchUpdateContext ctx = PrepareBatchUpdateContext<T>(state, routeMetadata, tableName, entities[0]);
                // MySQL-7：MySQL 按服务端版本选 UPDATE JOIN VALUES ROW 形态（8.75×）+ 对应批宽
                BatchUpdateSqlBuilder.BatchUpdateForm form = await ResolveMySqlUpdateFormAsync(ct).ConfigureAwait(false);
                // UNNEST 阶段 B（2026-10-02）：PG 的数组形态——每列一个数组参数，语句文本与批宽无关；
                // 能力检测（生成物提供逐列填充器 + Provider 接受这些元素类型的数组）不全真时保持 VALUES 形态。
                if (UseUnnestArraysForUpdate<T>(routeMetadata, out IReadOnlyList<Type>? arrayElementTypes))
                {
                    return await RunInTransactionScopeAsync(
                        operation.Owner,
                        (tran, token) => ExecuteBulkUpdateArrayBatchesAsync(
                            entities, routeMetadata, ctx, tran, arrayElementTypes!, token),
                        ct).ConfigureAwait(false);
                }
                // BULK-001：方言参数上限 + 单批行数上限（文本规模）双约束
                // ITM-883（r24，ITM-809 族第 3 处）：分子扣租户参数——单语句批量内核每语句在池尾
                // 追加 1 个 @__tenant0（上方 B8 注释证两路径租户语义对齐），不扣则租户实体在
                // 参数恰好满批时越界 1 个，退回运行期协议层报错。与 BulkUpdateBatchAsync 的
                // (driverLimit - tenantParams) 同口径（族内四处现已全扣）。
                int singleStatementTenantParams = ctx.HasTenantFilter ? 1 : 0;
                int rowsPerBatch = Math.Min(
                    Math.Max(1, (SqlLimits.MaxBindParametersFor(TProvider.Dialect) - singleStatementTenantParams)
                        / (ctx.SetColumnCount + 1)),
                    MaxRowsPerUpdateBatchFor(form));
                return await RunInTransactionScopeAsync(
                    operation.Owner,
                    async (tran, token) =>
                        await ExecuteBulkUpdateBatchesAsync(entities, routeMetadata, ctx, tran, rowsPerBatch, form, token)
                            .ConfigureAwait(false),
                    ct).ConfigureAwait(false);
            }

            // 版本化实体：单语句路径（PG UNNEST / MySQL JoinValuesRow）在计数级保留乐观锁契约。
            // 能力不全时返回 null，回退下方逐条路径。
            if (await TryExecuteVersionedBulkUpdateAsync<T>(
                    entities, state, routeMetadata, operation.Owner, ct).ConfigureAwait(false) is { } versionedTotal)
            {
                return versionedTotal;
            }
        }

        return await ExecuteBulkUpdateRowByRowAsync<T>(entities, operation.Owner, ct).ConfigureAwait(false);
    }

    /// <summary>BulkUpdate 逐条核心逻辑（不含 EnterOperation）——供 BulkUpdateAsync 和 BulkUpdateBatchAsync SQLite 回退复用。
    /// v5.4 精炼 L1：事务骨架收敛至 RunInTransactionScopeAsync。
    /// <para><b>P0-1（2026-09-21）参数池化路径</b>：原实现每行走 <c>UpdateCoreAsync</c>，
    /// 每行新建 DbCommand + 重建全部参数 + 新建 async 委托。三方言实测每行
    /// 1457~1625 B，是 BulkInsert（148~736 B）的 2.2~11 倍——同一文件的
    /// <see cref="MultiValueBulkInsert"/> 早有「命令跨批复用 + 参数池 + 只写 Value」范式，
    /// 逐条 UPDATE 是漏项。现按生成器是否发射 <see cref="CrudMetadata.BindUpdateValues"/>
    /// 分派：有则走池化路径（零 CreateParameter），无则回退原逐条路径（旧模型程序集）。</para>
    /// <para><b>乐观锁语义保持不变</b>：affectedRows 的 0 行 / 多行检查与 version 内存回填
    /// 的时机（提交成功后统一执行）都按原契约保留——池化只改「参数怎么来」，不改判定。</para></summary>
    private async ValueTask<long> ExecuteBulkUpdateRowByRowAsync<T>(
        IReadOnlyList<T> entities, object? operationOwner, CancellationToken ct)
        where T : class, new()
    {
        // P0-1：元数据走每类型静态视图（注册期单调不可变，与 BulkUpdateAsync 的 R8 同口径）
        EntityRuntimeInfo info = GetEntityInfoForCrud<T>();
        CrudMetadata metadata = info.Crud;

        var (total, deferredVersionIncrements) = await RunInTransactionScopeAsync(
            operationOwner,
            async (transaction, token) => metadata.BindUpdateValues is { } valuesBinder
                ? await ExecuteBulkUpdatePooledAsync(
                    entities, metadata, valuesBinder, transaction, token).ConfigureAwait(false)
                : await ExecuteBulkUpdateLegacyAsync(
                    entities, operationOwner, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        // ITM-556：内存 version 回填在提交成功后统一执行——中途冲突整批回滚时，
        // 已成功条目的内存状态与 DB 保持一致，重试不产生假冲突。置于内核之外：
        // 仅成功提交路径可达此处（复用外部事务时回填发生在本方法返回前，
        // 调用方随后回滚该外部事务的既有语义不变）。
        foreach (Action increment in deferredVersionIncrements) increment();
        return total;
    }

    /// <summary>P0-1：池化逐条 UPDATE——命令与参数池建一次，逐行只写 Value。
    /// <para><b>为什么命令能跨行复用</b>：所有行的 UPDATE 语句逐位相同（同表同 SET 列集），
    /// 变的只是参数值；生成的 UPDATE 恒以 <c>WHERE pk = @pN [AND version = @pM]</c> 结尾，
    /// 参数序固定。因此 CommandText 设一次、参数对象建一次，逐行只写 Value。</para>
    /// <para><b>与 <see cref="ExecuteBulkUpdateBatchesAsync{T}"/> 的关系</b>：后者是「一条 SQL 更新 N 行」，
    /// 本方法是「N 条 SQL 各更新 1 行」——参数布局相同（每行 paramsPerRow 个），故直接复用
    /// <see cref="BatchUpdateSqlBuilder.CreateParameterArray"/> 的命名契约。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "池化逐条 UPDATE 的分支构成：每行参数缓存命中/建池、版本实体 deferred 回填、"
            + "DbBatch/单命令两驱动形态——拆分需在方法间传参数池与回填列表，状态面更大。")]
    private async ValueTask<(long Total, List<Action> Increments)> ExecuteBulkUpdatePooledAsync<T>(
        IReadOnlyList<T> entities, CrudMetadata metadata,
        Action<DbParameter[], object, int> valuesBinder,
        DbTransaction tran, CancellationToken ct)
        where T : class, new()
    {
        List<Action> increments = [];
        if (entities.Count == 0) return (0, increments);

        // P0-1：参数数从 probe 提取而非硬算——BindUpdateValues 的参数序是
        // [SET 列…, 主键…, 并发令牌?]（见 CommandFactoryEmitter.GenerateBindUpdateValuesBody），
        // 带 [ConcurrencyCheck] 的实体比 setColumnCount+1 多一个。硬算会让乐观锁实体
        // 的 version 参数拿不到值，静默写错数据。probe 同时是生成器三处
        // （SQL/Bind/元数据）漂移的运行时哨兵（与 PrepareBatchUpdateContext 同范式）。
        // R3（2026-10-12）：paramsPerRow per-(Type, Dialect) 缓存——probe 哨兵语义随 B9 先例
        //（PrepareBatchUpdateContext）转为每进程一次；生成器三处漂移仍由首进程 probe 拦截。
        (Type, SqlDialect) rowParamsKey = (typeof(T), TProvider.Dialect);
        if (!DataSessionCache.UpdateRowParamsCache.TryGetValue(rowParamsKey, out int paramsPerRow))
        {
            DbCommand probe = CreateCommand();
            try
            {
                metadata.BindUpdate(probe, entities[0]);
                paramsPerRow = probe.Parameters.Count;
            }
            finally
            {
                await probe.DisposeAsync().ConfigureAwait(false);
            }
            if (paramsPerRow <= 0)
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' BindUpdate produced {paramsPerRow} parameters; recompile the model assembly.");
            DataSessionCache.UpdateRowParamsCache.TryAdd(rowParamsKey, paramsPerRow);
        }

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        // 生成的 UPDATE 语句：复用 GetCommandSqls 的单一真源（含租户后缀的两形态由缓存提供）。
        // S1：静态槽命中零注册表读取；未命中才走冷路径解析。
        string updateSql = GetCommandSqls<T>().Update;
        if (updateSql.Length == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no updatable columns.");
        if (HasTenantFilter<T>())
            updateSql = GetUpdateWithTenantSqlFast<T>(updateSql);
        cmd.CommandText = updateSql;

        // 参数池：一行 paramsPerRow 个 + 可选租户参数（固定名追加末尾）
        bool hasTenant = HasTenantFilter<T>();
        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterArray(
            paramsPerRow, hasTenant, _tenantParameterName, _tenantId,
            TProvider.CreateParameter);
        // N4（2026-10-04 全量复读）：可空/byte[] 列的 DbType 提示建池后一次性建立（原由
        // BindUpdateValues 每行重写；租户槽在 CreateParameterArray 内经 CreateParameter 带值，
        // 不在 Init 列序内）。旧生成器程序集 Init 委托为 null：不调，其 binder 保持旧形态。
        metadata.InitUpdateParameters?.Invoke(pool, 0);
        AttachParameters(cmd, pool, paramsPerRow, paramsPerRow, hasTenant ? 1 : 0);

        // MySQL-8/PG-7：MySQL/PG dialect 走 DbBatch 打包——N 条 UPDATE 单次协议往返。
        // 探针十六实测（真库 100 命令，同连接同事务）：100 命令 1 个 DbBatch = 14.70ms，
        // 逐条复用命令 = 52.18ms（3.55×）。走 ADO.NET 通用 DbBatch 抽象（.NET 7+
        // DbConnection.CreateBatch），Core 不依赖任何驱动类型；驱动不支持的方言
        // 由 dialect 判据留在逐条路径（G31 方言感知）。
        // PG-7（2026-09-27 移植）：探针二十八实测 PG 远程库同族对照 = **7.85×**
        // （200 命令逐条 118.7ms vs DbBatch100/批 15.1ms）——PG 远程 RTT（~556µs）
        // 高于本地 MySQL，打包省得更多。
        // SQLite 不做：探针二十五实测进程内无 RTT，DbBatch vs 逐条 = 1.02~1.04×
        // （省不下命令对象/状态机之外的任何成本），平移收益为噪声。
        if (SupportsBatchedPooledUpdate)
        {
            return await ExecuteBulkUpdatePooledWithBatchAsync(
                new PooledBatchUpdateInput<T>(
                    entities, metadata, valuesBinder, tran, updateSql, pool, paramsPerRow),
                increments, ct).ConfigureAwait(false);
        }

        long total = 0;
        for (int i = 0; i < entities.Count; i++)
        {
            // 只写 Value——零 CreateParameter（P0-1 的核心）
            valuesBinder(pool, entities[i], 0);

            int affectedRows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (metadata.IncrementVersion is { } incrementVersion)
            {
                if (affectedRows == 0)
                    throw new ConcurrencyConflictException(
                        $"Entity '{typeof(T).Name}' was modified by another transaction.");
                if (affectedRows != 1)
                    throw new InvalidOperationException(
                        $"Concurrency update for '{typeof(T).Name}' affected {affectedRows} rows.");
                // 闭包必须捕获本次迭代的实体而非循环变量 i——lambda 在提交成功后统一执行，
                // 届时 i 已越界（ITM-556 的延迟回放语义）
                T entity = entities[i];
                increments.Add(() => incrementVersion(entity));
            }
            total += affectedRows;
        }
        return (total, increments);
    }

    /// <summary>MySQL-8/PG-7 移植判据：仅 MySQL/PG dialect 走 DbBatch 批路径。
    /// 抽成属性而非内联 `is X or Y`——内联 or 模式的分支会把宿主方法的认知复杂度
    /// 推过 S3776 的 15 线；集中一处也便于后续方言加入时单点维护。</summary>
    private static bool SupportsBatchedPooledUpdate
        => TProvider.Dialect is SqlDialect.MySql or SqlDialect.PostgreSql;

    /// <summary>MySQL-8/PG-7：DbBatch 打包的池化逐条 UPDATE（MySQL/PG dialect；SQLite 逐条——无 RTT 无收益）。
    /// <para><b>打包粒度</b>：<see cref="BatchedUpdateCommandsPerRound"/> 条命令一个 DbBatch
    /// （一次协议往返）。粒度取 100 的实测依据：探针十六 100 命令 = 14.70ms，且限制单批
    /// 报文在 max_allowed_packet 常见默认（每命令 UPDATE ≈ 1KB 量级 → 100KB/批）内。</para>
    /// <para><b>乐观锁批内判定</b>：DbBatch.ExecuteNonQueryAsync 返回批内受影响行数总和，
    /// 不按命令拆分。单行 UPDATE（WHERE pk 唯一）每行恰 1 行，故：
    /// 总和 == 批大小 → 全成功；总和 &lt; 批大小 → 存在 0 行匹配（乐观锁冲突）；
    /// 总和 &gt; 批大小 → 存在多行匹配（WHERE 语义被破坏）。与逐条路径的抛异常类型一致
    /// （异常消息本就不含行号，语义等价）。</para>
    /// <para><b>延迟 version 回填</b>：与逐条路径同口径——仅批成功后把整批实体的 increment
    /// 加入待回放列表（ITM-556：中途冲突整批回滚，已"成功"行的内存状态与 DB 保持一致）。</para></summary>
    private const int BatchedUpdateCommandsPerRound = 100;

    /// <summary>MySQL-8/PG-7 批路径的上下文聚合（S107）——池化逐条 UPDATE 的全部输入。</summary>
    private readonly record struct PooledBatchUpdateInput<T>(
        IReadOnlyList<T> Entities,
        CrudMetadata Metadata,
        Action<DbParameter[], object, int> ValuesBinder,
        DbTransaction Tran,
        string UpdateSql,
        DbParameter[] Pool,
        int ParamsPerRow)
        where T : class, new();

    private async ValueTask<(long Total, List<Action> Increments)> ExecuteBulkUpdatePooledWithBatchAsync<T>(
        PooledBatchUpdateInput<T> input, List<Action> increments, CancellationToken ct)
        where T : class, new()
    {
        long total = 0;
        Action<object>? incrementVersion = input.Metadata.IncrementVersion;

        for (int start = 0; start < input.Entities.Count; start += BatchedUpdateCommandsPerRound)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + BatchedUpdateCommandsPerRound, input.Entities.Count);
            int batchLen = end - start;

            // 每批新建 DbBatch（命令集合逐批填充）。参数对象逐命令新建——
            // DbBatchCommand.Parameters 是每命令独立集合，无法跨命令共享池；
            // 探针十六已验证"逐命令新建参数 + 单次往返"净收益仍 3.55×。
            DbConnection batchConn = input.Tran.Connection ?? throw new InvalidOperationException(
                "Batch UPDATE requires the transaction's connection.");
            using DbBatch batch = batchConn.CreateBatch();
            batch.Transaction = input.Tran;
            // DbBatch.Timeout 是 int 秒（与 DbCommand.CommandTimeout 同口径）；
            // ≤ 0 = 全库 Zero 无限等待契约。
            batch.Timeout = _options.CommandTimeoutSeconds;

            for (int i = start; i < end; i++)
            {
                var batchCommand = batch.CreateBatchCommand();
                batchCommand.CommandText = input.UpdateSql;
                BindIntoBatchCommand(batchCommand, input.Pool, input.ValuesBinder, input.Entities[i]);
                batch.BatchCommands.Add(batchCommand);
            }

            int affectedRows = await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            total += affectedRows;

            // 批总和判定 + 延迟回填登记（见方法 summary 与乐观锁语义说明）
            if (incrementVersion is not null)
            {
                VerifyBatchConcurrencyRows<T>(affectedRows, batchLen, start / BatchedUpdateCommandsPerRound);
            }
            if (incrementVersion is not null)
            {
                for (int i = start; i < end; i++)
                {
                    T entity = input.Entities[i];
                    increments.Add(() => incrementVersion(entity));
                }
            }
        }
        return (total, increments);
    }

    /// <summary>乐观锁批总和判定——单行 UPDATE（WHERE pk 唯一）每行恰 1 行：
    /// 总和 &lt; 批大小 = 存在 0 行匹配（乐观锁冲突）；总和 &gt; 批大小 = 存在多行匹配
    /// （WHERE 语义被破坏）。异常类型与逐条路径一致（消息本就不含具体行号）。</summary>
    private static void VerifyBatchConcurrencyRows<T>(int affectedRows, int batchLen, int batchIndex)
        where T : class, new()
    {
        if (affectedRows < batchLen)
            throw new ConcurrencyConflictException(
                $"Entity '{typeof(T).Name}' was modified by another transaction "
                + $"(batch {batchIndex}: {affectedRows} of {batchLen} rows matched).");
        if (affectedRows > batchLen)
            throw new InvalidOperationException(
                $"Concurrency update for '{typeof(T).Name}' affected {affectedRows} rows in a "
                + $"{batchLen}-command batch.");
    }

    /// <summary>把池内一行参数值拷进 DbBatchCommand——valuesBinder 先把值写进池对象
    /// （零 CreateParameter 的取值路径照旧），再把池内全部参数（行参数 + 池尾会话级槽）
    /// 显式拷进命令自己的参数集合（DbBatchCommand.Parameters 是每命令独立集合，
    /// 池对象不能被多个命令集合共同持有——值会互相覆盖）。
    /// <para><b>internal for testing</b>：本方法是 DbBatch 路径的参数拷贝契约点
    /// （拷贝范围 = 池全长，见 B8 修复注释）；PG/MySQL 真库端到端在
    /// Integration.Tests 的 BulkUpdateTenantRoutingTests，本地 Core 单测
    /// （BulkUpdateBatchReuseTests.BindIntoBatchCommand_CopiesRowParamsPlusTenantSlot）
    /// 以方言夹具锁定同一契约（与 AttachParameters 的 internal 先例同口径）。</para></summary>
    internal static void BindIntoBatchCommand<T>(
        DbBatchCommand batchCommand, DbParameter[] pool,
        Action<DbParameter[], object, int> valuesBinder, T entity)
    {
        valuesBinder(pool, entity!, 0);
        // B8 轮修复（2026-10-01）：拷贝范围 = 池全长（paramsPerRow + 租户槽）——
        // 池布局为「行参数 × paramsPerRow, 租户参数?」（CreateParameterArray 尾部追加），
        // 原实现只拷前 paramsPerRow 个，租户实体的 UPDATE 后缀引用 @__tenant0 而
        // DbBatch 命令缺该参数：PG 42703 响亮失败、MySQL 拒绝未定义参数（两方言均经
        // 真库 Integration BulkUpdateTenantRoutingTests 实测锁定）。非租户实体池长
        // 恒等于 paramsPerRow，本修复对其为零变化。
        int totalParams = pool.Length;
        for (int c = 0; c < totalParams; c++)
        {
            DbParameter target = batchCommand.CreateParameter();
            DbParameter source = pool[c];
            target.ParameterName = source.ParameterName;
            target.DbType = source.DbType;
            target.Value = source.Value;
            batchCommand.Parameters.Add(target);
        }
    }

    /// <summary>P0-1 的旧版模型程序集回退路径：无 <see cref="CrudMetadata.BindUpdateValues"/>
    /// 时逐行 <c>UpdateCoreAsync</c>（每行新建命令与参数）。语义与池化路径逐位一致，
    /// 参数创建量回到改前水平——这是旧程序集的既有成本，不是回归。</summary>
    private async ValueTask<(long Total, List<Action> Increments)> ExecuteBulkUpdateLegacyAsync<T>(
        IReadOnlyList<T> entities, object? operationOwner, CancellationToken token)
        where T : class, new()
    {
        long total = 0;
        List<Action> increments = [];
        foreach (T entity in entities)
        {
            total += await UpdateCoreAsync(
                entity, operationOwner, token, increments).ConfigureAwait(false);
        }
        return (total, increments);
    }

    /// <summary>v5.0 阶段 4.3b：批量更新（单语句批量 UPDATE，方案 Y 严格版）。
    /// <para><b>与 <see cref="BulkUpdateAsync{T}"/> 的差异</b>：本方法走批量 SQL
    /// （PG: UPDATE FROM VALUES；MySQL: CASE WHEN），单次 RTT 完成 N 行更新。</para>
    /// <para><b>SQLite 方言感知回退</b>：SQLite 的 CASE WHEN 大批量比逐条慢 6.4x（SQL 解析开销），
    /// 实测验证 SQLite 逐条 UPDATE 已是最优路径。SQLite 调用本方法自动回退到
    /// <see cref="BulkUpdateAsync{T}"/>（版本化单语句或逐条，乐观锁语义保留），调用方无需感知。</para>
    /// <para><b>乐观锁不支持</b>：带 <c>[ConcurrencyCheck]</c> 的实体调用本方法抛
    /// <see cref="NotSupportedException"/>（PG/MySQL 路径）；SQLite 回退到 BulkUpdateAsync 则支持。
    /// 版本化实体请用 <see cref="BulkUpdateAsync{T}"/>：它在能力支持时自动路由到版本化单语句
    /// 批量更新（逐行 version 谓词），否则回退逐条。</para>
    /// <para><b>输入不可变约束</b>：调用方在方法返回前不得修改 <paramref name="entities"/> 集合
    /// （与 BulkInsertAsync / BulkUpdateAsync 的 IReadOnlyList&lt;T&gt; 契约一致）。</para>
    /// <para><b>租户过滤</b>：自动追加 <c>AND tenant_id = @p</c>（与 BulkUpdateAsync 对齐）。</para>
    /// <para><b>参数上限</b>：按驱动上限分批执行（PG/MySQL 65535；SQLite 走上文逐条回退路径、不分批）。物理约束非性能阈值。</para></summary>
    public async ValueTask<long> BulkUpdateBatchAsync<T>(
        IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        // R8：单次注册表快照——原实现入口先读 CurrentState 检查、随后又独立读一次取元数据
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated CRUD.");
        // r12-B1（D3 残留族）：空短路后置（三方言/两路径统一口径——SQLite 回退路径同样
        // 不应使未注册类型+空列表静默成功）
        if (entities.Count == 0) return 0;

        // v5.0 SQLite 方言感知回退：CASE WHEN 在 SQLite 上比逐条慢 6.4x（实测验证）。
        // 直接调内部逐条路径（不复用 BulkUpdateAsync 的 EnterOperation——会双重锁定）。
        if (TProvider.Dialect == SqlDialect.Sqlite)
            return await ExecuteBulkUpdateRowByRowAsync(entities, operation.Owner, ct).ConfigureAwait(false);

        if (!state._tableNames.TryGetValue(typeof(T), out string? tableName))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        // 乐观锁实体拒绝：本显式单语句 API 不承载逐行版本谓词。版本化能力经
        // BulkUpdateAsync 的自动路由提供（PG UNNEST / MySQL JoinValuesRow），
        // 生成物能力不全时回退逐条。
        if (metadata.IncrementVersion is not null)
            throw new NotSupportedException(
                $"BulkUpdateBatchAsync cannot honor [ConcurrencyCheck] on '{typeof(T).Name}'; " +
                "batch UPDATE cannot express per-row version matching. " +
                "Use BulkUpdateAsync (auto-routed versioned bulk update) instead.");

        // 准备批量上下文：SET 列集、引号包裹标识符、租户过滤标记。
        BatchUpdateContext ctx = PrepareBatchUpdateContext<T>(state, metadata, tableName, entities[0]);
        // UNNEST 阶段 B（2026-10-02）：PG 数组形态——两个 UPDATE 入口共用同一能力检测与执行体
        // （此前只有 BulkUpdateAsync 的自动路由走数组，本公开入口仍走 VALUES，形成
        // 「同操作两形态」的不对称：B120 族）。乐观锁实体已在上方拒绝，列序与批量 UPDATE
        // 的 SET 列 + 主键一致。
        if (UseUnnestArraysForUpdate<T>(metadata, out IReadOnlyList<Type>? arrayElementTypes))
        {
            return await RunInTransactionScopeAsync(
                operation.Owner,
                (tran, token) => ExecuteBulkUpdateArrayBatchesAsync(
                    entities, metadata, ctx, tran, arrayElementTypes!, token),
                ct).ConfigureAwait(false);
        }
        // ITM-640：SQLite 已在上方回退逐条路径，此处恒非 SQLite——原三元的 999 分支不可达。
        const int driverLimit = SqlLimits.MaxBindParameters;
        int tenantParams = ctx.HasTenantFilter ? 1 : 0;
        // MySQL-7：MySQL 按服务端版本选 UPDATE JOIN VALUES ROW 形态（8.75×）+ 对应批宽
        BatchUpdateSqlBuilder.BatchUpdateForm form = await ResolveMySqlUpdateFormAsync(ct).ConfigureAwait(false);
        // BULK-001（2026-09-23）：再取单批行数上限——本路径在 MySQL 走 CASE WHEN 形态，
        // 文本按 O(行数×列数) 增长，仅受参数上限约束会生成 MB 级单语句。
        int rowsPerBatch = Math.Min(
            Math.Max(1, (driverLimit - tenantParams) / (ctx.SetColumnCount + 1)),
            MaxRowsPerUpdateBatchFor(form));

        // v5.4 精炼 L1：事务骨架收敛至 RunInTransactionScopeAsync。
        return await RunInTransactionScopeAsync(
            operation.Owner,
            async (tran, token) =>
                await ExecuteBulkUpdateBatchesAsync(entities, metadata, ctx, tran, rowsPerBatch, form, token)
                    .ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    /// <summary>准备批量 UPDATE 上下文：SET 列集取自 CrudMetadata 真源、引号包裹、租户过滤。
    /// probe 命令验证 BindUpdate 参数序与元数据列集一致（ITM-642——原实现解析生成 SQL 文本
    /// 反解列名，含逗号标识符被 Split(',') 错切、表名内含 " SET " 亦会误判）。
    /// <para>B9（2026-10-01 全 API 逐项轮）：probe 结果按 (Type, Dialect) 经
    /// <see cref="DataSessionCache.BatchUpdateContextCache"/> 缓存——生成器三处一致性是
    /// 编译期事实（同一二进制内恒定），每进程每类型验证一次即可。租户位（会话态）不缓存，
    /// 每次经 HasTenantFilter 现算。</para></summary>
    private BatchUpdateContext PrepareBatchUpdateContext<T>(
        PalORM_Runtime.RuntimeRegistryState state, CrudMetadata metadata,
        string tableName, T firstEntity)
        where T : class, new()
    {
        // ITM-642：SET 列集直接消费生成器发射的 UpdateColumns（与 BuildUpdateSql/BindUpdate
        // 同源同序，ITM-552 单一谓词）——不再解析生成 SQL 文本反解列名。
        int setColumnCount = metadata.UpdateColumns.Count;
        if (setColumnCount <= 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no updatable columns.");
        if (!DataSessionCache.BatchUpdateContextCache.TryGetValue(
                (typeof(T), TProvider.Dialect), out var cached))
        {
            // probe 提取参数总数，作为生成器三处（SQL/Bind/元数据）漂移的运行时哨兵
            // （每进程每类型一次，见缓存 doc）。
            // ITM-792(r21) 订正：本方法为同步（BindUpdate 无 IO）——await using 不适用，
            // 用 try/finally 显式 Dispose 保证异常路径释放（与 peer 的异步释放语义等价）。
            DbCommand probe = CreateCommand();
            try
            {
                metadata.BindUpdate(probe, firstEntity);
                int totalParams = probe.Parameters.Count;
                // 并发令牌列不在 UpdateColumns 里（SET 列表排除），但 BindUpdate 的末位参数
                // 是版本令牌（单行 UPDATE 的 WHERE version=@p 用）。版本化单语句路径也经
                // 本哨兵，故期望值按元数据是否带并发令牌 +1。
                int expectedParams = setColumnCount + 1
                    + (metadata.IncrementVersion is not null ? 1 : 0);
                if (totalParams != expectedParams)
                    throw new InvalidOperationException(
                        $"Type '{typeof(T).Name}' BindUpdate produced {totalParams} parameters but metadata " +
                        $"declares {setColumnCount} update columns (+1 primary key"
                        + (metadata.IncrementVersion is not null ? " +1 concurrency token" : "")
                        + "). Recompile the model assembly.");
                string[] setColumns = metadata.UpdateColumns
                    .Select(TProvider.QuoteIdentifier).ToArray();
                string quotedTable = TProvider.QuoteIdentifier(tableName);
                // r19/ITM-684：缺 PkColumns 不再静默回退主键 "id"——错列更新比明确失败更危险。
                // Register 的必填键校验使此分支经公共 API 不可达（防御纵深），但与 GetPkColumn/
                // ITM-672 缺键拒绝族保持同口径：旧生成器/手工片段必须显式失败。
                string quotedPk = state._pkColumns.TryGetValue(typeof(T), out string? pkCol)
                    ? TProvider.QuoteIdentifier(pkCol)
                    : throw new InvalidOperationException(
                        $"Type '{typeof(T).Name}' has no generated primary key metadata; " +
                        "recompile the model assembly against the current PalORM source generator.");
                cached = (setColumns, quotedTable, quotedPk);
                DataSessionCache.BatchUpdateContextCache.TryAdd((typeof(T), TProvider.Dialect), cached);
            }
            finally
            {
                probe.Dispose();
            }
        }
        return new BatchUpdateContext(
            cached.SetColumns, cached.QuotedTable, cached.QuotedPk, HasTenantFilter<T>());
    }

    /// <summary><b>M1/L2</b>：批量 UPDATE 的分批执行内核——拥有批次循环，命令、参数池与
    /// SQL 文本在批间复用。
    /// <para><b>为什么收敛成单方法</b>：原实现是「调用方循环 + 每批一个 ExecuteBatchUpdateAsync」，
    /// 每批新建 DbCommand、每批重建 rowParamCount 个参数、每批重建一份逐位相同的 SQL 文本。
    /// INSERT 路径自 v4.6 起就有「命令跨批复用 + 满批参数池 + CommandText 仅批大小变化时重建」
    /// 三件套（<see cref="MultiValueBulkInsert.ExecuteBatchesAsync"/>），UPDATE 路径是同类未收敛点。
    /// 本方法把整套范式搬过来：命令与参数池在进入循环前建一次，逐批只写 <c>Value</c>；
    /// SQL 文本仅在批大小变化时重建（满批恒等，仅末批可能不同）。</para>
    /// <para><b>取值路径</b>：优先走生成器发射的 <see cref="CrudMetadata.BindUpdateValues"/>
    /// （零 CreateParameter）。旧版模型程序集该绑定器为 null 时回退 probe 取值，语义不变、
    /// 参数创建量回到改前水平。</para>
    /// <para>参数名与顺序与 <see cref="BatchUpdateSqlBuilder"/> 的占位符逐位对应，由
    /// <c>BatchUpdateParameterContractTests</c> 锁定跨 Provider 契约。</para></summary>
    private async ValueTask<long> ExecuteBulkUpdateBatchesAsync<T>(
        IReadOnlyList<T> entities, CrudMetadata metadata, BatchUpdateContext ctx,
        DbTransaction tran, int rowsPerBatch,
        BatchUpdateSqlBuilder.BatchUpdateForm form, CancellationToken ct)
        where T : class, new()
    {
        int setColumnCount = ctx.SetColumnCount;
        int paramsPerRow = setColumnCount + 1;
        int tenantParams = ctx.HasTenantFilter ? 1 : 0;
        // 池按"本调用实际出现的最大批"建——输入不足一个满批时不为不存在的行预留
        int poolRows = Math.Min(rowsPerBatch, entities.Count);
        int poolRowParamCount = poolRows * paramsPerRow;

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;

        // 参数池与命令参数集合一次建好；末批缩短时只收敛集合、不新建参数对象
        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterArray(
            poolRowParamCount, ctx.HasTenantFilter, _tenantParameterName, _tenantId,
            TProvider.CreateParameter);
        // N4（2026-10-04 全量复读）：同 ExecuteBulkUpdatePooledAsync——可空/byte[] 列的 DbType
        // 提示一次性建立（原由 BindUpdateValues 每行每列重写恒定值）。
        metadata.InitUpdateParameters?.Invoke(pool, 0);
        AttachParameters(cmd, pool, poolRowParamCount, poolRowParamCount, tenantParams);

        Action<DbParameter[], object, int>? valuesBinder = metadata.BindUpdateValues;
        string? lastBatchSql = null;
        int lastBatchLength = -1;
        long totalAffected = 0;

        for (int start = 0; start < entities.Count; start += rowsPerBatch)
        {
            int end = Math.Min(start + rowsPerBatch, entities.Count);
            int batchLen = end - start;
            int rowParamCount = batchLen * paramsPerRow;

            if (batchLen != lastBatchLength)
            {
                lastBatchSql = BatchUpdateSqlBuilder.Build(
                    TProvider.Dialect, ctx.QuotedTable, ctx.QuotedPk, ctx.SetColumns,
                    batchLen, ctx.HasTenantFilter, _tenantParameterName, form);
                lastBatchLength = batchLen;
                AttachParameters(cmd, pool, rowParamCount, poolRowParamCount, tenantParams);
            }
            cmd.CommandText = lastBatchSql!;

            if (valuesBinder is not null)
            {
                // 零分配路径：只写 Value
                for (int i = start; i < end; i++)
                    valuesBinder(pool, entities[i], (i - start) * paramsPerRow);
            }
            else
            {
                await BindRowValuesViaProbeAsync(
                    metadata, pool, entities, start, end, paramsPerRow).ConfigureAwait(false);
            }

            totalAffected += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return totalAffected;
    }

    /// <summary>UNNEST 阶段 B：批量 UPDATE 的数组形态能力检测。
    /// <para>两条件全真：生成物提供逐列数组填充器/元素类型表/数组分配器（旧生成器为 null）、
    /// Provider 接受<b>全部列的</b>元素类型的数组参数（<see cref="IDbProvider.CreateTypedArrayParameter"/>
    /// 对每列非 null）。任一列不支持即整体回退 VALUES 形态——不做"部分列走数组、部分列逐个"的混合，
    /// 那种半形态是不对称缺陷的温床（B120 族）。</para>
    /// <para><b>能力检测而非方言枚举</b>：判据是 Provider 是否接受这些元素类型
    /// （与阶段 A 的 BulkDelete 同纪律）。探测数组由生成物的分配器给出（AOT 安全，不用
    /// <c>Array.CreateInstance(Type, …)</c>）。</para></summary>
    private static bool UseUnnestArraysForUpdate<T>(
        CrudMetadata metadata, out IReadOnlyList<Type>? arrayElementTypes)
        where T : class, new()
    {
        arrayElementTypes = null;
        // 方言前置（2026-10-06，10-05 审计 P3）：UNNEST 是 PG 专有语法（BatchUpdateSqlBuilder
        // 的 usesUnnestArrays 同判定），能力检测与 SQL 构造必须结构同源——否则非 PG 方言的
        // 第三方 Provider 实现数组能力时会绑 @u* 数组参数却得到 VALUES 形态 SQL（参数错位）
        if (TProvider.Dialect != SqlDialect.PostgreSql)
            return false;
        if (metadata.FillUpdateColumnArrays is null
            || metadata.CreateUpdateColumnArrays is null
            || metadata.UpdateColumnArrayElementTypes is not { Count: > 0 } elementTypes)
        {
            SqlSlots<T>.UpdateArraySupported = false;
            return false;
        }
        // S1：生成物与 Provider 能力恒定，每 (TProvider, T) 探测一次。
        if (SqlSlots<T>.UpdateArraySupported is { } known)
        {
            arrayElementTypes = known ? elementTypes : null;
            return known;
        }
        Array[] probes = metadata.CreateUpdateColumnArrays(1);
        for (int c = 0; c < elementTypes.Count; c++)
        {
            if (TProvider.CreateTypedArrayParameter("@probe", probes[c], elementTypes[c]) is null)
            {
                SqlSlots<T>.UpdateArraySupported = false;
                return false;
            }
        }
        SqlSlots<T>.UpdateArraySupported = true;
        arrayElementTypes = elementTypes;
        return true;
    }

    /// <summary>UNNEST 阶段 B：数组形态的批量 UPDATE 主体——每批把实体区间按列填进数组，一列一个参数，
    /// 语句文本跨批恒定（只依赖列数，与批宽无关）。
    /// <para>列数组按批容量预建一次、批间复用（与既有参数池的跨批复用同纪律 M1/PERF-004）；
    /// 末批缩短时用短数组——UNNEST 按传入数组的实际长度展开，长度必须精确等于本批行数。</para></summary>
    private async Task<long> ExecuteBulkUpdateArrayBatchesAsync<T>(
        IReadOnlyList<T> entities,
        CrudMetadata metadata,
        BatchUpdateContext ctx,
        DbTransaction? tran,
        IReadOnlyList<Type> arrayElementTypes,
        CancellationToken ct)
        where T : class, new()
    {
        Action<IReadOnlyList<object>, int, int, Array[], int> fillArrays = metadata.FillUpdateColumnArrays!;
        Func<int, Array[]> createArrays = metadata.CreateUpdateColumnArrays!;
        int columnCount = arrayElementTypes.Count;
        // 契约断言（2026-10-02）：生成物的数组列序 = SET 列 + 主键，与数组形态 SQL 的
        // UNNEST(@u0…@u{SetColumnCount}) 参数个数逐位对应。不符即生成器与 Core 漂移——
        // 明确抛错而非静默少绑（历史上并发令牌列曾被发射进数组，个数多 1，一旦前置条件
        // 放宽就会以「参数未被使用」的驱动异常暴露，错误信息不指向根因）。
        // 能力检测已排除该情形，此处是防未来漂移的哨兵（与 PrepareBatchUpdateContext 的
        // probe 哨兵同范式）。
        if (columnCount != ctx.SetColumnCount + 1)
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}': UNNEST array form expects {ctx.SetColumnCount + 1} column arrays "
                + $"(SET columns + primary key) but the generated filler provides {columnCount}. "
                + "The generator and batch UPDATE SQL disagree; recompile the model assembly "
                + "(see CommandFactoryEmitter.UpdateArrayColumns).");
        }
        // 数组形态不受"语句内参数个数"约束（整批每列一个参数）。深挖轮批宽提升——
        // 5000→20000 实测 -36%（探针 B，见 UnnestArraysMaxRowsPerBatch）。
        int batchSize = UnnestArraysMaxRowsPerBatch;
        int fullBatchLen = Math.Min(batchSize, entities.Count);

        // 满批数组预建一次批间复用；末批不足满批时单独建一组短数组（UNNEST 按数组实际长度展开，
        // 长度必须精确等于本批行数——复用长数组会把上一批的残留行一并更新）
        int lastBatchLen = entities.Count % fullBatchLen;
        Array[] fullArrays = createArrays(fullBatchLen);
        Array[] shortArrays = lastBatchLen == 0 ? fullArrays : createArrays(lastBatchLen);

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        cmd.CommandText = BatchUpdateSqlBuilder.Build(
            TProvider.Dialect, ctx.QuotedTable, ctx.QuotedPk, ctx.SetColumns,
            rowCount: 1, hasTenantFilter: ctx.HasTenantFilter,
            tenantParameterName: _tenantParameterName,
            form: BatchUpdateSqlBuilder.BatchUpdateForm.UnnestArrays);

        // 参数对象建一次（每列一个），批间只换 Value
        var parameters = new DbParameter[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            parameters[c] = TProvider.CreateParameter(
                BatchUpdateSqlBuilder.UnnestColumnParameterName(c), DBNull.Value);
            cmd.Parameters.Add(parameters[c]);
        }
        BindDefaultFilterParameters<T>(cmd);

        long totalAffected = 0;
        for (int start = 0; start < entities.Count; start += batchSize)
        {
            int batchLen = Math.Min(batchSize, entities.Count - start);
            Array[] columns = batchLen == fullBatchLen ? fullArrays : shortArrays;
            fillArrays(entities, start, batchLen, columns, 0);
            for (int c = 0; c < columnCount; c++)
            {
                // ITM-894：能力检测与执行不一致（探测非 null 而满批 null）时给显式诊断，
                // 对齐 BulkDelete BuildArrayParameter 的消息形态（原 null 断言直接 NRE 无信息）
                parameters[c].Value = (TProvider.CreateTypedArrayParameter(
                    BatchUpdateSqlBuilder.UnnestColumnParameterName(c),
                    columns[c], arrayElementTypes[c])
                    ?? throw new InvalidOperationException(
                        $"Provider '{TProvider.Name}' returned a null typed array parameter for "
                        + $"element type '{arrayElementTypes[c].Name}' (batch length {batchLen}); "
                        + "the capability probe and the execution path disagree.")).Value;
            }
            totalAffected += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return totalAffected;
    }

    /// <summary>版本化单语句批量更新的路由判定与执行（PG UNNEST / MySQL JoinValuesRow）。
    /// <para><b>契约</b>：冲突检测保持计数级（与现行 100 命令 DbBatch 路径同型），异常类型
    /// 与消息前缀不变；行级归属留到可选诊断能力。目标行只更新一次（PG 官方文档与 MySQL 8.4
    /// 实测一致），重复主键使受影响数小于输入数，按冲突抛出。</para>
    /// <para><b>版本回填</b>：语句批全部成功后、事务作用域返回后统一递增全部实体。中途冲突
    /// 抛异常，作用域回滚，递增不执行；外部事务形态保持“方法返回前回填”的既有语义。</para>
    /// <returns>成功返回受影响行数；能力不全（旧生成器/SQLite/低版本 MySQL/无数组能力）返回
    /// null，调用方回退逐条路径。</returns></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "PG/MySQL 两条能力门禁与执行分支在同一路由方法内并排；拆分需跨方法传递"
            + "实体列表、注册快照与事务归属。")]
    private async ValueTask<long?> TryExecuteVersionedBulkUpdateAsync<T>(
        IReadOnlyList<T> entities,
        PalORM_Runtime.RuntimeRegistryState state,
        CrudMetadata metadata,
        object? operationOwner,
        CancellationToken ct)
        where T : class, new()
    {
        EntityRuntimeInfo info = GetEntityInfoForCrud<T>();
        if (info.ConcurrencyColumn is null || metadata.BindUpdateValues is null)
            return null;
        if (!state._tableNames.TryGetValue(typeof(T), out string? tableName))
            return null;

        string quotedVersionColumn = TProvider.QuoteIdentifier(info.ConcurrencyColumn);
        BatchUpdateContext ctx = PrepareBatchUpdateContext<T>(state, metadata, tableName, entities[0]);

        long total;
        if (TProvider.Dialect == SqlDialect.PostgreSql)
        {
            if (info.FillVersionArray is null || info.CreateVersionArray is null
                || info.VersionArrayElementType is null)
            {
                return null;
            }
            if (!UseUnnestArraysForUpdate<T>(metadata, out IReadOnlyList<Type>? arrayElementTypes))
                return null;
            total = await RunInTransactionScopeAsync(
                operationOwner,
                (tran, token) => ExecuteBulkUpdateArrayBatchesVersionedAsync(
                    entities, metadata, ctx, tran, arrayElementTypes!, quotedVersionColumn,
                    info.CreateVersionArray, info.FillVersionArray, info.VersionArrayElementType, token),
                ct).ConfigureAwait(false);
        }
        else if (TProvider.Dialect == SqlDialect.MySql)
        {
            if (info.BindVersionValue is null)
                return null;
            BatchUpdateSqlBuilder.BatchUpdateForm form =
                await ResolveMySqlUpdateFormAsync(ct).ConfigureAwait(false);
            if (form != BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow)
                return null;
            int tenantParams = ctx.HasTenantFilter ? 1 : 0;
            int rowsPerBatch = Math.Min(
                Math.Max(1, (SqlLimits.MaxBindParametersFor(TProvider.Dialect) - tenantParams)
                    / (ctx.SetColumnCount + 2)),
                MaxRowsPerUpdateBatchFor(form));
            total = await RunInTransactionScopeAsync(
                operationOwner,
                (tran, token) => ExecuteBulkUpdateBatchesVersionedAsync(
                    entities, metadata, ctx, tran, rowsPerBatch, form, quotedVersionColumn,
                    info.BindVersionValue, token),
                ct).ConfigureAwait(false);
        }
        else
        {
            return null;
        }

        ApplyVersionIncrements(metadata, entities);
        return total;
    }

    /// <summary>事务成功后统一回填内存 version。单语句路径全部批都成功才到达此处，故可整列表
    /// 递增；逐条路径的 per-batch 暂存语义不变（中途冲突时递增列表随异常丢弃）。</summary>
    private static void ApplyVersionIncrements<T>(CrudMetadata metadata, IReadOnlyList<T> entities)
        where T : class, new()
    {
        if (metadata.IncrementVersion is not { } increment)
            return;
        for (int i = 0; i < entities.Count; i++)
            increment(entities[i]);
    }

    /// <summary>版本化 PG UNNEST 数组形态内核：每批填 SET 列 + 主键 + 版本列数组，语句文本跨批
    /// 恒定。受影响数与批大小不等即按冲突/语义破坏抛出（见 <see cref="VerifyBatchConcurrencyRows{T}"/>）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "数组预建/填充、版本数组、参数赋值与逐批冲突校验在同一内核内顺序展开；"
            + "拆分会让批次索引与数组长度契约跨方法传递。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S107:MethodsShouldNotHaveTooManyParameters",
        Justification = "内核输入 = 实体、元数据、批上下文、事务、数组能力三件套、版本列名与取消令牌；"
            + "聚合对象会为每次批量更新多一次分配。")]
    private async Task<long> ExecuteBulkUpdateArrayBatchesVersionedAsync<T>(
        IReadOnlyList<T> entities,
        CrudMetadata metadata,
        BatchUpdateContext ctx,
        DbTransaction? tran,
        IReadOnlyList<Type> arrayElementTypes,
        string quotedVersionColumn,
        Func<int, Array> createVersionArray,
        Action<IReadOnlyList<object>, int, int, Array> fillVersionArray,
        Type versionElementType,
        CancellationToken ct)
        where T : class, new()
    {
        Action<IReadOnlyList<object>, int, int, Array[], int> fillArrays = metadata.FillUpdateColumnArrays!;
        Func<int, Array[]> createArrays = metadata.CreateUpdateColumnArrays!;
        int columnCount = arrayElementTypes.Count;
        if (columnCount != ctx.SetColumnCount + 1)
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}': UNNEST array form expects {ctx.SetColumnCount + 1} column arrays "
                + $"(SET columns + primary key) but the generated filler provides {columnCount}. "
                + "The generator and batch UPDATE SQL disagree; recompile the model assembly.");
        }

        int batchSize = UnnestArraysMaxRowsPerBatch;
        int fullBatchLen = Math.Min(batchSize, entities.Count);
        int lastBatchLen = entities.Count % fullBatchLen;
        Array[] fullArrays = createArrays(fullBatchLen);
        Array[] shortArrays = lastBatchLen == 0 ? fullArrays : createArrays(lastBatchLen);
        Array fullVersionArray = createVersionArray(fullBatchLen);
        Array shortVersionArray = lastBatchLen == 0 ? fullVersionArray : createVersionArray(lastBatchLen);

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        cmd.CommandText = BatchUpdateSqlBuilder.Build(
            TProvider.Dialect, ctx.QuotedTable, ctx.QuotedPk, ctx.SetColumns,
            rowCount: 1, hasTenantFilter: ctx.HasTenantFilter,
            tenantParameterName: _tenantParameterName,
            form: BatchUpdateSqlBuilder.BatchUpdateForm.UnnestArrays,
            quotedConcurrencyColumn: quotedVersionColumn);

        int parameterCount = columnCount + 1;
        var parameters = new DbParameter[parameterCount];
        for (int c = 0; c < parameterCount; c++)
        {
            parameters[c] = TProvider.CreateParameter(
                BatchUpdateSqlBuilder.UnnestColumnParameterName(c), DBNull.Value);
            cmd.Parameters.Add(parameters[c]);
        }
        BindDefaultFilterParameters<T>(cmd);

        long totalAffected = 0;
        int batchIndex = 0;
        for (int start = 0; start < entities.Count; start += batchSize, batchIndex++)
        {
            int batchLen = Math.Min(batchSize, entities.Count - start);
            Array[] columns = batchLen == fullBatchLen ? fullArrays : shortArrays;
            Array versionArray = batchLen == fullBatchLen ? fullVersionArray : shortVersionArray;
            fillArrays(entities, start, batchLen, columns, 0);
            fillVersionArray(entities, start, batchLen, versionArray);
            for (int c = 0; c < columnCount; c++)
            {
                parameters[c].Value = (TProvider.CreateTypedArrayParameter(
                    BatchUpdateSqlBuilder.UnnestColumnParameterName(c),
                    columns[c], arrayElementTypes[c])
                    ?? throw new InvalidOperationException(
                        $"Provider '{TProvider.Name}' returned a null typed array parameter for "
                        + $"element type '{arrayElementTypes[c].Name}' (batch length {batchLen}); "
                        + "the capability probe and the execution path disagree.")).Value;
            }
            parameters[columnCount].Value = (TProvider.CreateTypedArrayParameter(
                BatchUpdateSqlBuilder.UnnestColumnParameterName(columnCount),
                versionArray, versionElementType)
                ?? throw new InvalidOperationException(
                    $"Provider '{TProvider.Name}' returned a null typed array parameter for "
                    + $"the concurrency token (element type '{versionElementType.Name}', "
                    + $"batch length {batchLen}); the capability probe and the execution path disagree.")).Value;

            int affectedRows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            totalAffected += affectedRows;
            VerifyBatchConcurrencyRows<T>(affectedRows, batchLen, batchIndex);
        }
        return totalAffected;
    }

    /// <summary>版本化 MySQL JoinValuesRow 内核：参数池按行布局 SET 列 + 主键 + 版本令牌，
    /// 每行只写 Value；受影响数按批校验。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S107:MethodsShouldNotHaveTooManyParameters",
        Justification = "内核输入 = 实体、元数据、批上下文、事务、批宽、形态、版本列名、版本绑定器"
            + "与取消令牌；聚合对象会为每次批量更新多一次分配。")]
    private async Task<long> ExecuteBulkUpdateBatchesVersionedAsync<T>(
        IReadOnlyList<T> entities,
        CrudMetadata metadata,
        BatchUpdateContext ctx,
        DbTransaction tran,
        int rowsPerBatch,
        BatchUpdateSqlBuilder.BatchUpdateForm form,
        string quotedVersionColumn,
        Action<DbParameter[], object, int> bindVersionValue,
        CancellationToken ct)
        where T : class, new()
    {
        int setColumnCount = ctx.SetColumnCount;
        int paramsPerRow = setColumnCount + 2;
        int tenantParams = ctx.HasTenantFilter ? 1 : 0;
        int poolRows = Math.Min(rowsPerBatch, entities.Count);
        int poolRowParamCount = poolRows * paramsPerRow;

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;

        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterArray(
            poolRowParamCount, ctx.HasTenantFilter, _tenantParameterName, _tenantId,
            TProvider.CreateParameter);
        metadata.InitUpdateParameters?.Invoke(pool, 0);
        AttachParameters(cmd, pool, poolRowParamCount, poolRowParamCount, tenantParams);

        Action<DbParameter[], object, int> valuesBinder = metadata.BindUpdateValues!;
        string? lastBatchSql = null;
        int lastBatchLength = -1;
        long totalAffected = 0;
        int batchIndex = 0;

        for (int start = 0; start < entities.Count; start += rowsPerBatch, batchIndex++)
        {
            int end = Math.Min(start + rowsPerBatch, entities.Count);
            int batchLen = end - start;
            int rowParamCount = batchLen * paramsPerRow;

            if (batchLen != lastBatchLength)
            {
                lastBatchSql = BatchUpdateSqlBuilder.Build(
                    TProvider.Dialect, ctx.QuotedTable, ctx.QuotedPk, ctx.SetColumns,
                    batchLen, ctx.HasTenantFilter, _tenantParameterName, form,
                    quotedConcurrencyColumn: quotedVersionColumn);
                lastBatchLength = batchLen;
                AttachParameters(cmd, pool, rowParamCount, poolRowParamCount, tenantParams);
            }
            cmd.CommandText = lastBatchSql!;

            for (int i = start; i < end; i++)
            {
                int offset = (i - start) * paramsPerRow;
                valuesBinder(pool, entities[i], offset);
                bindVersionValue(pool, entities[i], offset + setColumnCount + 1);
            }

            int affectedRows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            totalAffected += affectedRows;
            VerifyBatchConcurrencyRows<T>(affectedRows, batchLen, batchIndex);
        }
        return totalAffected;
    }

    /// <summary>MySQL-7：批量 UPDATE 形态判定——MySQL 方言按服务端版本选
    /// <see cref="BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow"/>（UPDATE JOIN
    /// table value constructor，MySQL 8.0.19+），否则 <see cref="BatchUpdateSqlBuilder.BatchUpdateForm.CaseWhen"/>。
    /// 非 MySQL 方言恒 CaseWhen（PG 走 FROM VALUES，SQLite 已在调用方回退逐条）。
    /// <para><b>版本探测</b>：连接串键控 + 60s TTL（EPSL 2026-10-11 审计 B 批 #2：原按连接
    /// 实例键控，per-op 会话形态每会话新包装永不命中，每次 BulkUpdate 重付 SELECT VERSION()
    /// 往返；与 local_infile 探测同轮迁 <see cref="CapabilityProbeCache{T}"/>——版本是
    /// 「同一连接串所指服务端」的事实，不是进程级可变状态）。
    /// 探测异常保守回退 CaseWhen（不把瞬时故障固化为新形态），TTL 让服务端升级在一分钟内生效。</para></summary>
    private async ValueTask<BatchUpdateSqlBuilder.BatchUpdateForm> ResolveMySqlUpdateFormAsync(CancellationToken ct)
    {
        if (TProvider.Dialect != SqlDialect.MySql)
            return BatchUpdateSqlBuilder.BatchUpdateForm.CaseWhen;

        try
        {
            return (await CapabilityProbeCache<MySqlUpdateFormCacheHolder.MySqlUpdateFormProbe>.GetOrProbeAsync(
                _conn.ConnectionString,
                MySqlUpdateFormProbeTtlMilliseconds,
                async token =>
                {
                    await using DbCommand cmd = CreateCommand();
                    cmd.CommandTimeout = 3;  // SELECT VERSION() 是即时查询，3 秒足够
                    cmd.CommandText = "SELECT VERSION()";
                    object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    bool supportsValuesRow = scalar is string version && ParseMySqlVersion(version) >= MySQL_8_0_19;
                    return new MySqlUpdateFormCacheHolder.MySqlUpdateFormProbe(
                        supportsValuesRow
                            ? BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow
                            : BatchUpdateSqlBuilder.BatchUpdateForm.CaseWhen);
                },
                ct).ConfigureAwait(false)).Form;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 探测故障保守回退基线形态（与 local_infile 探测故障降级同族：能力探测不应
            // 终止整批更新；故障不写缓存，下次重探）。取消原样上抛。
            return BatchUpdateSqlBuilder.BatchUpdateForm.CaseWhen;
        }
    }

    /// <summary>MySQL 8.0.19 的比较键——UPDATE JOIN table value constructor 的最低版本。</summary>
    private const long MySQL_8_0_19 = (8L << 40) | (0L << 24) | 19L;

    private const int MySqlUpdateFormProbeTtlMilliseconds = 60_000;

    /// <summary>解析 MySQL 版本串为可比较键：主版本×2^40 + 次版本×2^24 + 补丁。
    /// 兼容 <c>8.4.11</c>、<c>8.0.36-0ubuntu0.22.04.1</c>、<c>5.7.44-log</c> 等形态
    /// （补丁段取首个数字段，其后任意后缀忽略）。解析失败返回 0（调用方按不支持处理）。</summary>
    private static long ParseMySqlVersion(string version)
    {
        // 取首个 "-"/" " 前的数字点分部分
        int end = 0;
        while (end < version.Length && (char.IsAsciiDigit(version[end]) || version[end] == '.'))
            end++;
        if (end == 0) return 0;
        ReadOnlySpan<char> core = version.AsSpan(0, end);
        Span<Range> ranges = stackalloc Range[3];
        int parts = core.Split(ranges, '.');
        if (parts == 0) return 0;
        long major = parts > 0 && long.TryParse(core[ranges[0]], out long maj) ? maj : 0;
        long minor = parts > 1 && long.TryParse(core[ranges[1]], out long min) ? min : 0;
        long patch = parts > 2 && long.TryParse(core[ranges[2]], out long pat) ? pat : 0;
        return (major << 40) | (minor << 24) | patch;
    }

    /// <summary>MySQL-7：按形态取单批行数上限。
    /// <para><b>JoinValuesRow</b>：VALUES ROW 形态服务端是等值 JOIN（无逐行 CASE 分支比较），
    /// 批宽不影响单批 CPU；但批宽越大 SQL 文本越长、参数绑定集合越大，且新连接首次
    /// 解析成本仍随文本线性涨。探针十五实测 1000/2000 行同值，2000 A/A drift 4.8%
    /// （1000 为 9.2%）——取 2000，兼顾往返数与量具稳定性。</para>
    /// <para><b>CaseWhen</b>：沿用全局 MaxRowsPerBatch=5000（该形态服务端成本 = O(行数²)，
    /// 本已是压到协议上限前的折中值；探针十五显示更小批宽更快，但 CASE WHEN 只在
    /// &lt; 8.0.19 老服务端出现，改动会牵动 SQLite 同族共享上限，本轮不动）。</para>
    /// <para><b>PostgreSQL（2026-10-02）</b>：UPDATE FROM VALUES 单批上限 1000 行。VALUES 行数相对表规模
    /// 过大时规划器把连接翻成 Hash Join + 目标表全表顺扫：42 万行表上 2000 行单语句 45.65ms，
    /// 拆成 1000×2 走 Nested Loop + 主键索引 18.63ms（2.45×；统计陈旧同形态 2.21×），
    /// 即 PerfHub BulkUpdate/PG/2000 长期 2.65~2.83× 的根因（探针 pgplan）。1000 不是普适安全线
    /// （翻转取决于批行数/表行数），是与地板形态同宽、在该场景实测保持索引计划的值。</para></summary>
    private static int MaxRowsPerUpdateBatchFor(BatchUpdateSqlBuilder.BatchUpdateForm form)
    {
        if (TProvider.Dialect == SqlDialect.PostgreSql)
            return Math.Min(SqlLimits.MaxRowsPerBatch, PostgreSqlMaxRowsPerUpdateBatch);
        return form == BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow
            ? Math.Min(SqlLimits.MaxRowsPerBatch, 2000)
            : SqlLimits.MaxRowsPerBatch;
    }

    /// <summary>PG 批量 UPDATE FROM VALUES 的单批行数上限，依据见 <see cref="MaxRowsPerUpdateBatchFor"/>。</summary>
    private const int PostgreSqlMaxRowsPerUpdateBatch = 1000;

    /// <summary>深挖轮（2026-10-14，探针 B）：UNNEST 数组形态的单批行数上限——语句文本与批宽
    /// 无关（每列一个数组参数），批宽只受服务端计划形态与往返摊销约束。探针（20000 行表、
    /// UNNEST UPDATE、9 轮取中位）：批 5000（4 语句）= 104.9ms / 10000（2 语句）= 73.6ms /
    /// 20000（单语句）= 67.6ms——<b>4×5000 比单语句慢 55%</b>，机制是批/表行数比 25% 时规划器
    /// 已把数组连接翻成 Hash Join + 全表顺扫（每语句一次全表扫描，4 语句 = 4 次扫描）；批宽
    /// 提到与表同量级后只扫一次。大表（比值小）方向：Nested Loop + 主键索引计划不变，批宽
    /// 提升只减语句数与往返，两头都不劣。20000 是探针实证上界（更大宽度未测，锁时长按需
    /// 再评）。适用于三个数组形态执行体（UPDATE/UPSERT/DELETE——DELETE 的 <c>= ANY(@ids)</c>
    /// 无连接翻转面，收益为纯往返摊销）。</summary>
    private const int UnnestArraysMaxRowsPerBatch = 20000;

    /// <summary>旧版生成器模型程序集的回退取值：probe 命令逐行 <c>BindUpdate</c> 建参数，
    /// 再把值写进池。语义与 <see cref="CrudMetadata.BindUpdateValues"/> 一致，参数创建量回到
    /// 改前水平（这是旧程序集的既有成本，不是本路径的回归）。
    /// probe 命令由会话连接创建（<see cref="CreateCommand(DbTransaction?)"/> 带事务/超时口径）。</summary>
    private async ValueTask BindRowValuesViaProbeAsync<T>(
        CrudMetadata metadata, DbParameter[] pool,
        IReadOnlyList<T> entities, int start, int end, int paramsPerRow)
        where T : class, new()
    {
        await using DbCommand probe = CreateCommand();
        for (int i = start; i < end; i++)
        {
            // PARAM-REUSE-OK[carrier] probe 从不执行，只把逐行 BindUpdate 的值搬进池
            probe.Parameters.Clear();
            metadata.BindUpdate(probe, entities[i]);
            int baseIndex = (i - start) * paramsPerRow;
            for (int c = 0; c < paramsPerRow; c++)
                pool[baseIndex + c].Value = probe.Parameters[c].Value;
        }
    }

    /// <summary>把参数池的前 <paramref name="rowParamCount"/> 个（+ 末尾租户参数）挂到命令集合。
    /// 批大小未变时集合元素数一致，直接返回；末批缩短时清空后按池内前缀重挂——
    /// 参数对象全部来自池，无新分配（参数集合本身的重建是驱动固有成本）。
    /// <para><b>internal for testing</b>：本方法是 M1 跨批复用的契约点（池按满批建、命令集合按
    /// 实际批收敛），PG/MySQL 批量路径本地跑不到，靠单测锁定。</para></summary>
    internal static void AttachParameters(
        DbCommand cmd, DbParameter[] pool, int rowParamCount, int poolRowParamCount, int tenantParams)
    {
        int required = rowParamCount + tenantParams;
        if (cmd.Parameters.Count == required) return;
        // PARAM-REUSE-OK[pool:pool] 重挂的是参数池中的同一实例（非新对象），批间只改 Value
        cmd.Parameters.Clear();
        for (int i = 0; i < rowParamCount; i++)
            cmd.Parameters.Add(pool[i]);
        if (tenantParams > 0)
            cmd.Parameters.Add(pool[poolRowParamCount]);
    }

    /// <summary>批量 UPDATE 上下文（避免方法参数过多 S107）。</summary>
    private sealed record BatchUpdateContext(
        string[] SetColumns, string QuotedTable, string QuotedPk, bool HasTenantFilter)
    {
        public int SetColumnCount => SetColumns.Length;
    }

    /// <summary>批量 Upsert。整个输入在同一事务内执行，复用源生成写入元数据。
    /// <para>ITM-556 注记: 自增 ID 回填随每条 UPSERT 立即发生；中途失败整批回滚时，
    /// 已回填的内存 ID 对应的行不存在于 DB——异常路径下不要继续使用输入实体的 ID，
    /// 重试应重新走 BulkMergeAsync（UPSERT 幂等）。</para></summary>
    /// <returns><b>成功处理的实体数</b>（按输入计数），<b>非</b>数据库受影响行数——与
    /// BulkInsert/Update/Delete 返回驱动行数的语义不同（审计 API-010 文档化）。
    /// 选择该语义的原因（2026-09-19 论证后裁决维持）：① MySQL ON DUPLICATE KEY 的
    /// affectedRows 对更新行计 2、插入行计 1——返回值将依赖数据历史（同输入重放返回不同值），
    /// 调用方的对账逻辑会静默错；② PG/SQLite 的 ON CONFLICT 每行计 1、恰等于实体数，
    /// 处理实体数是唯一跨方言可预测口径；③ 混合路径（默认键逐条 INSERT + 批量 UPSERT）
    /// 下两种驱动口径不可加总。BulkMergeSetBasedTests 已锁定本口径。</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "分区循环含三条路径（默认键集合化/默认键逐行回退/既有键缓冲）+ rowByRow 旁支，"
            + "均为路由判定不可再拆（拆出需传递 operation lease 与事务上下文，跨方法状态面更大）。")]
    public async ValueTask<long> BulkMergeAsync<T>(IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        // R8：单次注册表快照——原实现入口与路由各读一次 CurrentState（两次 Volatile.Read）
        PalORM_Runtime.RuntimeRegistryState mergeState = PalORM_Runtime.CurrentState;
        if (!mergeState._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata mergeMetadata))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated CRUD.");
        if (entities.Count == 0) return 0;

        // v5.6.0 集合化：按键状态分区——默认键行走逐条 INSERT（保留 ID 回填契约，
        // InsertCoreAsync 的物化/回填无法在多值形态下按行还原）；非默认键行
        // （bulk-merge 的主流场景：既有键的重复执行更新）改为**多行 UPSERT**，
        // 每批一条语句。原实现 N 行 = N 次往返（每行一次 SaveCoreAsync），
        // 10K 行在远程库（RTT ~1ms）约 10s。集合化后的批宽（BatchUpsertAsync 的
        // maxRowsPerBatch）：PG 1000 行/批（PostgreSqlMaxRowsPerUpsertBatch，分配纪律）、
        // MySQL 1000 行/批（MySqlOdkuMaxRowsPerBatch，探针二十）、SQLite 受 999 参数
        // 上限按列数分批（SqlLimits.MaxBindParametersFor）——10K 行 PG/MySQL 约 10 条语句，
        // SQLite 语句数 = ⌈列数×10K ÷ 999⌉ 量级（大值经 A/B 证伪见 SqlLimits）。
        // 单行语义保持：ON CONFLICT/ON DUPLICATE KEY 与 SaveCoreAsync 的单行
        // upsert 用同一谓词列集（UpsertColumns，含 PK）；[ConcurrencyCheck] 实体
        // 维持逐条路径——SaveCoreAsync 会以同消息拒绝（UPSERT 无法尊重乐观锁，ITM-503）。
        bool rowByRow = mergeMetadata.IncrementVersion is not null
            || mergeMetadata.UpsertColumns.Count == 0;

        return await RunInTransactionScopeAsync(
            operation.Owner,
            async (transaction, token) =>
            {
                long affected = 0;
                if (rowByRow)
                {
                    foreach (T entity in entities)
                    {
                        await SaveCoreAsync(entity, operation.Owner, token).ConfigureAwait(false);
                        affected++;
                    }
                    return affected;
                }

                // B6（2026-10-01 全 API 逐项轮）：惰性分配——全默认键（自增新实体）场景不付
                // count 容量的引用数组（2 万行约 160KB）；两个批次方法自带空列表短路。
                // B6 落地（2026-10-12）：默认键行集合化——RETURNING 方言（PG/SQLite）改走
                // 多行 INSERT..RETURNING 按行序回填（BatchInsertReturningAsync），N 次往返收敛为
                // N/批；MySQL 保持逐行（LAST_INSERT_ID 多行回填需"批内自增值连续"假设，
                // auto_increment_increment/触发器/混合分配都会破坏，MySQL 文档只保证首值）。
                bool batchDefaultKeyInserts = TProvider.SupportsReturningClause
                    && mergeMetadata.InsertColumns.Count > 0;
                List<T>? upsertBatch = null;
                List<T>? insertBatch = null;
                foreach (T entity in entities)
                {
                    if (mergeMetadata.HasDefaultKey(entity))
                    {
                        if (batchDefaultKeyInserts)
                        {
                            (insertBatch ??= new List<T>(entities.Count)).Add(entity);
                        }
                        else
                        {
                            await SaveCoreAsync(entity, operation.Owner, token).ConfigureAwait(false);
                            affected++;
                        }
                    }
                    else
                    {
                        (upsertBatch ??= new List<T>(entities.Count)).Add(entity);
                    }
                }

                if (insertBatch is not null)
                    affected += await BatchInsertReturningAsync(
                        transaction, insertBatch, mergeMetadata, token).ConfigureAwait(false);
                affected += await BatchUpsertAsync(
                    transaction, upsertBatch ?? [], mergeMetadata, token).ConfigureAwait(false);
                return affected;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>B6 落地（2026-10-12）：BulkMerge 默认键行的集合化插入——RETURNING 方言
    /// （PG/SQLite）多行 INSERT ... RETURNING 主键，按行序回填实体，N 次往返收敛为 N/批。
    /// <para><b>行序映射</b>：RETURNING 行序按 VALUES 顺序（EF Core Npgsql 多行
    /// INSERT..RETURNING 的同构形态）；返回行数与批行数不符时明确失败，不静默错位回填。</para>
    /// <para><b>回填时点</b>：经 <see cref="BackfillGeneratedId"/>——自开事务延迟到提交后回放，
    /// 与单行 SaveCoreAsync 路径同语义（TX-004）。</para>
    /// <para><b>已知边界</b>：本路径每次批间重建参数（BindInsert 直绑）——相对原 N 次往返已是
    /// 数量级收敛；参数池化留待 PerfHub 门禁显示需要时再做（&lt;5% 不做线）。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "批循环（分批/绑行）+ RETURNING 读循环（行数核对、ID 回填双守卫）各为"
            + "不可拆的顺序语义；拆分需在方法间传递回填对位状态，正确性风险大于可读性收益。")]
    private async Task<long> BatchInsertReturningAsync<T>(
        DbTransaction transaction,
        List<T> entities,
        CrudMetadata metadata,
        CancellationToken ct) where T : class, new()
    {
        EntityRuntimeInfo info = GetEntityInfoForCrud<T>();
        if (info.PkColumn is not { } pkColumn)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no primary key column; set-based insert requires one.");
        string insertTableName = info.TableName;
        Action<object, long>? backfill = info.SetId;

        int columnCount = metadata.InsertColumns.Count;
        int batchSize = Math.Max(1, Math.Min(
            SqlLimits.MaxRowsPerBatch,
            SqlLimits.MaxBindParametersFor(TProvider.Dialect) / columnCount));
        string quotedTable = TProvider.QuoteIdentifier(insertTableName);
        string quotedPk = TProvider.QuoteIdentifier(pkColumn);
        // S1：引用列清单由 (TProvider, T) 唯一决定，每类型构建一次。
        string quotedColumns = SqlSlots<T>.InsertReturningQuotedColumnsOrBuild(metadata.InsertColumns);

        // R3（2026-10-12）：参数池化——原每批 fresh 命令 + 每行 BindInsert 建参（PG 默认键
        // merge 20000 行约 20 万参数对象）。镜像 MultiValueBulkInsert 的 R-UNNESTB 安全池形态：
        // 命令跨批复用 + 池参数就地写 Value（不 Clear 不 Add 新实例）；末批 RemoveAt 只裁池尾
        //（ITM-785：短批恒为末次，裁后不回满批）。旧生成器程序集（无 BindInsertValues）与
        // 首批即非满批的单批小数据保持 fresh 路径（与 MultiValueBulkInsert 的池纪律同款）。
        long processed = 0;
        Action<DbParameter[], object, int>? valuesBinder = metadata.BindInsertValues;
        int fullBatchRows = Math.Min(batchSize, entities.Count);
        DbCommand? pooledCmd = null;
        DbParameter[]? pool = null;
        string? lastSql = null;
        int lastRowCount = -1;
        try
        {
            for (int start = 0; start < entities.Count; start += batchSize)
            {
                int end = Math.Min(start + batchSize, entities.Count);
                int rowCount = end - start;
                DbCommand cmd;
                bool ownsCmd;
                if (valuesBinder is not null && (pool is not null || rowCount == batchSize))
                {
                    if (pooledCmd is null)
                    {
                        pooledCmd = CreateCommand();
                        pooledCmd.Transaction = transaction;
                        pooledCmd.CommandTimeout = _options.CommandTimeoutSeconds;
                    }
                    cmd = pooledCmd;
                    ownsCmd = false;
                    if (pool is null)
                    {
                        pool = new DbParameter[fullBatchRows * columnCount];
                        for (int i = 0; i < pool.Length; i++)
                        {
                            DbParameter pooled = pooledCmd.CreateParameter();
                            pooled.ParameterName = ParameterNameCache.GetName(i);
                            pooledCmd.Parameters.Add(pooled);
                            pool[i] = pooled;
                        }
                        // B21 同款：池 DbType 一次性建立（BindInsertValues 池存续期内只写 Value）
                        metadata.InitInsertParameters?.Invoke(pool, 0);
                    }
                    int neededParameters = rowCount * columnCount;
                    // PARAM-REUSE-OK[pool:pool] 末批裁剪只移除池尾实例，保留前缀实例不变（valuesBinder 只改其 Value）
                    for (int i = pooledCmd.Parameters.Count - 1; i >= neededParameters; i--)
                        pooledCmd.Parameters.RemoveAt(i);
                    for (int row = start; row < end; row++)
                        valuesBinder(pool, entities[row], (row - start) * columnCount);
                    if (rowCount != lastRowCount)
                    {
                        lastSql = BuildInsertReturningBatchSql(
                            quotedTable, quotedColumns, quotedPk, rowCount, columnCount);
                        lastRowCount = rowCount;
                    }
                    pooledCmd.CommandText = lastSql;
                }
                else
                {
                    cmd = CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandTimeout = _options.CommandTimeoutSeconds;
                    cmd.CommandText = BuildInsertReturningBatchSql(
                        quotedTable, quotedColumns, quotedPk, rowCount, columnCount);
                    for (int row = start; row < end; row++)
                        metadata.BindInsert(cmd, entities[row], (row - start) * columnCount);
                    ownsCmd = true;
                }

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                int returned = 0;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    long? id = NormalizeGeneratedId(
                        await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) ? null : reader.GetValue(0));
                    if (id is { } generatedId && backfill is not null)
                        BackfillGeneratedId(backfill, entities[start + returned], generatedId);
                    returned++;
                }
                if (returned != rowCount)
                    throw new InvalidOperationException(
                        $"INSERT ... RETURNING returned {returned} rows for {rowCount} inserted rows " +
                        $"of '{typeof(T).Name}'; refusing to backfill possibly misaligned ids.");
                processed += rowCount;
                if (ownsCmd)
                    await cmd.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (pooledCmd is not null)
                await pooledCmd.DisposeAsync().ConfigureAwait(false);
        }
        return processed;
    }

    /// <summary>多行 INSERT..RETURNING 语句构建（同步方法收栈缓冲生命周期，与
    /// <see cref="MultiValueBulkInsert"/> 的 BuildBatchSql 同纪律——span 不跨 await）。</summary>
    private static string BuildInsertReturningBatchSql(
        string quotedTable, string quotedColumns, string quotedPk, int rowCount, int columnCount)
    {
        Span<char> sqlBuffer = stackalloc char[512];
        var sb = new ValueStringBuilder(sqlBuffer);
        try
        {
            MultiValueBulkInsert.AppendInsertStatement(
                ref sb, quotedTable, quotedColumns, rowCount, columnCount);
            sb.Append(" RETURNING ");
            sb.Append(quotedPk);
            return sb.ToString();
        }
        finally { sb.Dispose(); }
    }

    /// <summary>旧模型程序集回退路径：scratch 命令逐行 BindUpsert → 值拷贝进池（PL-3.2 抽出）。</summary>
    private static void BindUpsertRowViaScratch<T>(
        DbCommand scratch, CrudMetadata metadata, T entity,
        DbParameter[] pool, int rowBase, int columnCount) where T : class, new()
    {
        // PARAM-REUSE-OK[carrier] scratch 从不执行，只承载逐行 BindUpsert 的值产出
        scratch.Parameters.Clear();
        metadata.BindUpsert(scratch, entity);
        if (scratch.Parameters.Count != columnCount)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' upsert binder produced {scratch.Parameters.Count} " +
                $"parameters for {columnCount} upsert columns.");
        for (int c = 0; c < columnCount; c++)
            pool[rowBase + c].Value = scratch.Parameters[c].Value;
    }

    /// <summary>多行 UPSERT 分批执行——PG/SQLite 走 ON CONFLICT (...) DO UPDATE SET c=excluded.c，
    /// MySQL 走 ON DUPLICATE KEY UPDATE c=VALUES(c)（与单行 upsert 的既有 SQL 形态一致）。
    /// <para><b>批次上限</b>：每语句参数总数按方言取上限（SQLite 999 保守值——32766 大值
    /// 经同轮 A/B 实测为负优化，见 <see cref="SqlLimits.MaxBindParametersFor"/>；PG/MySQL 65535），
    /// 再与"单批行数上限 × 列数"取较小者约束语句文本规模
    /// （2026-09-25 前为硬编码 900，BULK-001 修正为方言感知）。</para>
    /// <para><b>批内重复主键的语义边界（如实登记）</b>：单行逐条形态下后行静默覆盖前行
    /// （last-wins）。集合化后 MySQL 保持 last-wins（ON DUPLICATE KEY 天然如此）；
    /// PG/SQLite 对同一语句内影响同一行会报错（PG: "cannot affect row a second time"）。
    /// 旧行为的 last-wins 依赖执行顺序，属于未定义边界的巧合而非契约——集合化把它
    /// 变成明确失败。需要确定性 last-wins 时请先按主键去重再调用。</para>
    /// <para><b>返回值</b>：与原实现一致，返回处理的行数（不依赖驱动的 affectedRows——
    /// MySQL ON DUPLICATE KEY 的 affectedRows 对 insert/update 取值不同，不可比）。</para></summary>
    /// <summary>MySQL-9：ODKU 单批行数上限——探针二十实测 1000 行/批比 5000 行快
    /// 1.13~1.22× 且 SQL 文本 -82%；低于 ODKU 不再改善（500 行 204.5ms vs 1000 行 197.1ms，
    /// 批数增加反超收益）。非 MySQL 方言不用此值（PG ON CONFLICT/SQLite 维持上限）。</summary>
    private const int MySqlOdkuMaxRowsPerBatch = 1000;

    /// <summary>PG ON CONFLICT 单批行数上限（2026-10-02）：时延对批宽不敏感，分配随批宽线性增长——
    /// 参数池按"本调用最大批"建，5000 行批宽下 2000 行即 1 万个参数对象、2 万行 2.5 万个。
    /// PerfHub UpsertBatch/PG 同批对照：产品（5000）对 ADO 地板（1000）时延 1.01×/1.02×、
    /// 分配 4.31MB 对 2.79MB（2000 行）、19.77MB 对 12.41MB（2 万行）。</summary>
    private const int PostgreSqlMaxRowsPerUpsertBatch = 1000;

    private async Task<long> BatchUpsertAsync<T>(
        DbTransaction transaction,
        List<T> entities,
        CrudMetadata metadata,
        CancellationToken ct) where T : class, new()
    {
        if (entities.Count == 0) return 0;

        int columnCount = metadata.UpsertColumns.Count;
        // BULK-001（2026-09-23）：按方言取参数上限——原硬编码 900（SQLite 999 的余量）把 PG/MySQL
        // 的 65535 上限钳到 900：20 列实体 45 行/批、10K 行 223 次往返（同上限只需 4 批）。
        // SQLite 侧 32766 大值经 2026-09-26 同轮 A/B 实测证伪（批量劣化 6~16×），维持 999。
        // 再叠加单批行数上限约束语句文本规模。
        // MySQL-9（2026-09-27）：MySQL ODKU 用 1000 行/批——探针二十实测（真库 20000 行，
        // 多轮）：1000 行比 5000 行快 1.13~1.22×（ODKU 服务端近线性但批内唯一键探测与
        // 大文本解析仍有成本），SQL 文本从 199KB 降到 36KB（-82%）。SQLite 未实测，维持原上限。
        // PG（2026-10-02）：1000 行/批，依据见 PostgreSqlMaxRowsPerUpsertBatch。
        int maxRowsPerBatch = TProvider.Dialect switch
        {
            SqlDialect.MySql => Math.Min(SqlLimits.MaxRowsPerBatch, MySqlOdkuMaxRowsPerBatch),
            SqlDialect.PostgreSql => Math.Min(SqlLimits.MaxRowsPerBatch, PostgreSqlMaxRowsPerUpsertBatch),
            _ => SqlLimits.MaxRowsPerBatch,
        };
        // ITM-884（r24，ITM-809 族第 4 处）：参数上限扣租户参数——租户守卫（ApplyTenantGuardToShape）
        // 每语句追加 1 个租户参数（PG/SQLite 冲突子句 WHERE 后；MySQL 各赋值项引用同名 @p，
        // 参数集合内仍只占 1 个对象）。行数上限分支（maxRowsPerBatch * columnCount）不含租户参数，
        // 不扣——两分支语义各自正确，Math.Min 取较小者。此处 tenantGuarded（1347 行）尚未声明，
        // HasTenantFilter 现算无缓存（同 818 行注释口径），两处调用等值。
        int upsertTenantParams = HasTenantFilter<T>() ? 1 : 0;
        int maxParametersPerStatement = Math.Min(
            SqlLimits.MaxBindParametersFor(TProvider.Dialect) - upsertTenantParams,
            maxRowsPerBatch * columnCount);
        int batchSize = Math.Max(1, maxParametersPerStatement / columnCount);

        // S1：表名与主键列取每类型静态视图（注册期快照，单调不可变）。
        EntityRuntimeInfo info = GetEntityInfoForCrud<T>();
        string tableName = info.TableName;
        if (info.PkColumn is not { } pkColumn)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no primary key column; set-based upsert requires one.");
        UpsertSqlShape shape = ApplyTenantGuardToShape<T>(BuildUpsertSqlShape(typeof(T), tableName, metadata, pkColumn));
        bool tenantGuarded = HasTenantFilter<T>();

        // UNNEST 阶段 B（2026-10-02）：PG 数组形态——INSERT … SELECT * FROM UNNEST(@u0,…) + 冲突子句。
        // 能力检测与 UPDATE 路径同纪律（生成物三件套 + Provider 接受全部列的元素类型）。
        // 批内重复主键语义与既有形态一致：PG 对同语句影响同一行两次明确报错（见方法级文档）。
        if (UseUnnestArraysForUpsert<T>(metadata, out IReadOnlyList<Type>? upsertElementTypes))
        {
            return await ExecuteUpsertArrayBatchesAsync(
                entities, metadata, shape, upsertElementTypes!, transaction, tenantGuarded, ct).ConfigureAwait(false);
        }

        // 绑定策略：BindUpsert 每行从 @p0 起命名且 MySQL 参数集合在 Add 时校验重名——
        // 不能直接往批命令里逐行 Append。PL-3.2：生成器发射 BindUpsertValues 时直写参数池
        // （零 CreateParameter）；旧模型程序集回退 scratch 命令绑定 → 值拷贝进预建参数池。
        // S1：新生成器路径不需要 scratch 命令，惰性化后每调用省一个命令对象。
        Action<DbParameter[], object, int>? upsertValuesBinder = metadata.BindUpsertValues;
        await using DbCommand? scratch = upsertValuesBinder is null ? CreateCommand() : null;

        // PERF-004（2026-09-23）：命令与参数池跨批复用——池按满批大小建一次（下标 0..N-1 逐批同义），
        // 批间只写 Value；末批缩短时经 AttachParameters 收敛命令参数集合（池对象零新增分配）。
        // SQL 按 rowCount 记忆化（满批文本逐批相同，原实现每批重建一次）。
        // 租户护栏（2026-10-06）：池布局 = [行参数…][租户参数]，与 BulkUpdate 的
        // AttachParameters 契约同构；守卫 SQL 已在 shape 层施加。
        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        int fullRowCount = Math.Min(batchSize, entities.Count);
        int tenantParams = CountTenantParams(tenantGuarded);
        var pool = CreateUpsertParameterPool<T>(cmd, fullRowCount, columnCount, tenantGuarded);
        // N4（2026-10-04 全量复读）：可空/byte[] 列的 DbType 提示建池后一次性建立——原由
        // BindUpsertValues 每行重写（池存续期内恒定，纯冗余）。旧生成器程序集 Init 委托为
        // null：不调，其 BindUpsertValues 保持自写 DbType 的旧形态（B21 对 INSERT 池的同款契约）。
        metadata.InitUpsertParameters?.Invoke(pool, 0);

        string? lastSql = null;
        int lastRowCount = -1;
        long processed = 0;
        for (int start = 0; start < entities.Count; start += batchSize)
        {
            int end = Math.Min(start + batchSize, entities.Count);
            int rowCount = end - start;
            AttachParameters(cmd, pool, rowCount * columnCount, poolRowParamCount: fullRowCount * columnCount, tenantParams);

            for (int row = start; row < end; row++)
            {
                int rowBase = (row - start) * columnCount;
                if (upsertValuesBinder is not null)
                {
                    upsertValuesBinder(pool, entities[row], rowBase);
                    continue;
                }
                BindUpsertRowViaScratch(scratch!, metadata, entities[row], pool, rowBase, columnCount);
            }

            if (rowCount != lastRowCount)
            {
                lastSql = BuildUpsertBatchSql(rowCount, columnCount, shape);
                lastRowCount = rowCount;
            }
            cmd.CommandText = lastSql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            processed += rowCount;
        }
        return processed;
    }

    /// <summary>UPSERT 语句的可复用片段（方言分派一次，逐批复用）。</summary>
    private readonly record struct UpsertSqlShape(string QuotedTable, string QuotedColumns, string ConflictClause);

    /// <summary>租户护栏形态（2026-10-06）：带租户会话时冲突子句带守卫——PG/SQLite 冲突子句后
    /// 追加 WHERE 表名限定的租户条件，MySQL 逐赋值项包 IF(tenant_id = @p, …)（ODKU 无 WHERE）。
    /// values 与 UNNEST 两路共用同一 shape。</summary>
    private UpsertSqlShape ApplyTenantGuardToShape<T>(UpsertSqlShape shape) where T : class, new()
    {
        if (!HasTenantFilter<T>())
            return shape;
        string qualifiedTenantColumn = $"{shape.QuotedTable}.{TProvider.QuoteIdentifier("tenant_id")}";
        // S1：守卫后的冲突子句由 (TProvider, T) 唯一决定；MySQL 的 IF(...) 正则包装只跑一次。
        return new UpsertSqlShape(
            shape.QuotedTable, shape.QuotedColumns,
            SqlSlots<T>.UpsertTenantConflictOrBuild(
                shape.ConflictClause, qualifiedTenantColumn, _tenantParameterName));
    }

    /// <summary>租户护栏参数槽数量（守卫开启时池尾 1 个 @__tenant0 槽）。</summary>
    private static int CountTenantParams(bool tenantGuarded) => tenantGuarded ? 1 : 0;

    /// <summary>批量 UPSERT 参数池：[行参数 fullRowCount×columnCount][租户参数?]——
    /// 池尾租户参数槽与 BulkUpdate 的 AttachParameters 契约同构。</summary>
    private System.Data.Common.DbParameter[] CreateUpsertParameterPool<T>(
        DbCommand cmd, int fullRowCount, int columnCount, bool tenantGuarded)
        where T : class, new()
    {
        int rowParamCount = fullRowCount * columnCount;
        var pool = new System.Data.Common.DbParameter[rowParamCount + (tenantGuarded ? 1 : 0)];
        for (int i = 0; i < rowParamCount; i++)
        {
            System.Data.Common.DbParameter parameter = cmd.CreateParameter();
            parameter.ParameterName = QueryBuilder<T>.GetParameterName(i);
            pool[i] = parameter;
        }
        if (tenantGuarded)
            pool[rowParamCount] = TProvider.CreateParameter(_tenantParameterName, _tenantId!);
        return pool;
    }

    /// <summary>UNNEST 阶段 B：批量 UPSERT 的数组形态能力检测（与
    /// <see cref="UseUnnestArraysForUpdate"/> 同纪律——生成物三件套非空 + Provider 接受
    /// 全部列的元素类型；任一列不支持整体回退多值 VALUES 形态）。</summary>
    private static bool UseUnnestArraysForUpsert<T>(
        CrudMetadata metadata, out IReadOnlyList<Type>? arrayElementTypes)
        where T : class, new()
    {
        arrayElementTypes = null;
        // 方言前置（同 UseUnnestArraysForUpdate：UNNEST 语法 PG 专有，能力检测与 SQL 构造同源）
        if (TProvider.Dialect != SqlDialect.PostgreSql)
            return false;
        if (metadata.FillUpsertColumnArrays is null
            || metadata.CreateUpsertColumnArrays is null
            || metadata.UpsertColumnArrayElementTypes is not { Count: > 0 } elementTypes)
        {
            SqlSlots<T>.UpsertArraySupported = false;
            return false;
        }
        // S1：同 Update 侧，能力每 (TProvider, T) 探测一次。
        if (SqlSlots<T>.UpsertArraySupported is { } known)
        {
            arrayElementTypes = known ? elementTypes : null;
            return known;
        }
        Array[] probes = metadata.CreateUpsertColumnArrays(1);
        for (int c = 0; c < elementTypes.Count; c++)
        {
            if (TProvider.CreateTypedArrayParameter("@probe", probes[c], elementTypes[c]) is null)
            {
                SqlSlots<T>.UpsertArraySupported = false;
                return false;
            }
        }
        SqlSlots<T>.UpsertArraySupported = true;
        arrayElementTypes = elementTypes;
        return true;
    }

    /// <summary>UNNEST 阶段 B：数组形态的批量 UPSERT 主体——<c>INSERT … SELECT * FROM
    /// UNNEST(@u0, @u1, …) ON CONFLICT …</c>。语句文本跨批恒定（参数个数 = 列数，与批宽无关），
    /// 列数组按批容量预建复用（末批用短数组，理由同 <see cref="ExecuteBulkUpdateArrayBatchesAsync{T}"/>）。
    /// <para><b>返回值口径与既有形态一致</b>：返回处理的行数（不依赖 affectedRows——
    /// MySQL ODKU 的计数口径与 PG 不同，见 BatchUpsertAsync 文档）。</para></summary>
    private async Task<long> ExecuteUpsertArrayBatchesAsync<T>(
        List<T> entities,
        CrudMetadata metadata,
        UpsertSqlShape shape,
        IReadOnlyList<Type> arrayElementTypes,
        DbTransaction? transaction,
        bool tenantGuarded,
        CancellationToken ct)
        where T : class, new()
    {
        Action<IReadOnlyList<object>, int, int, Array[], int> fillArrays = metadata.FillUpsertColumnArrays!;
        Func<int, Array[]> createArrays = metadata.CreateUpsertColumnArrays!;
        int columnCount = arrayElementTypes.Count;
        // 契约断言（2026-10-06，镜像 UPDATE 侧哨兵/ExecuteBulkUpdateArrayBatchesAsync）：生成物的
        // 数组列序 = UpsertColumns 全集，与数组形态 SQL 的 UNNEST(@u0…) 参数个数及 INSERT 列清单
        // 逐位对应。不符即生成器与 Core 漂移——明确抛错而非静默错绑（哨兵范式同 UPDATE 侧）。
        if (columnCount != metadata.UpsertColumns.Count)
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}': UNNEST array form expects {metadata.UpsertColumns.Count} column arrays "
                + $"(upsert columns) but the generated filler provides {columnCount}. "
                + "The generator and batch upsert SQL disagree; recompile the model assembly.");
        }
        // 数组形态不受"语句内参数个数"约束（整批每列一个参数）。批宽：既有 PG upsert 的
        // 1000 行上限理由是"参数池按最大批建、行×列个参数对象"——数组形态的成本结构不同
        //（每列一个数组，元素总量 = 行×列但无参数对象），不适用该约束。深挖轮批宽提升
        //（探针 B 同机制，见 UnnestArraysMaxRowsPerBatch）。
        int batchSize = UnnestArraysMaxRowsPerBatch;
        int fullBatchLen = Math.Min(batchSize, entities.Count);

        int lastBatchLen = entities.Count % fullBatchLen;
        Array[] fullArrays = createArrays(fullBatchLen);
        Array[] shortArrays = lastBatchLen == 0 ? fullArrays : createArrays(lastBatchLen);

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        var sql = new System.Text.StringBuilder(96 + shape.QuotedColumns.Length + shape.ConflictClause.Length);
        sql.Append("INSERT INTO ").Append(shape.QuotedTable).Append(" (")
            .Append(shape.QuotedColumns).Append(") SELECT * FROM UNNEST(");
        for (int c = 0; c < columnCount; c++)
        {
            if (c > 0) sql.Append(", ");
            sql.Append(BatchUpdateSqlBuilder.UnnestColumnParameterName(c));
        }
        sql.Append(')').Append(shape.ConflictClause);
        cmd.CommandText = sql.ToString();

        var parameters = new DbParameter[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            parameters[c] = TProvider.CreateParameter(
                BatchUpdateSqlBuilder.UnnestColumnParameterName(c), DBNull.Value);
            cmd.Parameters.Add(parameters[c]);
        }
        // 租户护栏：守卫 SQL 已在 shape 层施加，此处只补 @__tenant0 绑定
        if (tenantGuarded)
            cmd.Parameters.Add(TProvider.CreateParameter(_tenantParameterName, _tenantId!));

        long processed = 0;
        for (int start = 0; start < entities.Count; start += batchSize)
        {
            int batchLen = Math.Min(batchSize, entities.Count - start);
            Array[] columns = batchLen == fullBatchLen ? fullArrays : shortArrays;
            fillArrays(entities, start, batchLen, columns, 0);
            for (int c = 0; c < columnCount; c++)
            {
                // ITM-894：同 Update 执行体——null 时显式诊断（对齐 BuildArrayParameter）
                parameters[c].Value = (TProvider.CreateTypedArrayParameter(
                    BatchUpdateSqlBuilder.UnnestColumnParameterName(c),
                    columns[c], arrayElementTypes[c])
                    ?? throw new InvalidOperationException(
                        $"Provider '{TProvider.Name}' returned a null typed array parameter for "
                        + $"element type '{arrayElementTypes[c].Name}' (batch length {batchLen}); "
                        + "the capability probe and the execution path disagree.")).Value;
            }
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            processed += batchLen;
        }
        return processed;
    }

    /// <summary>构建方言分派的 UPSERT 骨架片段。UPDATE 集 = UpsertColumns 去掉主键
    /// （ON CONFLICT 的 DO UPDATE 不能更新冲突键本身）；MySQL 用 VALUES(c) 形态
    /// （与单行 upsert 的既有生成 SQL 一致）。</summary>
    private UpsertSqlShape BuildUpsertSqlShape(Type entityType, string tableName, CrudMetadata metadata, string pkColumn)
    {
        // R3（2026-10-12）：形状字符串 per-(Type, Dialect) 缓存——原每次调用 1 LINQ 迭代器 +
        // 2 string.Join + 冲突子句插值（与 QuotedColumnsCache 同纪律；值由编译期元数据唯一决定，
        // UpsertColumns/pkColumn 同源于注册表）。record struct 包装零分配，缓存存字符串组件即可。
        (Type, SqlDialect) shapeKey = (entityType, TProvider.Dialect);
        if (DataSessionCache.UpsertShapeCache.TryGetValue(shapeKey, out (string QuotedColumns, string ConflictClause) cached))
            return new UpsertSqlShape(TProvider.QuoteIdentifier(tableName), cached.QuotedColumns, cached.ConflictClause);

        string quote(string identifier) => TProvider.QuoteIdentifier(identifier);
        List<string> updateColumns =
        [
            .. metadata.UpsertColumns.Where(c => !string.Equals(c, pkColumn, StringComparison.Ordinal))
        ];
        if (updateColumns.Count == 0)
            throw new NotSupportedException(
                $"Set-based upsert on '{tableName}' has no updatable columns " +
                "(upsert columns minus primary key is empty); use BulkInsertAsync instead.");
        // ITM-876（r23 裁决登记）：VALUES() 形态在 MySQL 8.0.20+ 处于弃用路线（推荐 row alias
        // 新语法），但真库探针（OdkuDeprecationProbeTests，MySQL 8.4.11）实测**零弃用警告**且
        // 行为正确——分派双形态（row alias 要求 8.0.19+）当前无收益信号，只引入 UpsertSqlShape
        // 生成/快照/三序一致的双份维护面。维持单形态 + 哨兵测试：未来版本真报弃用错误时
        // 探针自动转红，届时按 MySQL-7 ResolveMySqlUpdateFormAsync 先例（版本探测 + 每连接缓存）分派。
        string conflictClause = TProvider.Dialect == SqlDialect.MySql
            ? " ON DUPLICATE KEY UPDATE " + string.Join(", ",
                updateColumns.Select(c => $"{quote(c)} = VALUES({quote(c)})"))
            : $" ON CONFLICT ({quote(pkColumn)}) DO UPDATE SET " + string.Join(", ",
                updateColumns.Select(c => $"{quote(c)} = excluded.{quote(c)}"));
        string quotedColumns = string.Join(", ", metadata.UpsertColumns.Select(quote));
        DataSessionCache.UpsertShapeCache.TryAdd(shapeKey, (quotedColumns, conflictClause));
        return new UpsertSqlShape(quote(tableName), quotedColumns, conflictClause);
    }

    /// <summary>构建单批的多值 UPSERT SQL——参数已由调用方按批内连续下标预建绑定，
    /// 此处只生成与之一一对应的 VALUES 占位符。</summary>
    private static string BuildUpsertBatchSql(int rowCount, int columnCount, UpsertSqlShape shape)
    {
        var sql = new System.Text.StringBuilder(64 + rowCount * (columnCount * 8 + 4));
        sql.Append("INSERT INTO ").Append(shape.QuotedTable).Append(" (")
            .Append(shape.QuotedColumns).Append(") VALUES ");
        for (int row = 0; row < rowCount; row++)
        {
            sql.Append(row > 0 ? ", (" : "(");
            for (int c = 0; c < columnCount; c++)
            {
                if (c > 0) sql.Append(", ");
                sql.Append(ParameterNameCache.GetName(row * columnCount + c));
            }
            sql.Append(')');
        }
        sql.Append(shape.ConflictClause);
        return sql.ToString();
    }

    /// <summary>种子数据。要求每个实体具有非默认稳定主键，重复执行按主键更新。
    /// <para><b>ITM-911（r24）</b>：校验/收集段改读注册表单快照（R8 纪律，与 bulk 家族对齐，
    /// 原 <c>PalORM_Runtime.CrudMetadatas</c> 直读绕过快照）+ 收集循环响应取消。
    /// 外层 EnterOperation 租约登记不做：收集段不触碰会话可变状态（连接/门禁/过滤），
    /// 实际 DB 操作在内层 BulkMergeAsync 自带租约。</para></summary>
    public async ValueTask SeedAsync<T>(IEnumerable<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entities);
        // ITM-911：单快照（R8）——路由判定与后续取值同一版本
        if (!PalORM_Runtime.CurrentState._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        // B20（2026-10-01 全 API 逐项轮）：单遍收集 + 默认键校验——原 ToList（全量物化）
        // + Any（第二遍枚举 + LINQ 委托）双遍开销；本形态一遍完成且零 LINQ。
        // ICollection 可预知 Count 时按量预分配（与 ToList 的预分配行为对齐）。
        List<T> items = entities is ICollection<T> sized ? new List<T>(sized.Count) : new();
        foreach (T entity in entities)
        {
            ct.ThrowIfCancellationRequested();  // ITM-911：大集合取消响应
            if (metadata.HasDefaultKey(entity))
                throw new InvalidOperationException(
                    $"Seed entity '{typeof(T).Name}' requires a non-default stable primary key.");
            items.Add(entity);
        }
        // r12-B1（D3 残留族）：空短路后置——同 BulkInsertAsync 口径
        if (items.Count == 0) return;
        await BulkMergeAsync(items, ct).ConfigureAwait(false);
    }
}

/// <summary>MySQL-7：批量 UPDATE 形态探测的缓存值类型——探测缓存本体已迁
/// <see cref="CapabilityProbeCache{T}"/>（连接串键控 + 60s TTL，EPSL 2026-10-11 审计 B 批 #2），
/// 本类型只承载探测产出（形态枚举）。命名空间级静态 holder 而非 DataSession&lt;TProvider&gt;
/// 的嵌套类型，与泛型类的闭合类型绝缘（S2743 同因）。探测逻辑本体仍在 DataSession_Bulk
/// （需要 CreateCommand 走会话口径）。</summary>
internal static class MySqlUpdateFormCacheHolder
{
    /// <summary>探测结果值（形态枚举；TTL 由缓存底座管理）。</summary>
    internal sealed record MySqlUpdateFormProbe(BatchUpdateSqlBuilder.BatchUpdateForm Form);
}

