using System.Buffers;
using System.Data.Common;
using System.Runtime.InteropServices;

namespace PalORM;

/// <summary>结果集物化为 <see cref="List{T}"/> 的统一读取内核（2026-10-02）。
/// <para>前 <c>initialCapacity</c> 行直写 List；行数超出后转入 <see cref="ArrayPool{T}"/> 缓冲倍增，
/// 读完按实际行数一次性分配精确容量。原形态 <c>new List&lt;T&gt;(16)</c> 倍增扩容：2 万行累计分配
/// 16→32768 共 524KB 数组，其中 131KB、262KB 两块进 LOH（PerfHub 宽表 2 万行分配差 97% 由此解释）；
/// 本形态最终只分配 count×8 字节一次，1 万行 80KB 低于 LOH 阈值。</para>
/// <para>行数不超过 <c>initialCapacity</c> 的查询（单行/分页/小结果集）与原形态逐位相同，不碰池。</para></summary>
internal static class ResultListReader
{
    /// <summary>首次溢出时租用的缓冲下限（元素数）。</summary>
    private const int MinOverflowBufferLength = 64;

    /// <summary>读完 <paramref name="reader"/> 的全部行并物化为列表（容量恰为行数，或不超过初始容量时为初始容量）。</summary>
    /// <param name="reader">已执行、位于首行之前的读取器。</param>
    /// <param name="factory">逐行物化委托。</param>
    /// <param name="initialCapacity">直写 List 的初始容量；行数不超过它时不进池化路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="onFirstRow">仅在存在首行时、物化首行之前调用一次（直查族的首行列序校验）。</param>
    internal static async ValueTask<List<T>> ReadAllAsync<T>(
        DbDataReader reader, Func<DbDataReader, T> factory, int initialCapacity,
        CancellationToken ct, Action<DbDataReader>? onFirstRow = null)
        where T : class
    {
        initialCapacity = Math.Max(1, initialCapacity);
        var list = new List<T>(initialCapacity);
        while (list.Count < initialCapacity)
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return list;
            if (list.Count == 0)
                onFirstRow?.Invoke(reader);
            list.Add(factory(reader));
        }

        // List 恰好装满：先探下一行，没有更多行则原样返回（容量已精确，不租缓冲）
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return list;

        T[] buffer = ArrayPool<T>.Shared.Rent(Math.Max(initialCapacity * 4, MinOverflowBufferLength));
        int count = 0;
        try
        {
            list.CopyTo(buffer);
            count = list.Count;
            buffer[count++] = factory(reader);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (count == buffer.Length)
                {
                    T[] larger = ArrayPool<T>.Shared.Rent(buffer.Length * 2);
                    Array.Copy(buffer, larger, count);
                    Array.Clear(buffer, 0, count);
                    ArrayPool<T>.Shared.Return(buffer);
                    buffer = larger;
                }
                buffer[count++] = factory(reader);
            }

            list.Clear();
            list.Capacity = count;
            CollectionsMarshal.SetCount(list, count);
            buffer.AsSpan(0, count).CopyTo(CollectionsMarshal.AsSpan(list));
            return list;
        }
        finally
        {
            // 只清已写区间：池里的数组不得继续持有实体引用（尾部本就为 null）
            Array.Clear(buffer, 0, count);
            ArrayPool<T>.Shared.Return(buffer);
        }
    }
}
