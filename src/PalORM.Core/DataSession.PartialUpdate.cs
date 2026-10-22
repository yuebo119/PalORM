using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace PalORM;

public sealed partial class DataSession<TProvider>
{
    /// <summary>按列子集更新实体——只把指定属性写入 SET，未选中的列保持数据库当前值。
    /// <para><b>为什么需要</b>：默认 <see cref="UpdateAsync{T}"/> 固定 SET 全部可更新列；
    /// 宽表（几十列、含大文本/二进制）只改一两列时，整行 SET 会放大网络负载与服务端写放大。
    /// 本 API 复用生成器的 <c>BindUpdate</c> 完成值转换（转换器/OwnedJson/枚举存储形态均同源），
    /// 再裁掉未选中的 SET 参数并重编号——SQL 只引用被改列，未选参数不会发送。</para>
    /// <para><b>语义</b>：主键与并发令牌（<c>[ConcurrencyCheck]</c>）恒进 WHERE；版本列照常
    /// <c>version = version + 1</c>，受影响 0/非 1 行抛 <see cref="ConcurrencyConflictException"/>，
    /// 成功后回填内存 version；租户会话自动追加 <c>tenant_id = @__tenant0</c>；活动事务照常绑定。
    /// 选择主键、并发令牌、租户列、计算列或非映射属性会在访问数据库前明确失败。</para>
    /// <para><b>性能</b>：新生成器为选中列 + 主键/版本逐列建参，客户端参数分配随子集下降
    /// （10 列 SQLite 每操作 A/B：4104 → 3216 B/op，约 −22%）；旧生成器程序集回退“整行建参后
    /// 裁剪”的兼容路径。SQL 文本与服务端 SET 列数同样只含选中列。</para></summary>
    /// <typeparam name="T">实体类型。</typeparam>
    /// <param name="entity">携带新值与主键/版本值的实体。</param>
    /// <param name="columns">要更新的属性表达式（集合表达式，如 <c>[x =&gt; x.Name, x =&gt; x.Score]</c>），
    /// 至少一个；重复项自动去重。</param>
    /// <param name="ct">取消令牌（可选）。</param>
    /// <returns>受影响行数（带并发令牌时恒为 1，0/非 1 抛并发冲突）。</returns>
    public ValueTask<int> UpdateColumnsAsync<T>(
        T entity, ReadOnlySpan<Expression<Func<T, object?>>> columns, CancellationToken ct = default)
        where T : class, new()
        => UpdateColumnsEntryAsync(entity, columns.ToArray(), ct);

    /// <summary>单列快捷重载——等价于 <see cref="UpdateColumnsAsync{T}(T, ReadOnlySpan{Expression{Func{T, object?}}}, CancellationToken)"/>
    /// 传入单元素集合。</summary>
    /// <typeparam name="T">实体类型。</typeparam>
    /// <param name="entity">携带新值与主键/版本值的实体。</param>
    /// <param name="column">要更新的属性表达式。</param>
    /// <param name="ct">取消令牌（可选）。</param>
    /// <returns>受影响行数。</returns>
    public ValueTask<int> UpdateColumnsAsync<T>(
        T entity, Expression<Func<T, object?>> column, CancellationToken ct = default)
        where T : class, new()
        => UpdateColumnsEntryAsync(entity, [column], ct);

    private async ValueTask<int> UpdateColumnsEntryAsync<T>(
        T entity, Expression<Func<T, object?>>[] columns, CancellationToken ct)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Length == 0)
            throw new ArgumentException("At least one updatable column must be specified.", nameof(columns));

        EntityRuntimeInfo info = GetEntityInfo<T>();
        CrudPlan plan = GetCrudPlan<T>(info);
        Action<DbCommand, object> bindUpdate = plan.BindUpdate
            ?? throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated update binder; recompile the model assembly against the current PalORM version.");
        if (plan.UpdateColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no updatable columns.");

        SortedSet<int> selected = ResolveUpdateColumnSelection<T>(plan, columns);
        bool tenantFiltered = HasTenantFilter(info.Features);

        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        DbTransaction? transaction = GetActiveTransaction();
        string sql = GetPartialUpdateSql<T>(info, plan, selected, tenantFiltered);

        await using DbCommand cmd = CreateCommand(transaction);
        cmd.CommandText = sql;
        cmd.CommandTimeout = _commandTimeoutSeconds;
        if (plan.BindUpdateColumn is { } bindColumn && plan.BindUpdateWhere is { } bindWhere)
        {
            // 新生成器：只为选中 SET 列 + 主键/版本建参（客户端分配降到子集量级）；
            // 值表达式与全量 BindUpdate 同一生成真源（转换器/OwnedJson/枚举/类型提示）。
            int parameterIndex = 0;
            foreach (int oldIndex in selected)
            {
                DbParameter parameter = TProvider.CreateParameter(
                    ParameterNameCache.GetName(parameterIndex), DBNull.Value);
                bindColumn(entity, parameter, oldIndex);
                cmd.Parameters.Add(parameter);
                parameterIndex++;
            }
            bindWhere(entity, cmd, parameterIndex);
        }
        else
        {
            // 旧生成器兼容：按完整 SET 列序建参后裁掉未选中项，再把保留参数重编号为 @p0…。
            bindUpdate(cmd, entity);
            // PARAM-REUSE-OK[fresh] 本命令每次操作新建，参数集合仅本次绑定后裁剪一次，不跨执行复用
            for (int index = plan.UpdateColumns.Count - 1; index >= 0; index--)
            {
                if (!selected.Contains(index))
                    cmd.Parameters.RemoveAt(index);
            }
            for (int index = 0; index < cmd.Parameters.Count; index++)
                cmd.Parameters[index].ParameterName = ParameterNameCache.GetName(index);
        }
        if (tenantFiltered)
            BindDefaultFilterParameters(cmd, info.Features);

        int affectedRows = await ExecuteWriteRowsAsync(cmd, transaction, ct).ConfigureAwait(false);
        return ApplyUpdateOutcome(plan, entity, affectedRows, deferredVersionIncrements: null);
    }

    /// <summary>解析列选择：属性表达式 → 列名 → <see cref="CrudPlan.UpdateColumns"/> 索引；
    /// 去重后按原列序返回（参数顺序 = SET 子句顺序）。</summary>
    private static SortedSet<int> ResolveUpdateColumnSelection<T>(
        CrudPlan plan, Expression<Func<T, object?>>[] columns)
        where T : class, new()
    {
        var selected = new SortedSet<int>();
        foreach (Expression<Func<T, object?>> column in columns)
        {
            ArgumentNullException.ThrowIfNull(column, nameof(columns));
            string columnName = MemberResolver.GetColumnName(column);
            int index = -1;
            for (int i = 0; i < plan.UpdateColumns.Count; i++)
            {
                if (string.Equals(plan.UpdateColumns[i], columnName, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                throw new ArgumentException(
                    $"Column '{columnName}' of '{typeof(T).Name}' is not updatable (primary key, concurrency token, tenant, computed or non-mapped columns cannot be selected).",
                    nameof(columns));
            selected.Add(index);
        }
        return selected;
    }

    /// <summary>构建列子集 UPDATE 全句——从生成的全列 UPDATE 中提取 WHERE 子句（主键 +
    /// 并发令牌谓词）与版本列引用，重编号参数后按 (T, 方言, 选中列集, 租户位) 缓存。
    /// <para><b>格式依赖</b>：生成器的 UPDATE 恒为 <c>UPDATE … SET … WHERE …</c>，并发令牌
    /// 谓词是 WHERE 的最后一个 <c>AND</c> 分支。列子集不同只改 SET 段，WHERE 文本结构稳定。</para></summary>
    private static string GetPartialUpdateSql<T>(
        EntityRuntimeInfo info, CrudPlan plan, SortedSet<int> selected, bool tenantFiltered)
        where T : class, new()
    {
        string columnKey = string.Join(",", selected);
        (Type, SqlDialect, string, bool) cacheKey = (typeof(T), TProvider.Dialect, columnKey, tenantFiltered);
        if (DataSessionCache.PartialUpdateSqlCache.TryGetValue(cacheKey, out string? cached))
            return cached;

        CommandSqlSet sqls = GetCommandSqls<T>();
        string fullSql = sqls.Update;
        if (fullSql.Length == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no updatable columns.");
        int whereIndex = fullSql.LastIndexOf(" WHERE ", StringComparison.Ordinal);
        if (whereIndex < 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' generated UPDATE SQL has no WHERE clause.");
        string whereClause = fullSql[whereIndex..];

        string versionSet = "";
        if (plan.IncrementVersion is not null)
        {
            // 并发令牌谓词 = WHERE 的最后一个 AND 分支（生成器保证）；左端即版本列引用。
            int andIndex = whereClause.LastIndexOf(" AND ", StringComparison.Ordinal);
            int equalsIndex = whereClause.LastIndexOf(" = @p", StringComparison.Ordinal);
            if (andIndex < 0 || equalsIndex <= andIndex)
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' generated UPDATE SQL is missing the concurrency predicate.");
            string versionColumn = whereClause[(andIndex + " AND ".Length)..equalsIndex].Trim();
            versionSet = $", {versionColumn} = {versionColumn} + 1";
        }

        int setColumnCount = plan.UpdateColumns.Count;
        var setItems = new string[selected.Count];
        int newIndex = 0;
        foreach (int oldIndex in selected)
        {
            setItems[newIndex] = $"{TProvider.QuoteIdentifier(plan.UpdateColumns[oldIndex])} = {ParameterNameCache.GetName(newIndex)}";
            newIndex++;
        }

        // WHERE 只引用主键/并发参数（旧编号 >= SET 列数）；保留参数中的新编号 =
        // 选中 SET 数 + (旧编号 - SET 列数)。
        string rewrittenWhere = Regex.Replace(
            whereClause,
            "@p(\\d+)",
            match =>
            {
                int oldIndex = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return oldIndex >= setColumnCount
                    ? ParameterNameCache.GetName(selected.Count + (oldIndex - setColumnCount))
                    : match.Value;
            },
            RegexOptions.None,
            TimeSpan.FromSeconds(1));
        string tenantSuffix = tenantFiltered
            ? $" AND {TProvider.QuoteIdentifier("tenant_id")} = {_tenantParameterName}"
            : "";
        string built =
            $"UPDATE {TProvider.QuoteIdentifier(info.TableName)} SET {string.Join(", ", setItems)}" +
            $"{versionSet}{rewrittenWhere}{tenantSuffix}";
        DataSessionCache.PartialUpdateSqlCache.TryAdd(cacheKey, built);
        return built;
    }
}
