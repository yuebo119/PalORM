using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using PalORM;

// Handler 的 Dispose 由 ConsumeHandler/ConsumeCached 在 finally 中调用；
// CA2000 看不到跨调用的所有权转移（探针专有）。
#pragma warning disable CA2000

namespace PalORM.Benchmarks;

/// <summary>InterpolatedStringHandler 定向 A/B（入口成本，不触库）。
/// <para><b>基线</b>：现行编译期参数化路径——插值字面量转 <c>FormattableString</c>，
/// 经 <see cref="FormattableSqlFormatter.FormatCached"/> 形状缓存取 SQL，再逐参数取值
/// 模拟绑定。形状缓存命中时 SQL 文本零分配。</para>
/// <para><b>候选臂</b>：① 朴素 handler：直接拼 SQL + 池化字符/参数数组，每次
/// <c>new string</c>；② 缓存 handler：额外按字面量哈希做形状缓存，命中时复用 SQL 文本。
/// 两臂的参数数组都走 ArrayPool 并在 Dispose 时清零，字符缓冲同样池化——这是对候选臂
/// 最有利的实现形态（不含栈上参数数组等更激进的假设）。</para>
/// <para><b>口径</b>：500k 次/臂/轮，3 轮取中位；分配 = <c>GetTotalAllocatedBytes(precise)</c>。
/// 只测入口成本，不含 DbParameter 构造与数据库往返（那部分两臂相同）。</para></summary>
internal static class InterpolatedStringAbProbe
{
    // Handler 的 Dispose 由 ConsumeHandler/ConsumeCached 在 finally 中调用；
    // CA2000 看不到跨调用的所有权转移。
    [SuppressMessage("Reliability", "CA2000", Scope = "type",
        Justification = "Handler ownership transfers to ConsumeHandler/ConsumeCached, which dispose in finally.")]
    private const int Iterations = 500_000;
    private const int Warmup = 20_000;
    private static int _sink;

    public static void Run()
    {
        Console.WriteLine("═══════ InterpolatedStringHandler 定向 A/B（入口成本）═══════");
        Console.WriteLine($"迭代 {Iterations:N0}/臂/轮，3 轮取中位；分配为 GetTotalAllocatedBytes(precise)");
        Console.WriteLine();
        VerifyShape3();
        Console.WriteLine("形状校验：handler SQL 与 FormattableString 基线逐字一致");
        Console.WriteLine();
        Console.WriteLine("| 形状 | 臂 | ns/op | B/op |");
        Console.WriteLine("|---|---|---:|---:|");
        MeasureAll("1 洞", static () => Baseline1(7), static () => Handler1(7), static () => Cached1(7));
        MeasureAll("3 洞", static () => Baseline3(7, "alice", 12.34m),
            static () => Handler3(7, "alice", 12.34m), static () => Cached3(7, "alice", 12.34m));
        MeasureAll("6 洞", static () => Baseline6(7, "alice", 12.34m, 99L, "note", true),
            static () => Handler6(7, "alice", 12.34m, 99L, "note", true),
            static () => Cached6(7, "alice", 12.34m, 99L, "note", true));
        Console.WriteLine();
        Console.WriteLine($"sink={_sink}");
    }

    private static void MeasureAll(string shape, Func<int> baseline, Func<int> naive, Func<int> cached)
    {
        (double bNs, double bBytes) = Measure(baseline);
        (double hNs, double hBytes) = Measure(naive);
        (double cNs, double cBytes) = Measure(cached);
        Print(shape, "FormattableString + 形状缓存", bNs, bBytes);
        Print(shape, "handler 朴素", hNs, hBytes);
        Print(shape, "handler + 形状缓存", cNs, cBytes);
    }

    private static void Print(string shape, string arm, double ns, double bytes)
        => Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"| {shape} | {arm} | {ns:F1} | {bytes:F1} |"));

    [SuppressMessage("Reliability", "S1215",
        Justification = "分配测量前强制回收是量具步骤（与 PoolProbe 同口径）。")]
    private static (double Ns, double Bytes) Measure(Func<int> action)
    {
        for (int i = 0; i < Warmup; i++) _sink ^= action();
        double[] ns = new double[3];
        double[] bytes = new double[3];
        for (int round = 0; round < 3; round++)
        {
            GC.Collect();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            int sum = 0;
            for (int i = 0; i < Iterations; i++) sum += action();
            sw.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            _sink ^= sum;
            ns[round] = sw.Elapsed.TotalNanoseconds / Iterations;
            bytes[round] = allocated / (double)Iterations;
        }
        Array.Sort(ns);
        Array.Sort(bytes);
        return (ns[1], bytes[1]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Baseline1(int id)
    {
        FormattableString sql = $"SELECT * FROM t WHERE id = {id}";
        return ConsumeFormattable(sql);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Baseline3(int id, string name, decimal amount)
    {
        FormattableString sql = $"UPDATE t SET name = {name}, amount = {amount} WHERE id = {id}";
        return ConsumeFormattable(sql);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Baseline6(int id, string name, decimal amount, long qty, string note, bool active)
    {
        FormattableString sql =
            $"UPDATE t SET name = {name}, amount = {amount}, qty = {qty}, note = {note}, active = {active} WHERE id = {id}";
        return ConsumeFormattable(sql);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ConsumeFormattable(FormattableString sql)
    {
        string text = FormattableSqlFormatter.FormatCached(
            sql.Format, 0, sql.ArgumentCount, SqlDialect.Sqlite);
        int n = 0;
        for (int i = 0; i < sql.ArgumentCount; i++)
        {
            object? value = sql.GetArgument(i);
            n += value is null ? 0 : 1;
        }
        return text.Length + n;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Handler1(int id) => ConsumeHandler($"SELECT * FROM t WHERE id = {id}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Handler3(int id, string name, decimal amount)
        => ConsumeHandler($"UPDATE t SET name = {name}, amount = {amount} WHERE id = {id}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Handler6(int id, string name, decimal amount, long qty, string note, bool active)
        => ConsumeHandler(
            $"UPDATE t SET name = {name}, amount = {amount}, qty = {qty}, note = {note}, active = {active} WHERE id = {id}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Cached1(int id) => ConsumeCached($"SELECT * FROM t WHERE id = {id}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Cached3(int id, string name, decimal amount)
        => ConsumeCached($"UPDATE t SET name = {name}, amount = {amount} WHERE id = {id}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Cached6(int id, string name, decimal amount, long qty, string note, bool active)
        => ConsumeCached(
            $"UPDATE t SET name = {name}, amount = {amount}, qty = {qty}, note = {note}, active = {active} WHERE id = {id}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ConsumeHandler(AbHandler handler)
    {
        try
        {
            string text = handler.GetSql();
            int n = 0;
            for (int i = 0; i < handler.ArgCount; i++)
            {
                object? value = handler.GetArg(i);
                n += value is null ? 0 : 1;
            }
            return text.Length + n;
        }
        finally
        {
            handler.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ConsumeCached(AbCachedHandler handler)
    {
        try
        {
            string text = handler.GetSql();
            int n = 0;
            for (int i = 0; i < handler.ArgCount; i++)
            {
                object? value = handler.GetArg(i);
                n += value is null ? 0 : 1;
            }
            return text.Length + n;
        }
        finally
        {
            handler.Dispose();
        }
    }

    private static void VerifyShape3()
    {
        const int id = 7;
        const string name = "alice";
        const decimal amount = 12.34m;
        FormattableString baseline = $"UPDATE t SET name = {name}, amount = {amount} WHERE id = {id}";
        string expected = FormattableSqlFormatter.FormatCached(
            baseline.Format, 0, baseline.ArgumentCount, SqlDialect.Sqlite);
        string actual = GetSql3(id, name, amount);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"handler SQL 与基线不一致：\n{expected}\n{actual}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string GetSql3(int id, string name, decimal amount)
    {
        var handler = new AbHandler(48, 3);
        handler.AppendLiteral("UPDATE t SET name = ");
        handler.AppendFormatted(name);
        handler.AppendLiteral(", amount = ");
        handler.AppendFormatted(amount);
        handler.AppendLiteral(" WHERE id = ");
        handler.AppendFormatted(id);
        try
        {
            return handler.GetSql();
        }
        finally
        {
            handler.Dispose();
        }
    }
}

/// <summary>朴素 handler：池化字符缓冲 + 池化参数数组，每次 new string。</summary>
[InterpolatedStringHandler]
internal ref struct AbHandler
{
    private char[] _buffer;
    private readonly object?[] _args;
    private int _pos;
    private int _argCount;

    public AbHandler(int literalLength, int formattedCount)
    {
        _buffer = ArrayPool<char>.Shared.Rent(Math.Max(64, literalLength + (formattedCount * 8)));
        _args = ArrayPool<object?>.Shared.Rent(Math.Max(1, formattedCount));
    }

    public readonly int ArgCount => _argCount;

    public void AppendLiteral(string value)
    {
        EnsureCapacity(_pos + value.Length);
        value.AsSpan().CopyTo(_buffer.AsSpan(_pos));
        _pos += value.Length;
    }

    public void AppendFormatted<T>(T value)
    {
        _args[_argCount] = value;
        AppendPlaceholder(_argCount);
        _argCount++;
    }

    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value);

    public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value);

    public readonly object? GetArg(int index) => _args[index];

    public readonly string GetSql() => new(_buffer, 0, _pos);

    public void Dispose()
    {
        ArrayPool<char>.Shared.Return(_buffer);
        ArrayPool<object?>.Shared.Return(_args, clearArray: true);
    }

    private void AppendPlaceholder(int index)
    {
        string placeholder = ParameterNameCache.GetName(index);
        EnsureCapacity(_pos + placeholder.Length);
        placeholder.AsSpan().CopyTo(_buffer.AsSpan(_pos));
        _pos += placeholder.Length;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length) return;
        int next = Math.Max(required, _buffer.Length * 2);
        char[] grown = ArrayPool<char>.Shared.Rent(next);
        _buffer.AsSpan(0, _pos).CopyTo(grown);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = grown;
    }
}

/// <summary>缓存 handler：在朴素臂基础上按字面量哈希缓存 SQL 文本（命中时省字符串分配）。</summary>
[InterpolatedStringHandler]
internal ref struct AbCachedHandler
{
    private const int MaxEntries = 4096;
    private static readonly ConcurrentDictionary<ShapeKey, string> Cache = new();

    private char[] _buffer;
    private readonly object?[] _args;
    private int _pos;
    private int _argCount;
    private string? _firstLiteral;
    private int _hash;
    private readonly int _literalLength;

    public AbCachedHandler(int literalLength, int formattedCount)
    {
        _literalLength = literalLength;
        _buffer = ArrayPool<char>.Shared.Rent(Math.Max(64, literalLength + (formattedCount * 8)));
        _args = ArrayPool<object?>.Shared.Rent(Math.Max(1, formattedCount));
    }

    public readonly int ArgCount => _argCount;

    public void AppendLiteral(string value)
    {
        _firstLiteral ??= value;
        _hash = HashCode.Combine(_hash, value.GetHashCode(StringComparison.Ordinal), value.Length);
        EnsureCapacity(_pos + value.Length);
        value.AsSpan().CopyTo(_buffer.AsSpan(_pos));
        _pos += value.Length;
    }

    public void AppendFormatted<T>(T value)
    {
        _args[_argCount] = value;
        AppendPlaceholder(_argCount);
        _argCount++;
    }

    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value);

    public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value);

    public readonly object? GetArg(int index) => _args[index];

    public string GetSql()
    {
        ShapeKey key = new(_firstLiteral ?? "", _literalLength, _argCount, _hash);
        if (Cache.TryGetValue(key, out string? cached)) return cached;
        string sql = new(_buffer, 0, _pos);
        if (Cache.Count >= MaxEntries) Cache.Clear();
        return Cache.GetOrAdd(key, sql);
    }

    public void Dispose()
    {
        ArrayPool<char>.Shared.Return(_buffer);
        ArrayPool<object?>.Shared.Return(_args, clearArray: true);
    }

    private void AppendPlaceholder(int index)
    {
        string placeholder = ParameterNameCache.GetName(index);
        EnsureCapacity(_pos + placeholder.Length);
        placeholder.AsSpan().CopyTo(_buffer.AsSpan(_pos));
        _pos += placeholder.Length;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length) return;
        int next = Math.Max(required, _buffer.Length * 2);
        char[] grown = ArrayPool<char>.Shared.Rent(next);
        _buffer.AsSpan(0, _pos).CopyTo(grown);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = grown;
    }

    private readonly record struct ShapeKey(string FirstLiteral, int LiteralLength, int ArgCount, int Hash);
}
#pragma warning restore CA2000
