using System.Data.Common;

namespace PalORM;

/// <summary>v5.0 阶段 4.3b：批量 UPDATE SQL 构造器（静态，与 DataSession 解耦降低复杂度）。
/// <para>PG 方言（UPDATE FROM VALUES，Django 实测 4x 提速）：</para>
/// <para><c>UPDATE t AS tgt SET "a" = v.col0, "b" = v.col1 FROM (VALUES ...) AS v(col0, col1, col_pk) WHERE tgt."id" = v.col_pk</c></para>
/// <para>MySQL/SQLite 方言（CASE WHEN，跨方言通用）：</para>
/// <para><c>UPDATE t SET "a" = CASE "id" WHEN @pk0 THEN @v0a WHEN @pk1 THEN @v1a END WHERE "id" IN (@pk0, @pk1)</c></para>
/// <para><b>参数顺序</b>（每行 setColumnCount+1 个，对应 BindUpdate 的输出顺序）：
/// [setCol1, setCol2, ..., pk]。与 BindUpdate 参数序一致——SET 列先，PK 在末尾。</para>
/// 不含租户参数（租户参数由调用方在 SQL 末尾追加）。</summary>
internal static class BatchUpdateSqlBuilder
{
    /// <summary>构造批量 UPDATE SQL。</summary>
    /// <param name="dialect">SQL 方言。</param>
    /// <param name="quotedTable">引号包裹的表名。</param>
    /// <param name="quotedPk">引号包裹的主键列名。</param>
    /// <param name="setColumns">引号包裹的 SET 列名（不含主键）。</param>
    /// <param name="rowCount">本批行数。</param>
    /// <param name="hasTenantFilter">是否追加租户过滤。</param>
    /// <param name="tenantParameterName">租户参数占位符（仅在 hasTenantFilter 时使用）。</param>
    /// <exception cref="ArgumentException">SET 列集为空，或 hasTenantFilter 为 true 而租户参数名为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">rowCount 小于 1。</exception>
    public static string Build(
        SqlDialect dialect, string quotedTable, string quotedPk, string[] setColumns,
        int rowCount, bool hasTenantFilter, string tenantParameterName)
    {
        ArgumentNullException.ThrowIfNull(setColumns);
        // R9：空 SET 集会生成 "UPDATE t AS tgt SET  FROM ..." / "UPDATE t SET  WHEN ..."，
        // rowCount=0 会生成 "FROM (VALUES ) AS v(...)" / "IN ()"——上游虽有守卫
        // （DataSession_Bulk 的 setColumnCount 检查与 rowsPerBatch 下限），本类是公开给
        // 三 Provider 的静态构造器，防御必须自带。
        if (setColumns.Length == 0)
            throw new ArgumentException("Batch UPDATE requires at least one SET column.", nameof(setColumns));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);
        // R10：租户参数名直接拼进 SQL 文本——当前值源自内部常量，开放为可配置项前
        // 先在校验上闭合注入面（白名单与 IdentifierSafety 同族）。
        if (hasTenantFilter && !IsSafeParameterName(tenantParameterName))
            throw new ArgumentException(
                "Tenant filter parameter name must start with '@' or ':' and contain only letters, digits and underscores.",
                nameof(tenantParameterName));

        int setColCount = setColumns.Length;
        int paramsPerRow = setColCount + 1;  // pk + set cols
        var sb = new ValueStringBuilder(stackalloc char[512]);
        try
        {
            if (dialect == SqlDialect.PostgreSql)
                BuildPostgreSql(ref sb, quotedTable, quotedPk, setColumns, rowCount, paramsPerRow);
            else
                BuildCaseWhen(ref sb, quotedTable, quotedPk, setColumns, rowCount, paramsPerRow, setColCount);

            if (hasTenantFilter)
            {
                // v5.0 修复 P2-2：按方言选择引号——MySQL 用反引号，PG/SQLite 用双引号。
                // 之前硬编码双引号在 MySQL 默认 sql_mode 下会把 "tenant_id" 当字符串字面量。
                char q = dialect == SqlDialect.MySql ? '`' : '"';
                sb.Append(" AND ");
                sb.Append(q);
                sb.Append("tenant_id");
                sb.Append(q);
                sb.Append(" = ");
                sb.Append(tenantParameterName);
            }
            sb.TrimEnd();
            return sb.ToString();
        }
        finally { sb.Dispose(); }
    }

    /// <summary>参数名合法性——必须以 @ 或 : 开头，其余仅含字母数字下划线（与三方言参数命名一致）。</summary>
    private static bool IsSafeParameterName(string? name)
    {
        if (string.IsNullOrEmpty(name) || (name[0] != '@' && name[0] != ':'))
            return false;
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }
        return true;
    }

    private static void BuildPostgreSql(ref ValueStringBuilder sb, string quotedTable, string quotedPk,
        string[] setColumns, int rowCount, int paramsPerRow)
    {
        int setColCount = setColumns.Length;
        // 参数顺序对应 BindUpdate: [setCol0, setCol1, ..., pk]
        // VALUES 里每行: (col0, col1, ..., pk) —— pk 放最后
        sb.Append("UPDATE ");
        sb.Append(quotedTable);
        sb.Append(" AS tgt SET ");
        for (int c = 0; c < setColCount; c++)
        {
            if (c > 0) sb.Append(", ");
            sb.Append(setColumns[c]);
            sb.Append(" = v.col");
            sb.Append(c);
        }
        sb.Append(" FROM (VALUES ");
        for (int r = 0; r < rowCount; r++)
        {
            if (r > 0) sb.Append(", ");
            AppendValueRow(ref sb, r * paramsPerRow, setColCount);
        }
        // v(col0, col1, ..., col_pk)——pk 列放最后，名为 col_pk
        sb.Append(") AS v(col0");
        for (int c = 1; c < setColCount; c++)
        {
            sb.Append(", col");
            sb.Append(c);
        }
        sb.Append(", col_pk) WHERE tgt.");
        sb.Append(quotedPk);
        sb.Append(" = v.col_pk");
    }

    /// <summary>写单行 VALUES 占位符 <c>(@pN, @pN+1, ..., @p{pk})</c>——数字直写 VSB，
    /// 不经 int→string 中间分配（M7）。</summary>
    private static void AppendValueRow(ref ValueStringBuilder sb, int baseIdx, int setColCount)
    {
        sb.Append('(');
        AppendParameterName(ref sb, baseIdx);
        for (int c = 1; c < setColCount; c++)
        {
            sb.Append(", ");
            AppendParameterName(ref sb, baseIdx + c);
        }
        sb.Append(", ");
        AppendParameterName(ref sb, baseIdx + setColCount);  // pk（最后）
        sb.Append(')');
    }

    private static void AppendParameterName(ref ValueStringBuilder sb, int index)
    {
        sb.Append('@');
        sb.Append('p');
        sb.Append(index);
    }

    private static void BuildCaseWhen(ref ValueStringBuilder sb, string quotedTable, string quotedPk,
        string[] setColumns, int rowCount, int paramsPerRow, int setColCount)
    {
        // 参数顺序对应 BindUpdate: [setCol0, setCol1, ..., pk]
        // CASE 子句每行: WHEN @p_pk THEN @p_setCol
        // 其中 @p_pk = baseIdx + setColCount（pk 在每行末尾），@p_setCol = baseIdx + c
        sb.Append("UPDATE ");
        sb.Append(quotedTable);
        sb.Append(" SET ");
        for (int c = 0; c < setColCount; c++)
        {
            if (c > 0) sb.Append(", ");
            sb.Append(setColumns[c]);
            sb.Append(" = CASE ");
            sb.Append(quotedPk);
            for (int r = 0; r < rowCount; r++)
            {
                int baseIdx = r * paramsPerRow;
                int pkIdx = baseIdx + setColCount;  // pk 在每行末尾
                sb.Append(" WHEN ");
                AppendParameterName(ref sb, pkIdx);
                sb.Append(" THEN ");
                AppendParameterName(ref sb, baseIdx + c);
            }
            sb.Append(" END");
        }
        sb.Append(" WHERE ");
        sb.Append(quotedPk);
        sb.Append(" IN (");
        for (int r = 0; r < rowCount; r++)
        {
            if (r > 0) sb.Append(", ");
            AppendParameterName(ref sb, r * paramsPerRow + setColCount);  // pk 在每行末尾
        }
        sb.Append(')');
    }

    /// <summary>为批量 UPDATE 的目标命令建立参数池：<c>@p0…@p{rowParamCount-1}</c> 按行递增
    /// （SET 列在前、主键在每行末尾），租户参数以固定名追加在末尾——与 <see cref="Build"/>
    /// 的占位符逐位对应，并按批大小把池内参数挂到命令集合。
    /// <para><b>池的意义在 v5.6 才成立</b>：上一版只把参数创建挪进池、取值仍靠 probe 逐行
    /// <c>BindUpdate</c> 建参数，创建总量不变——真库 A/B 实测净负收益，已回退。现在取值走
    /// <c>CrudMetadata.BindUpdateValues</c>（零 CreateParameter），池才真正消除每行参数创建。</para>
    /// <para><b>为什么抽成静态可测</b>：调用方是 PG/MySQL 的批量 UPDATE，本地 SQLite 走逐条
    /// 回退、到不了该分支，故「参数名与顺序 == SQL 占位符」这条跨 Provider 契约只能靠单测
    /// 锁定——见 <c>BatchUpdateParameterContractTests</c>。</para></summary>
    internal static DbParameter[] CreateParameterPool(
        DbCommand cmd, int rowParamCount, bool hasTenantFilter,
        string tenantParameterName, object? tenantId,
        Func<string, object?, DbParameter> createParameter)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        DbParameter[] pool = CreateParameterArray(
            rowParamCount, hasTenantFilter, tenantParameterName, tenantId, createParameter);
        foreach (DbParameter parameter in pool)
            cmd.Parameters.Add(parameter);
        return pool;
    }

    /// <summary><b>M1</b>：仅创建参数数组（不挂到命令集合）——批量 UPDATE 的跨批复用路径由
    /// 调用方按批大小自行收敛命令参数集合，参数对象全程只建一次。
    /// 命名/顺序契约与 <see cref="CreateParameterPool"/> 一致（同一实现）。</summary>
    internal static DbParameter[] CreateParameterArray(
        int rowParamCount, bool hasTenantFilter,
        string tenantParameterName, object? tenantId,
        Func<string, object?, DbParameter> createParameter)
    {
        ArgumentNullException.ThrowIfNull(createParameter);
        ArgumentOutOfRangeException.ThrowIfNegative(rowParamCount);
        if (hasTenantFilter && !IsSafeParameterName(tenantParameterName))
            throw new ArgumentException(
                "Tenant filter parameter name must start with '@' or ':' and contain only letters, digits and underscores.",
                nameof(tenantParameterName));
        var pool = new DbParameter[rowParamCount + (hasTenantFilter ? 1 : 0)];
        for (int i = 0; i < rowParamCount; i++)
            pool[i] = createParameter(ParameterNameCache.GetName(i), DBNull.Value);
        if (hasTenantFilter)
            pool[rowParamCount] = createParameter(tenantParameterName, tenantId);
        return pool;
    }
}
