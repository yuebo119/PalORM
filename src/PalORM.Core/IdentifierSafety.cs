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
    /// <summary>拒绝 NUL、C0 控制字符（U+0000-U+001F）、DEL（U+007F）以及 C1 控制字符
    /// （U+0080-U+009F）。引号/反引号转义不覆盖控制字符——它们在驱动 C 层或服务端 SQL 解析器
    /// 可能被解释为语句定界或截断信号（NUL 截断已证，ITM-584；C0 换行/制表符 ITM-593；
    /// C1 NEL/RI 在多字节 UTF-8 序列下行为不稳，ITM-608 扩展覆盖）。</summary>
    public static void ThrowIfUnsafe(string identifier)
    {
        foreach (char ch in identifier)
        {
            // ITM-608: 扩展 C1 控制字符（U+0080-U+009F）——NEL(U+0085) 等在多字节 UTF-8 下
            // 驱动 C 层解析行为同样不稳。当前调用点全编译期常量，威胁面接近 0，防御性扩展。
            if (ch < ' ' || (ch >= '\x7F' && ch <= '\x9F'))
                throw new ArgumentException(
                    // r19/R-P3-03：M5 string.Format → 插值（string.Create 保持文化安全，S6618）
                    string.Create(CultureInfo.InvariantCulture,
                        $"标识符包含控制字符 U+{(int)ch:X4}——驱动/服务端 C 层解析行为不稳（NUL 截断 / 换行穿透引号定界等）。拒绝以保安全。"),
                    nameof(identifier));
        }
    }
}
