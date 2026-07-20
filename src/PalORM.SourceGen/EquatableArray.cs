namespace PalORM.SourceGen;

/// <summary>值相等数组——支持 foreach 和 record 的 Equals/GetHashCode。
/// <para>源生成器增量管线的缓存键依赖值相等——默认 C# 数组是引用相等，
/// 用此包装提供元素级值相等语义。</para></summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>> where T : IEquatable<T>
{
    private readonly T[] _items;
    public EquatableArray(T[] items) => _items = items;
    public EquatableArray(System.Collections.Immutable.ImmutableArray<T> items) : this(items.AsSpan().ToArray()) { }
    public ReadOnlySpan<T> AsSpan() => _items;
    public T[] ToArray() => _items;
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
