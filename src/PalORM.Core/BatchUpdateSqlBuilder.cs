using System.Data.Common;

namespace PalORM;

/// <summary>v5.0 阶段 4.3b：批量 UPDATE SQL 构造器（静态，与 DataSession 解耦降低复杂度）。
/// <para>PG 方言（UPDATE FROM VALUES，Django 实测 4x 提速）：</para>
/// <para><c>UPDATE t AS tgt SET "a" = v.col0, "b" = v.col1 FROM (VALUES ...) AS v(col0, col1, col_pk) WHERE tgt."id" = v.col_pk</c></para>
/// <para>MySQL/SQLite 方言（CASE WHEN，跨方言通用）：</para>
/// <para><c>UPDATE t SET "a" = CASE "id" WHEN @pk0 THEN @v0a WHEN @pk1 THEN @v1a END WHERE "id" IN (@pk0, @pk1)</c></para>
/// <para>MySQL ≥ 8.0.19 方言（UPDATE JOIN table value constructor，MySQL-7 实测 8.75×）：</para>
/// <para><c>UPDATE t AS tgt JOIN (VALUES ROW(...), ROW(...)) AS v(c0, c1, pk) ON tgt."id" = v.pk SET tgt.c0 = v.c0</c></para>
/// <para><b>参数顺序</b>（每行 setColumnCount+1 个，对应 BindUpdate 的输出顺序）：
/// [setCol1, setCol2, ..., pk]。与 BindUpdate 参数序一致——SET 列先，PK 在末尾。
/// 不含租户参数（租户参数由调用方在 SQL 末尾追加）。</para>
/// </summary>
internal static class BatchUpdateSqlBuilder
{
    /// <summary>批量 UPDATE 形态——同方言下按服务端能力选择。
    /// <para><b>MySQL-7（2026-09-27）</b>：CASE WHEN 的服务端求值是 O(行数×CASE 分支数)，
    /// 单批耗时随批宽超线性（真库 20000 行实测：500 行/批 271.7ms → 5000 行/批 1373.9ms，
    /// 探针十五）。UPDATE JOIN table value constructor（MySQL 8.0.19+）用等值 JOIN
    /// 替代逐行 CASE 分支比较，同负载 156.7ms（8.75×），且 SQL 文本只有 CASE WHEN 的 1/6.7。
    /// 低于 8.0.19 的服务端不认识该语法，由调用方版本探测后回退 <see cref="CaseWhen"/>。</para></summary>
    internal enum BatchUpdateForm
    {
        /// <summary>CASE WHEN 形态——全方言兼容的基线（PG 走 FROM VALUES，不经过此枚举）。</summary>
        CaseWhen,

        /// <summary>UPDATE JOIN (VALUES ROW(...)) 形态——MySQL ≥ 8.0.19 专有。</summary>
        JoinValuesRow,
    }

    /// <summary>构造批量 UPDATE SQL。</summary>
    /// <param name="dialect">SQL 方言。</param>
    /// <param name="quotedTable">引号包裹的表名。</param>
    /// <param name="quotedPk">引号包裹的主键列名。</param>
    /// <param name="setColumns">引号包裹的 SET 列名（不含主键）。</param>
    /// <param name="rowCount">本批行数。</param>
    /// <param name="hasTenantFilter">是否追加租户过滤。</param>
    /// <param name="tenantParameterName">租户参数占位符（仅在 hasTenantFilter 时使用）。</param>
    /// <param name="form">MySQL 方言的形态选择（非 MySQL 方言忽略，恒走方言既有形态）。</param>
    /// <exception cref="ArgumentException">SET 列集为空，或 hasTenantFilter 为 true 而租户参数名为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">rowCount 小于 1。</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S107:MethodsShouldNotHaveTooManyParameters",
        Justification = "MySQL-7：form 是方言内形态参数（MySQL ≥ 8.0.19 走 JoinValuesRow），"
            + "与 dialect 二维描述同一条语句的构建；聚合为对象会把'构造参数'变成'构造输入对象'，"
            + "而调用方（DataSession_Bulk）每次调用一个批，无跨批复用——YAGNI。")]
    public static string Build(
        SqlDialect dialect, string quotedTable, string quotedPk, string[] setColumns,
        int rowCount, bool hasTenantFilter, string tenantParameterName,
        BatchUpdateForm form = BatchUpdateForm.CaseWhen)
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
        // A′（2026-10-01 充分论证轮）：形态判定单一真源——分派与租户谓词落位共用同一布尔。
        // 原两处条件不一致（分派用 `MySQL && form`，追加只用 `form`）：非 MySQL 方言忽略
        // form（既定契约，见 Build_NonMySqlDialects_IgnoreJoinValuesRowForm），届时实际形态
        // 有 WHERE 而追加按 form 误判为"无 WHERE 形态"，会生成双 WHERE（B8 修复引入的矩阵
        // 回归，当前调用面不可达但属契约内输入；本布尔使二者结构性同源，任何一方扩展自动同步）。
        bool usesJoinValuesRow =
            dialect == SqlDialect.MySql && form == BatchUpdateForm.JoinValuesRow;
        var sb = new ValueStringBuilder(stackalloc char[512]);
        try
        {
            if (dialect == SqlDialect.PostgreSql)
                BuildPostgreSql(ref sb, quotedTable, quotedPk, setColumns, rowCount, paramsPerRow);
            else if (usesJoinValuesRow)
                BuildJoinValuesRow(ref sb, quotedTable, quotedPk, setColumns, rowCount, paramsPerRow);
            else
                BuildCaseWhen(ref sb, quotedTable, quotedPk, setColumns, rowCount, paramsPerRow, setColCount);

            if (hasTenantFilter)
            {
                // v5.0 修复 P2-2：按方言选择引号——MySQL 用反引号，PG/SQLite 用双引号。
                // 之前硬编码双引号在 MySQL 默认 sql_mode 下会把 "tenant_id" 当字符串字面量。
                char q = dialect == SqlDialect.MySql ? '`' : '"';
                // B8 二修（2026-10-01 全 API 逐项轮）：JoinValuesRow 形态<b>没有 WHERE 子句</b>
                // （连接条件在 ON，语句以 SET 结尾）——原盲追加 " AND ..." 会落进 SET 表达式
                //（tgt.label = v.c1 AND tenant_id = @__tenant0），MySQL 对字符串列求布尔触发
                // "Truncated incorrect DOUBLE value"（真库 Integration 实测，租户实体 + MySQL
                // 8.0.19+ 路径此前后无覆盖）。A′：以 usesJoinValuesRow 为唯一判据落位——
                // 该形态补完整 WHERE 并以 tgt 限定列名，其余形态（皆有 WHERE）追加 AND。
                if (usesJoinValuesRow)
                {
                    sb.Append(" WHERE tgt.");
                    sb.Append(q);
                    sb.Append("tenant_id");
                    sb.Append(q);
                }
                else
                {
                    sb.Append(" AND ");
                    sb.Append(q);
                    sb.Append("tenant_id");
                    sb.Append(q);
                }
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

    /// <summary>MySQL ≥ 8.0.19 形态：UPDATE JOIN table value constructor。
    /// <para><c>UPDATE t AS tgt JOIN (VALUES ROW(@p0,@p1,@p2), ROW(@p3,@p4,@p5)) AS v(c0, c1, pk)
    /// ON tgt.pk = v.pk SET tgt.c0 = v.c0, tgt.c1 = v.c1</c></para>
    /// <para><b>与 CASE WHEN 的语义差异（输入重复主键时）</b>：CASE WHEN 对同一 pk 的多个
    /// WHEN 分支按序求值取末值（行只更新一次）；JOIN 形态下重复 pk 的匹配是一对多，
    /// 同一行按匹配次数更新（值相同则最终状态一致，但驱动 affected rows 计数膨胀）。
    /// 上游契约（IReadOnlyList 按主键唯一）不变，此处仅注释说明差异。</para>
    /// <para><b>参数序</b>：与 CASE WHEN 逐位一致——每行 ROW(setCol0..setColN, pk)，
    /// 调用方参数池布局零改动。</para></summary>
    private static void BuildJoinValuesRow(ref ValueStringBuilder sb, string quotedTable, string quotedPk,
        string[] setColumns, int rowCount, int paramsPerRow)
    {
        int setColCount = setColumns.Length;
        sb.Append("UPDATE ");
        sb.Append(quotedTable);
        sb.Append(" AS tgt JOIN (VALUES ");
        for (int r = 0; r < rowCount; r++)
        {
            if (r > 0) sb.Append(", ROW(");
            else sb.Append("ROW(");
            int baseIdx = r * paramsPerRow;
            for (int c = 0; c < setColCount; c++)
            {
                if (c > 0) sb.Append(',');
                AppendParameterName(ref sb, baseIdx + c);
            }
            sb.Append(',');
            AppendParameterName(ref sb, baseIdx + setColCount);  // pk（每行最后）
            sb.Append(')');
        }
        // 派生表列名：c0..cN + pk（SET 子句引用名）
        sb.Append(") AS v(c0");
        for (int c = 1; c < setColCount; c++)
        {
            sb.Append(", c");
            sb.Append(c);
        }
        sb.Append(", pk) ON tgt.");
        sb.Append(quotedPk);
        sb.Append(" = v.pk SET ");
        for (int c = 0; c < setColCount; c++)
        {
            if (c > 0) sb.Append(", ");
            sb.Append("tgt.");
            // setColumns 已是引号包裹列名，直接拼接（与 PG 形态 v.colN 引用对称）
            sb.Append(setColumns[c]);
            sb.Append(" = v.c");
            sb.Append(c);
        }
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
