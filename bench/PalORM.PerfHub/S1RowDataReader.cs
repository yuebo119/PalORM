using System.Data;

namespace PalORM.PerfHub;

/// <summary>把 <see cref="S1Row"/> 列表喂给 <c>MySqlBulkCopy</c> 的读取器。
/// <para><b>为什么不是 DataTable</b>：产品 <c>MySqlBulkCopyInserter</c> 在 v5.6 已从 DataTable
/// 改为读取器路径——实测同一批 10K 行 × 4 列，DataTable 每行 372 B / 114.2 ms 对读取器
/// 57 B / 101.6 ms（−84% 分配、−11% 耗时），并明确记下"DataTable 路径有专门优化"这一判断被证伪。
/// 本套件的 ADO 臂原先仍在用 DataTable，既不是行业最优实现，也在本环境实测把连接置为 Broken
/// （BulkDelete 随后报 SocketException 995）。</para>
/// <para>只实现 MySqlBulkCopy 读取所必需的成员，其余抛 <see cref="NotSupportedException"/>——
/// 静默返回假值比抛异常更危险（会把列值读成默认值而没人发现）。</para></summary>
internal sealed class S1RowDataReader(IReadOnlyList<S1Row> rows) : IDataReader
{
    private static readonly string[] ColumnNames = ["Id", "Name", "Qty", "Price", "Marker"];
    private readonly IReadOnlyList<S1Row> _rows = rows;
    private int _index = -1;

    public int FieldCount => ColumnNames.Length;

    public bool Read() => ++_index < _rows.Count;

    public object GetValue(int i)
    {
        S1Row row = _rows[_index];
        return i switch
        {
            0 => row.Id,
            1 => row.Name,
            2 => row.Qty,
            3 => row.Price,
            4 => row.Marker,
            _ => throw new ArgumentOutOfRangeException(nameof(i))
        };
    }

    public bool IsDBNull(int i) => false;

    public string GetName(int i) => ColumnNames[i];

    public int GetOrdinal(string name)
    {
        int index = Array.IndexOf(ColumnNames, name);
        return index >= 0
            ? index
            : throw new ArgumentOutOfRangeException(nameof(name), name, "S1 形状无此列");
    }

    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields
        | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type GetFieldType(int i) => i switch
    {
        0 or 4 => typeof(long),
        1 => typeof(string),
        2 => typeof(int),
        3 => typeof(decimal),
        _ => throw new ArgumentOutOfRangeException(nameof(i))
    };

    public string GetDataTypeName(int i) => GetFieldType(i).Name;

    public int GetValues(object[] values)
    {
        int count = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public object this[int i] => GetValue(i);

    public object this[string name] => GetValue(GetOrdinal(name));

    public void Close() { }

    public void Dispose() { }

    public int Depth => 0;

    public bool IsClosed => false;

    public int RecordsAffected => -1;

    public bool NextResult() => false;

    public bool GetBoolean(int i) => (bool)GetValue(i);

    public byte GetByte(int i) => (byte)GetValue(i);

    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length)
        => throw new NotSupportedException("S1 形状无二进制列");

    public char GetChar(int i) => (char)GetValue(i);

    public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length)
        => throw new NotSupportedException("S1 形状无字符数组列");

    public DateTime GetDateTime(int i) => (DateTime)GetValue(i);

    public decimal GetDecimal(int i) => (decimal)GetValue(i);

    public double GetDouble(int i) => (double)GetValue(i);

    public float GetFloat(int i) => (float)GetValue(i);

    public Guid GetGuid(int i) => (Guid)GetValue(i);

    public short GetInt16(int i) => (short)GetValue(i);

    public int GetInt32(int i) => (int)GetValue(i);

    public long GetInt64(int i) => (long)GetValue(i);

    public string GetString(int i) => (string)GetValue(i);

    public DataTable GetSchemaTable() => throw new NotSupportedException("MySqlBulkCopy 读取器路径不需要 schema 表");

    public IDataReader GetData(int i) => throw new NotSupportedException("S1 形状无嵌套读取器");
}
