using System.Data.Common;

namespace PalORM;

/// <summary>调用方原生 SQL/存储过程 + ordinal 映射路径的列序校验（ADR-A）。
/// QueryAsync 族、GridReader、StoredProcBuilder 共用——同型列错位会静默交换数据，
/// 三条路径必须一致防御（ITM-202）。</summary>
internal static class ColumnOrderValidator
{
    /// <summary>校验结果列序 = 实体声明列序。列数少于实体列数时仅校验重叠部分——
    /// 超出部分由 RowFactory 读取时抛 IndexOutOfRange（非静默）。</summary>
    internal static void Validate<T>(DbDataReader reader, bool enabled) where T : class, new()
    {
        if (!enabled) return;
        if (!PalORM_Runtime.ColumnNames.TryGetValue(typeof(T), out IReadOnlyList<string>? expected))
            return;
        int count = Math.Min(reader.FieldCount, expected.Count);
        for (int i = 0; i < count; i++)
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
