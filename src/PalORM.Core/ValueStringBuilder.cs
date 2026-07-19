using System.Buffers;
using System.Runtime.CompilerServices;

namespace PalORM;

/// <summary>栈分配字符串构建器——BuildSql 用, 替代 StringBuilder 消除堆分配。
/// <para><b>设计</b>: ref struct(仅栈上存在)。调用点以 stackalloc 提供初始缓冲（256-512B），
/// 超出时 Grow 切换到 ArrayPool 兜底（ITM-322：声明与调用点实现一致，勿改回 int 构造）。</para>
/// <para><b>为什么不用 StringBuilder</b>: StringBuilder 始终在堆上分配→高 QPS 场景 Gen0 GC 压力。
/// 小查询(≤512B)零分配, 大查询用 ArrayPool 减少 GC。</para></summary>
internal ref struct ValueStringBuilder
{
    private char[]? _pooled;
    private Span<char> _buffer;
    private int _pos;

    public ValueStringBuilder(Span<char> initial)
    {
        _pooled = null;
        _buffer = initial;
        _pos = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(char c)
    {
        if (_pos >= _buffer.Length) Grow();
        _buffer[_pos++] = c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(string? s)
    {
        if (s is null) return;
        if (_pos + s.Length > _buffer.Length) Grow(Math.Max(_pos + s.Length, _buffer.Length * 2));
        s.AsSpan().CopyTo(_buffer.Slice(_pos));
        _pos += s.Length;
    }

    private void Grow(int? min = null)
    {
        int newSize = Math.Max(min ?? _buffer.Length * 2, 256);
        char[] newPooled = ArrayPool<char>.Shared.Rent(newSize);
        _buffer.Slice(0, _pos).CopyTo(newPooled);
        if (_pooled is not null) ArrayPool<char>.Shared.Return(_pooled);
        _pooled = newPooled;
        _buffer = newPooled;
    }

    /// <summary>去除尾随空格——括组闭合前对齐（"cond ) " 而非 "cond  )"）。</summary>
    public void TrimEnd()
    {
        while (_pos > 0 && _buffer[_pos - 1] == ' ') _pos--;
    }

    public override string ToString()
    {
        string result = _buffer.Slice(0, _pos).ToString();
        Dispose();
        return result;
    }

    public void Dispose()
    {
        if (_pooled is not null)
        {
            ArrayPool<char>.Shared.Return(_pooled);
            _pooled = null;
        }
        _buffer = default;
    }
}
