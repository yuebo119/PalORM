namespace PalORM;

/// <summary>参数名预缓存--消除每次 $"@p{N}" 插值分配。
/// v4.1 极致降内存：p0 到 p1023 预构建为静态数组，索引取用零分配。
/// v4.3：提升为 public 供 SourceGen 生成代码引用。
/// <para><b>B1（2026-09-20）扩容到 MaxBindParameters</b>：原 1024 上界会被 MySQL 多值
/// INSERT 的满批参数池突破——<c>MaxParametersPerStatement = SqlLimits.MaxBindParameters</c>
/// (65535)，默认 batchSize=1000，2 列实体的 poolSize 即 2000，索引 1024..1999 全部落入
/// <c>$"@p{index}"</c> 插值分支，每次 BulkInsertAsync 约 2*(poolSize-1024) 次字符串分配。
/// SQLite 上限 999 保守值不越界（32766 大值经 A/B 实测证伪为负优化，
/// 见 SqlLimits.MaxBindParametersFor）；PG 走 COPY 不建该池，扩容对 MySQL 批量路径有实际收益。</para>
/// <para><b>F30（2026-10-12 审读修复）：分块惰性</b>——原一次性预建 65536 个字符串常驻
/// 约 2.9MB（65536 × ~36B 字符串 + 512KB 数组；原注释"约 1.3 MB"口径失实）。绝大多数
/// 进程只触碰低段索引（单行 CRUD + 中小批），按 1024 一块惰性构建后常驻随实际用量伸缩：
/// 只用前 2048 个名时约 72KB；满批路径首次触达对应块后与旧实现同零分配。索引取用为
/// 一次数组寻址 + 一次按位与，无锁（块内字符串内容确定性，CAS 败者丢弃）。</para></summary>
public static class ParameterNameCache
{
    /// <summary>预建参数名的数量上界——与 <see cref="SqlLimits.MaxBindParameters"/> 对齐，
    /// 使「单条语句的参数名全部命中缓存」成为可保证的性质而非取决于批大小与列数的巧合。</summary>
    private const int CachedNameCount = SqlLimits.MaxBindParameters;

    private const int ChunkShift = 10;
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;
    // 向上取整：CachedNameCount=65535 非 ChunkSize 整数倍，向下取整会缺最后一块
    private static readonly string[]?[] Chunks = new string[]?[(CachedNameCount >>> ChunkShift) + 1];

    /// <summary>构建单个分块 <c>@p{chunk*1024}</c>..<c>@p{chunk*1024+1023}</c>。</summary>
    private static string[] BuildChunk(int chunk)
    {
        var names = new string[ChunkSize];
        int @base = chunk << ChunkShift;
        for (int offset = 0; offset < ChunkSize; offset++)
            names[offset] = string.Concat("@p", (@base + offset)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        return names;
    }

    /// <summary>获取参数名 @p{index}。index 小于 <see cref="CachedNameCount"/> 时零分配（分块
    /// 惰性缓存，首次触碰的块一次性构建），超出时 fallback 插值（正常不会发生——调用方的
    /// 参数总量受 <see cref="SqlLimits.MaxBindParameters"/> 约束，此处仅为防御）。</summary>
    public static string GetName(int index)
    {
        if ((uint)index >= (uint)CachedNameCount)
            return $"@p{index}";
        string[]?[] chunks = Chunks;
        int chunkIndex = index >>> ChunkShift;
        string[]? chunk = System.Threading.Volatile.Read(ref chunks[chunkIndex]);
        if (chunk is null)
        {
            string[] created = BuildChunk(chunkIndex);
            chunk = System.Threading.Interlocked.CompareExchange(ref chunks[chunkIndex], created, null) ?? created;
        }
        return chunk[index & ChunkMask];
    }
}
