using System.Data.Common;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>D 层场景执行器（方案 §7.5）。
/// <para>复用 A 层判据：PalORM 侧走 API，手写侧走裸 ADO 常量模板，两侧同构表按列名比对。
/// 返回 <c>true</c> 表示两侧不一致，即该场景"失败"（供收缩器作为失败谓词）。</para>
/// <para>场景只操作 <c>cf_bulk_plain</c>（三列、无过滤），因此失败只可能来自值面、条件面、
/// 批量大小面或操作序列面，不会混入租户与软删的干扰。</para>
/// <para>DIALECT-4：方言作为参数传入（SQLite / PG / MySQL 共用同一执行器），
/// 标识符引用与手写 SQL 模板由 <see cref="DialectSql"/> 按方言生成。</para></summary>
internal static class RandomDiffRunner
{
    private static readonly string[] Columns = ["Id", "name", "amount"];

    private static string SelectAll(SqlDialect dialect)
        => DialectSql.SelectAll(dialect, MirrorTable.BulkMirror, Columns);

    private static string Amount(SqlDialect dialect) => DialectSql.Quote(dialect, "amount");

    private static string Name(SqlDialect dialect) => DialectSql.Quote(dialect, "name");

    private static Dictionary<string, object?>[] Project(List<CfBulkPlainEntity> rows)
        => [.. rows.Select(entity => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = entity.Id,
            ["name"] = entity.Name,
            ["amount"] = entity.Amount
        })];

    /// <summary>执行一个场景；返回 true 表示两侧结果不一致（收缩器谓词用）。</summary>
    public static async Task<bool> HasDifferenceAsync<TProvider>(
        DScenario scenario,
        DataSession<TProvider> db,
        DbConnection raw,
        SqlDialect dialect,
        CancellationToken ct = default)
        where TProvider : IDbProvider
        => (await FindDifferencesAsync(scenario, db, raw, dialect, ct)).Count > 0;

    /// <summary>执行一个场景并返回差异明细（失败报告用：收缩后仍能指出是哪一列不一致）。</summary>
    public static async Task<IReadOnlyList<RowComparer.Difference>> FindDifferencesAsync<TProvider>(
        DScenario scenario,
        DataSession<TProvider> db,
        DbConnection raw,
        SqlDialect dialect,
        CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        await MirrorTable.CleanAsync(raw, dialect, ct);

        CfBulkPlainEntity[] rows =
        [
            .. scenario.Values.Select((value, index) => new CfBulkPlainEntity
            {
                Id = index + 1,
                Name = scenario.Names[index],
                Amount = value
            })
        ];

        switch (scenario.Sequence)
        {
            case OperationSequence.InsertThenQuery:
                await InsertBothSidesAsync(db, raw, rows, dialect, ct);
                break;
            case OperationSequence.InsertThenDeleteThenQuery:
                await InsertBothSidesAsync(db, raw, rows, dialect, ct);
                await DeleteHalfBothSidesAsync(db, raw, rows, dialect, ct);
                break;
            case OperationSequence.QueryThenInsertThenQuery:
            {
                IReadOnlyList<RowComparer.Difference> pre = await CompareAsync(scenario, db, raw, dialect, ct);
                if (pre.Count > 0) return pre;
                await InsertBothSidesAsync(db, raw, rows, dialect, ct);
                break;
            }
            case OperationSequence.UpdateThenQuery:
                await InsertBothSidesAsync(db, raw, rows, dialect, ct);
                await RenameAllBothSidesAsync(db, raw, rows, dialect, ct);
                break;
        }

        return await CompareAsync(scenario, db, raw, dialect, ct);
    }

    private static async Task InsertBothSidesAsync<TProvider>(
        DataSession<TProvider> db, DbConnection raw, CfBulkPlainEntity[] rows,
        SqlDialect dialect, CancellationToken ct)
        where TProvider : IDbProvider
    {
        if (rows.Length == 0) return;
        await db.BulkInsertAsync(rows, ct);

        string sql = DialectSql.Insert(dialect, MirrorTable.BulkMirror, Columns, rows.Length);
        var parameters = new List<(string Name, object? Value)>(rows.Length * 3);
        for (int i = 0; i < rows.Length; i++)
        {
            parameters.Add(($"@p{i * 3}", rows[i].Id));
            parameters.Add(($"@p{i * 3 + 1}", rows[i].Name));
            parameters.Add(($"@p{i * 3 + 2}", rows[i].Amount));
        }
        await MirrorTable.ExecuteAsync(raw, dialect, sql, parameters, ct);
    }

    private static async Task DeleteHalfBothSidesAsync<TProvider>(
        DataSession<TProvider> db, DbConnection raw, CfBulkPlainEntity[] rows,
        SqlDialect dialect, CancellationToken ct)
        where TProvider : IDbProvider
    {
        if (rows.Length < 2) return;
        long[] keys = [.. rows.Take(rows.Length / 2).Select(r => r.Id)];
        if (keys.Length == 0) return;

        await db.BulkDeleteAsync<CfBulkPlainEntity, long>(keys, ct);
        await MirrorTable.ExecuteAsync(
            raw, dialect,
            DialectSql.DeleteByKeys(dialect, MirrorTable.BulkMirror, "Id", keys.Length),
            [.. keys.Select((key, i) => ($"@p{i}", (object?)key))], ct);
    }

    private static async Task RenameAllBothSidesAsync<TProvider>(
        DataSession<TProvider> db, DbConnection raw, CfBulkPlainEntity[] rows,
        SqlDialect dialect, CancellationToken ct)
        where TProvider : IDbProvider
    {
        if (rows.Length == 0) return;
        foreach (CfBulkPlainEntity row in rows) row.Name = $"u-{row.Name}";
        await db.BulkUpdateAsync(rows, ct);

        foreach (CfBulkPlainEntity row in rows)
        {
            await MirrorTable.ExecuteAsync(
                raw, dialect,
                DialectSql.UpdateColumn(dialect, MirrorTable.BulkMirror, "name", "Id"),
                [("@p0", row.Name), ("@p1", row.Id)], ct);
        }
    }

    private static async Task<IReadOnlyList<RowComparer.Difference>> CompareAsync<TProvider>(
        DScenario scenario, DataSession<TProvider> db, DbConnection raw,
        SqlDialect dialect, CancellationToken ct)
        where TProvider : IDbProvider
    {
        long first = scenario.Values.Length > 0 ? scenario.Values[0] : 0L;
        long second = scenario.Values.Length > 1 ? scenario.Values[1] : first;
        long[] allKeys = [.. scenario.Values.Select((_, index) => (long)(index + 1))];

        List<CfBulkPlainEntity> palormRows;
        string handwrittenSql;
        List<(string Name, object? Value)> parameters = [];

        switch (scenario.Condition)
        {
            case ConditionKind.Single:
                palormRows = await db.From<CfBulkPlainEntity>().Where($"amount > {first}").ToListAsync(ct);
                handwrittenSql = $"{SelectAll(dialect)} WHERE {Amount(dialect)} > @p0";
                parameters.Add(("@p0", first));
                break;
            case ConditionKind.AndChain:
                palormRows = await db.From<CfBulkPlainEntity>()
                    .Where($"amount > {first}").Where($"amount < {second}").ToListAsync(ct);
                handwrittenSql =
                    $"{SelectAll(dialect)} WHERE ({Amount(dialect)} > @p0) AND ({Amount(dialect)} < @p1)";
                parameters.Add(("@p0", first));
                parameters.Add(("@p1", second));
                break;
            case ConditionKind.OrChain:
                palormRows = await db.From<CfBulkPlainEntity>()
                    .Where($"amount = {first}").OrWhere($"amount = {second}").ToListAsync(ct);
                handwrittenSql =
                    $"{SelectAll(dialect)} WHERE ({Amount(dialect)} = @p0 OR {Amount(dialect)} = @p1)";
                parameters.Add(("@p0", first));
                parameters.Add(("@p1", second));
                break;
            case ConditionKind.Mixed:
                // 列名是 SQL 结构位置：先拼纯文本再交给 FormattableStringFactory；
                // 直接 $"{Name(dialect)} LIKE 'p%'" 会把列名当参数值（@p0 LIKE 'p%'）。
                string likeName = $"{Name(dialect)} LIKE 'p%'";
                palormRows = await db.From<CfBulkPlainEntity>()
                    .Where($"amount = {first}")
                    .OrWhere(System.Runtime.CompilerServices.FormattableStringFactory.Create(likeName))
                    .Where($"amount <> {second}")
                    .ToListAsync(ct);
                // 与文档承诺形态一致：((A) OR (B)) AND (C)
                handwrittenSql = $"{SelectAll(dialect)} WHERE (({Amount(dialect)} = @p0) "
                    + $"OR ({Name(dialect)} LIKE 'p%')) AND ({Amount(dialect)} <> @p1)";
                parameters.Add(("@p0", first));
                parameters.Add(("@p1", second));
                break;
            case ConditionKind.InEmpty:
                palormRows = await db.From<CfBulkPlainEntity>()
                    .WhereIn(e => e.Id, Array.Empty<long>()).ToListAsync(ct);
                handwrittenSql = $"{SelectAll(dialect)} WHERE 1 = 0";
                break;
            case ConditionKind.InFull:
                palormRows = await db.From<CfBulkPlainEntity>().WhereIn(e => e.Id, allKeys).ToListAsync(ct);
                handwrittenSql = allKeys.Length == 0
                    ? $"{SelectAll(dialect)} WHERE 1 = 0"
                    : $"{SelectAll(dialect)} WHERE {DialectSql.Quote(dialect, "Id")} IN "
                        + $"({string.Join(", ", Enumerable.Range(0, allKeys.Length).Select(i => $"@p{i}"))})";
                for (int i = 0; i < allKeys.Length; i++) parameters.Add(($"@p{i}", allKeys[i]));
                break;
            case ConditionKind.Like:
            {
                string likeOnly = $"{Name(dialect)} LIKE 'p%'";
                palormRows = await db.From<CfBulkPlainEntity>()
                    .Where(System.Runtime.CompilerServices.FormattableStringFactory.Create(likeOnly))
                    .ToListAsync(ct);
                handwrittenSql = $"{SelectAll(dialect)} WHERE {Name(dialect)} LIKE 'p%'";
                break;
            }
            default:
                palormRows = await db.From<CfBulkPlainEntity>().ToListAsync(ct);
                handwrittenSql = SelectAll(dialect);
                break;
        }

        IReadOnlyList<IReadOnlyDictionary<string, object?>> handwrittenRows =
            await MirrorTable.QueryAsync(raw, dialect, handwrittenSql, parameters, ct);

        return RowComparer.Compare(handwrittenRows, Project(palormRows));
    }
}