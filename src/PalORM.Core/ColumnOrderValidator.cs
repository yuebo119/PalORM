using System.Data.Common;

namespace PalORM;

/// <summary>调用方原生 SQL/存储过程 + ordinal 映射路径的列序校验（ADR-A）。
/// QueryAsync 族、GridReader、StoredProcBuilder 共用——同型列错位会静默交换数据，
/// 三条路径必须一致防御（ITM-202）。</summary>
internal static class ColumnOrderValidator
{
    /// <summary>校验结果列序 = 实体声明列序。结果列数少于实体列数时同样明确失败
    /// （ITM-211：RowFactory 会对缺失列抛 IndexOutOfRange，此处提前给出可诊断的消息）；
    /// 多余列不校验（调用方可 SELECT 附加列，RowFactory 只读前 N 列）。</summary>
    internal static void Validate<T>(DbDataReader reader, bool enabled) where T : class, new()
    {
        if (!enabled) return;
        if (!PalORM_Runtime.ColumnNames.TryGetValue(typeof(T), out IReadOnlyList<string>? expected))
            return;
        if (reader.FieldCount < expected.Count)
        {
            throw new InvalidOperationException(
                $"Query returned {reader.FieldCount} columns but entity '{typeof(T).Name}' declares " +
                $"{expected.Count} mapped columns; ordinal materialization would fail. " +
                "Select all mapped columns in declaration order, or set " +
                "DbOptions.ValidateQueryColumnOrder = false and handle partial projection yourself.");
        }
        for (int i = 0; i < expected.Count; i++)
        {
            string actual = reader.GetName(i);
            if (!string.Equals(actual, expected[i], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Query column order mismatch for '{typeof(T).Name}': result column {i} is '{actual}' " +
                    $"but entity declaration order expects '{expected[i]}'. Ordinal mapping would silently " +
                    "misassign data. List columns explicitly in entity declaration order, or set " +
                    "DbOptions.ValidateQueryColumnOrder = false if you use aliases and guarantee order yourself.");
            }
        }
    }
}
