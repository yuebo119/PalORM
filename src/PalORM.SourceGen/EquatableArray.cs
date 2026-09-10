namespace PalORM.SourceGen;

/// <summary>值相等数组——支持 foreach 和 record 的 Equals/GetHashCode。
/// <para>源生成器增量管线的缓存键依赖值相等——默认 C# 数组是引用相等，
/// 用此包装提供元素级值相等语义。</para></summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>> where T : IEquatable<T>
{
    private readonly T[] _items;
    /// <summary>ITM-783(r21)：复制入参（防御性）——与出方向 ToArray 的 ITM-737 口径对称，
    /// 调用方保留源数组的引用不再能污染本值对象（增量缓存键的等值语义）。</summary>
    public EquatableArray(T[] items) => _items = (T[])items.Clone();
    public EquatableArray(System.Collections.Immutable.ImmutableArray<T> items) : this(items.AsSpan().ToArray()) { }
    public ReadOnlySpan<T> AsSpan() => _items;
    /// <summary>返回元素的<b>防御性副本</b>（ITM-737：原实现直接返回内部数组，与
    /// <c>ImmutableArray&lt;T&gt;.ToArray()</c> 的"复制"语义相反，
    /// 调用方写入会污染本值对象并使增量缓存键与等值语义失效）。只读消费请用 <see cref="AsSpan"/> 零分配。</summary>
    public T[] ToArray() => _items is null ? [] : (T[])_items.Clone();
    public bool Equals(EquatableArray<T> other) => AsSpan().SequenceEqual(other.AsSpan());
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);
    public override int GetHashCode()
    {
        // ITM-544：default(EquatableArray<T>) 的 _items 为 null，直接 foreach 会 NRE——归一化空数组
        int hash = 17;
        foreach (var item in _items ?? Array.Empty<T>()) hash = hash * 31 + (item?.GetHashCode() ?? 0);
        return hash;
    }
    // ITM-544：default 实例枚举同样归一化，避免 _items 为 null 时 MoveNext/Current 抛 NRE
    public Enumerator GetEnumerator() => new(_items ?? Array.Empty<T>());
    public ref struct Enumerator
    {
        private readonly T[] _items;
        private int _index;
        internal Enumerator(T[] items) { _items = items; _index = -1; }
        public T Current => _items[_index];
        public bool MoveNext() => ++_index < _items.Length;
    }
}
