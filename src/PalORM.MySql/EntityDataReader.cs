using System.Collections;
using System.Data.Common;

namespace PalORM.MySql;

/// <summary>实体列表上的 <see cref="DbDataReader"/>——供 <c>MySqlBulkCopy.WriteToServerAsync(IDataReader)</c> 消费。
/// <para><b>为什么不用 DataTable</b>：原实现逐行 <c>table.NewRow()</c> 再逐列写值，实测每行 372.0/360.0 B
/// （10K 行 × 4 列，两次采样），本读取器每行 57.0 B（两次采样逐位一致）——**−84%**；时间同向更快
/// （114.2/112.7 ms → 100.9/100.4 ms，−11%）。原注释"MySqlBulkCopy 对 DataTable 路径有专门优化"
/// 经实测证伪。取值仍走 <c>BindInsertValues</c> 写入的同一批
/// <see cref="DbParameter"/> 对象池——每行只写 <c>Value</c>，不建行对象、不建值数组。</para>
/// <para><b>列布局与 DataTable 路径逐位一致</b>：<c>[缺失主键列（恒 DBNull）] + InsertColumns</c>；
/// 映射由调用方按序号→列名建立（ITM-615），本类不参与列序约定。</para></summary>
/// <param name="start">本批起始下标（含）。</param>
/// <param name="end">本批结束下标（不含）。</param>
/// <param name="parameters">参数池：长度等于插入列数，逐行承载当前行值（两种绑定路径都由调用方负责填充）。</param>
/// <param name="bindRow">按实体下标填充 <paramref name="parameters"/> 的回调（每批一个委托，非每行）。</param>
/// <param name="columnNames">完整列名（含前置的缺失主键列）。</param>
/// <param name="missingPrimaryKeyCount">前缀列数：这些列恒返回 <see cref="DBNull"/>（自增主键由 MySQL 生成）。</param>
internal sealed class EntityDataReader(
    int start,
    int end,
    DbParameter[] parameters,
    Action<int> bindRow,
    string[] columnNames,
    int missingPrimaryKeyCount) : DbDataReader
{
    private int _index = start - 1;

    public override int FieldCount => columnNames.Length;

    public override bool HasRows => end > start;

    public override bool IsClosed => false;

    public override int Depth => 0;

    /// <summary>LOAD DATA 路径不由本类统计受影响行数——契约值 -1（与 DataTable 路径同为"由驱动回填"）。</summary>
    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (++_index >= end)
            return false;

        bindRow(_index);
        return true;
    }

    public override object GetValue(int ordinal)
    {
        if (ordinal < missingPrimaryKeyCount)
            return DBNull.Value;

        object? value = parameters[ordinal - missingPrimaryKeyCount].Value;
        return value ?? DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < count; i++)
            values[i] = GetValue(i);
        return count;
    }

    /// <summary>M9：直接读参数值判空——原实现经 <see cref="GetValue"/> 取值，驱动对每列可能
    /// 先 IsDBNull 再 GetValue，同一 ordinal 两次解引用 + 两次 null 合并分支。
    /// 越界序号仍按 ADO.NET 契约从数组抛 IndexOutOfRangeException（与 GetOrdinal 同族）。</summary>
    public override bool IsDBNull(int ordinal)
        => ordinal < missingPrimaryKeyCount
            || parameters[ordinal - missingPrimaryKeyCount].Value is null or DBNull;

    public override string GetName(int ordinal) => columnNames[ordinal];

    public override int GetOrdinal(string name)
    {
        int ordinal = Array.IndexOf(columnNames, name);
        // 契约上 GetOrdinal 应抛 IndexOutOfRangeException，但那是运行时保留异常类型
        // （CA2201/S112 阻断用户代码抛出）。本路径的驱动只按序号取值（实测），
        // 故按仓库惯例改用 ArgumentException——调用方仍能明确知道列名不存在。
        return ordinal >= 0
            ? ordinal
            : throw new ArgumentException($"Column '{name}' not found in the bulk copy reader.", nameof(name));
    }

    /// <summary>恒为 <c>object</c>：与 DataTable 路径逐列声明 <c>typeof(object)</c> 等价，
    /// 驱动按值的运行时类型格式化文本。</summary>
    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields
        | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal) => typeof(object);

    /// <inheritdoc cref="GetFieldType"/>
    public override string GetDataTypeName(int ordinal) => "object";

    public override bool NextResult() => false;

    public override IEnumerator GetEnumerator() => throw new NotSupportedException(
        "EntityDataReader does not support enumeration; MySqlBulkCopy pulls rows via Read().");

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    /// <summary>驱动在当前路径下只经 <see cref="GetValue"/> 取值（实测）；分块读取不提供——
    /// 静默返回 0 会被调用方读成"读到 0 字节"。</summary>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException(
            "EntityDataReader does not support GetBytes; MySqlBulkCopy reads values via GetValue.");

    /// <inheritdoc cref="GetBytes"/>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException(
            "EntityDataReader does not support GetChars; MySqlBulkCopy reads values via GetValue.");
}
