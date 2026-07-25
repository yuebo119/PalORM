using System.Text;

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
    public static string Build(
        SqlDialect dialect, string quotedTable, string quotedPk, string[] setColumns,
        int rowCount, bool hasTenantFilter, string tenantParameterName)
    {
        int setColCount = setColumns.Length;
        int paramsPerRow = setColCount + 1;  // pk + set cols
        var sb = new StringBuilder();

        if (dialect == SqlDialect.PostgreSql)
            BuildPostgreSql(sb, quotedTable, quotedPk, setColumns, rowCount, paramsPerRow);
        else
            BuildCaseWhen(sb, quotedTable, quotedPk, setColumns, rowCount, paramsPerRow, setColCount);

        if (hasTenantFilter)
            sb.Append(" AND ").Append('"').Append("tenant_id").Append('"').Append(" = ").Append(tenantParameterName);

        return sb.ToString();
    }

    private static void BuildPostgreSql(StringBuilder sb, string quotedTable, string quotedPk,
        string[] setColumns, int rowCount, int paramsPerRow)
    {
        int setColCount = setColumns.Length;
        // 参数顺序对应 BindUpdate: [setCol0, setCol1, ..., pk]
        // VALUES 里每行: (col0, col1, ..., pk) —— pk 放最后
        sb.Append("UPDATE ").Append(quotedTable).Append(" AS tgt SET ");
        for (int c = 0; c < setColCount; c++)
        {
            if (c > 0) sb.Append(", ");
            sb.Append(setColumns[c]).Append(" = v.col").Append(c);
        }
        sb.Append(" FROM (VALUES ");
        for (int r = 0; r < rowCount; r++)
        {
            if (r > 0) sb.Append(", ");
            AppendValueRow(sb, r * paramsPerRow, setColCount);
        }
        // v(col0, col1, ..., col_pk)——pk 列放最后，名为 col_pk
        sb.Append(") AS v(col0");
        for (int c = 1; c < setColCount; c++)
            sb.Append(", col").Append(c);
        sb.Append(", col_pk) WHERE tgt.").Append(quotedPk).Append(" = v.col_pk");
    }

    private static void AppendValueRow(StringBuilder sb, int baseIdx, int setColCount)
    {
        sb.Append('(');
        sb.Append("@p").Append(baseIdx);  // setCol0
        for (int c = 1; c < setColCount; c++)
            sb.Append(", @p").Append(baseIdx + c);
        sb.Append(", @p").Append(baseIdx + setColCount);  // pk（最后）
        sb.Append(')');
    }

    private static void BuildCaseWhen(StringBuilder sb, string quotedTable, string quotedPk,
        string[] setColumns, int rowCount, int paramsPerRow, int setColCount)
    {
        // 参数顺序对应 BindUpdate: [setCol0, setCol1, ..., pk]
        // CASE 子句每行: WHEN @p_pk THEN @p_setCol
        // 其中 @p_pk = baseIdx + setColCount（pk 在每行末尾），@p_setCol = baseIdx + c
        sb.Append("UPDATE ").Append(quotedTable).Append(" SET ");
        for (int c = 0; c < setColCount; c++)
        {
            if (c > 0) sb.Append(", ");
            sb.Append(setColumns[c]).Append(" = CASE ").Append(quotedPk);
            for (int r = 0; r < rowCount; r++)
            {
                int baseIdx = r * paramsPerRow;
                int pkIdx = baseIdx + setColCount;  // pk 在每行末尾
                sb.Append(" WHEN @p").Append(pkIdx).Append(" THEN @p").Append(baseIdx + c);
            }
            sb.Append(" END");
        }
        sb.Append(" WHERE ").Append(quotedPk).Append(" IN (");
        for (int r = 0; r < rowCount; r++)
        {
            if (r > 0) sb.Append(", ");
            sb.Append("@p").Append(r * paramsPerRow + setColCount);  // pk 在每行末尾
        }
        sb.Append(')');
    }
}
