namespace PalORM;

/// <summary>参数名预缓存--消除每次 $"@p{N}" 插值分配。
/// v4.1 极致降内存：p0 到 p1023 预构建为静态数组，索引取用零分配。</summary>
internal static class ParameterNameCache
{
    private static readonly string[] Names =
        Enumerable.Range(0, 1024).Select(static i => $"@p{i}").ToArray();

    /// <summary>获取参数名 @p{index}。index 小于 1024 时零分配（静态缓存），超出时 fallback 插值。</summary>
    internal static string GetName(int index)
        => (uint)index < (uint)Names.Length
            ? Names[index]
            : $"@p{index}";
}
