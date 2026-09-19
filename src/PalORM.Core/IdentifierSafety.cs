using System.ComponentModel;
using System.Globalization;

namespace PalORM;

/// <summary>标识符安全校验——三方言共享的拒绝规则（ITM-593）。
/// 当前调用点表名/列名均为源生成器编译期常量，威胁面小；本守卫防御未来动态标识符
/// （如运行时拼接表名）误用，避免控制字符进入 SQL 引起解析异常或注入向量。
/// 跨程序集（Provider）共享，故 public；本类不属于稳定公共 API 表面，3.0 随内部演进调整。
/// ITM-609: [EditorBrowsable(Never)] 隐藏外部 IDE IntelliSense，避免误用直接调用。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class IdentifierSafety
{
    /// <summary>控制字符谓词（单一真源，独立审计 M1-1）：NUL、C0（U+0000-U+001F）、DEL（U+007F）、
    /// C1（U+0080-U+009F）。<see cref="ThrowIfUnsafe"/> 与 <c>QueryBuilder.Raw</c> 的 SQL 片段
    /// 防线共享本谓词——两处字符面口径由同一实现保证不漂移（对齐 SqlLimits/CORE-010 单点纪律）。</summary>
    public static bool IsControlChar(char ch)
        => ch < ' ' || (ch >= '\x7F' && ch <= '\x9F');

    /// <summary>拒绝 NUL、C0 控制字符（U+0000-U+001F）、DEL（U+007F）以及 C1 控制字符
    /// （U+0080-U+009F）。引号/反引号转义不覆盖控制字符——它们在驱动 C 层或服务端 SQL 解析器
    /// 可能被解释为语句定界或截断信号（NUL 截断已证，ITM-584；C0 换行/制表符 ITM-593；
    /// C1 NEL/RI 在多字节 UTF-8 序列下行为不稳，ITM-608 扩展覆盖）。
    /// <para>ITM-738(r20)：补空/空白拒绝——本类是跨程序集 public 守卫（Provider 共享），
    /// 直接调用 <c>ThrowIfUnsafe(null)</c> 此前抛 NRE、空/空白串此前放行；与生成侧
    /// <c>SqlGeneration.QuoteIdentifier</c> 的拒绝面统一（ITM-739）。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3267:LoopsShouldBeSimplifiedWithLinq",
        Justification = "守卫在标识符/片段进入 SQL 前的路径上：LINQ Where+Any 每次分配委托+迭代器。手写循环零分配（同 SqlShapeCache.FindMatch 口径）。")]
    public static void ThrowIfUnsafe(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        foreach (char ch in identifier)
        {
            // ITM-608: 扩展 C1 控制字符（U+0080-U+009F）——NEL(U+0085) 等在多字节 UTF-8 下
            // 驱动 C 层解析行为同样不稳。当前调用点全编译期常量，威胁面接近 0，防御性扩展。
            if (IsControlChar(ch))
            {
                // r19/R-P3-03：M5 string.Format → 插值（string.Create 保持文化安全，S6618）
                string message = string.Create(CultureInfo.InvariantCulture,
                    $"标识符包含控制字符 U+{(int)ch:X4}——驱动/服务端 C 层解析行为不稳（NUL 截断 / 换行穿透引号定界等）。拒绝以保安全。");
                throw new ArgumentException(message, nameof(identifier));
            }
        }
    }
}
