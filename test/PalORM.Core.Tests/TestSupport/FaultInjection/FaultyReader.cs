using System.Collections;
using System.Data.Common;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>按行号注入读取故障的 <see cref="DbDataReader"/> 包装（FLT-1）。
/// <para>用途：批量/分页读到一半驱动断开（网络中断、结果集被杀）。这类失效只有「读第 N 行
/// 时抛」能复现——在命令层抛只覆盖「第一条就失败」。</para>
/// <para>行号按 <see cref="ReadAsync(CancellationToken)"/> 的调用次数计（1 起：1 = 第一次读就失败），
/// 与命令序号的语义保持一致。</para></summary>
internal sealed class FaultyReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly int _failAtRow;
    private readonly Exception _exception;
    private int _reads;

    internal FaultyReader(DbDataReader inner, int failAtRow, Exception exception)
    {
        _inner = inner;
        _failAtRow = failAtRow;
        _exception = exception;
    }

    /// <summary>已发生的 Read 次数（断言注入确实发生在目标行）。</summary>
    internal int Reads => _reads;

    public override bool Read()
    {
        if (++_reads == _failAtRow) throw _exception;
        return _inner.Read();
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (++_reads == _failAtRow) throw _exception;
        return await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
    }


    public override bool GetBoolean(int ordinal) => _inner.GetBoolean(ordinal);

    public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => _inner.GetChar(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);

    public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);

    public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);

    public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);

    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);

    public override float GetFloat(int ordinal) => _inner.GetFloat(ordinal);

    public override Guid GetGuid(int ordinal) => _inner.GetGuid(ordinal);

    public override short GetInt16(int ordinal) => _inner.GetInt16(ordinal);

    public override int GetInt32(int ordinal) => _inner.GetInt32(ordinal);

    public override long GetInt64(int ordinal) => _inner.GetInt64(ordinal);

    public override string GetString(int ordinal) => _inner.GetString(ordinal);

    public override int Depth => _inner.Depth;

    public override int FieldCount => _inner.FieldCount;

    public override bool HasRows => _inner.HasRows;

    public override bool IsClosed => _inner.IsClosed;

    public override int RecordsAffected => _inner.RecordsAffected;

    public override object this[int ordinal] => _inner[ordinal];

    public override object this[string name] => _inner[name];

    public override string GetName(int ordinal) => _inner.GetName(ordinal);

    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

    public override object GetValue(int ordinal) => _inner.GetValue(ordinal);

    public override int GetValues(object[] values) => _inner.GetValues(values);

    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);

    public override bool NextResult() => _inner.NextResult();

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => _inner.NextResultAsync(cancellationToken);

    public override IEnumerator GetEnumerator() => _inner.GetEnumerator();

    public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(ordinal);

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
        => _inner.GetFieldValueAsync<T>(ordinal, cancellationToken);

    public override void Close() => _inner.Close();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
