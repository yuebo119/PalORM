namespace PalORM;

/// <summary>参数名预缓存--消除每次 $"@p{N}" 插值分配。
/// v4.1 极致降内存：p0 到 p1023 预构建为静态数组，索引取用零分配。
/// v4.3：提升为 public 供 SourceGen 生成代码引用。
/// <para><b>B1（2026-09-20）扩容到 MaxBindParameters</b>：原 1024 上界会被 MySQL 多值
/// INSERT 的满批参数池突破——<c>MaxParametersPerStatement = SqlLimits.MaxBindParameters</c>
/// (65535)，默认 batchSize=1000，2 列实体的 poolSize 即 2000，索引 1024..1999 全部落入
/// <c>$"@p{index}"</c> 插值分支，每次 BulkInsertAsync 约 2*(poolSize-1024) 次字符串分配。
/// 代价是常驻约 1.3 MB 的字符串表（65536 × (24B 对象头 + ~7 字符)），换来批量写入热路径
/// 的参数名零分配。SQLite 上限 999→32766（2026-09-25，引擎编译选项实测）后最深参数名仍在
/// 65536 表内不越界；PG 走 COPY 不建该池，扩容对 MySQL 与 SQLite 批量路径都有实际收益。</para></summary>
public static class ParameterNameCache
{
    /// <summary>预建参数名的数量上界——与 <see cref="SqlLimits.MaxBindParameters"/> 对齐，
    /// 使「单条语句的参数名全部命中缓存」成为可保证的性质而非取决于批大小与列数的巧合。</summary>
    private const int CachedNameCount = SqlLimits.MaxBindParameters;

    private static readonly string[] Names =
        BuildNames(CachedNameCount);

    /// <summary>构建 <c>@p0</c>..<c>@p{N-1}</c>——用 <see cref="string.Concat(string, string, string)"/>
    /// 逐段拼接（数字经 <see cref="int.ToString()"/>），启动期一次性成本。</summary>
    private static string[] BuildNames(int count)
    {
        var names = new string[count];
        for (int index = 0; index < count; index++)
            names[index] = string.Concat("@p", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return names;
    }

    /// <summary>获取参数名 @p{index}。index 小于 <see cref="CachedNameCount"/> 时零分配（静态缓存），
    /// 超出时 fallback 插值（正常不会发生——调用方的参数总量受 <see cref="SqlLimits.MaxBindParameters"/>
    /// 约束，此处仅为防御）。</summary>
    public static string GetName(int index)
        => (uint)index < (uint)Names.Length
            ? Names[index]
            : $"@p{index}";
}
