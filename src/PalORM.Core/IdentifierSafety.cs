using System.Globalization;

namespace PalORM;

/// <summary>标识符安全校验——三方言共享的拒绝规则（ITM-593）。
/// 当前调用点表名/列名均为源生成器编译期常量，威胁面小；本守卫防御未来动态标识符
/// （如运行时拼接表名）误用，避免控制字符进入 SQL 引起解析异常或注入向量。
/// 跨程序集（Provider）共享，故 public；本类不属于稳定公共 API 表面，3.0 随内部演进调整。</summary>
public static class IdentifierSafety
{
    /// <summary>拒绝 NUL 与 C0 控制字符（U+0000-U+001F）以及 DEL（U+007F）。
    /// 引号/反引号转义不覆盖控制字符——它们在驱动 C 层或服务端 SQL 解析器可能被解释为
    /// 语句定界或截断信号（NUL 截断已证，ITM-584；换行/制表符在部分驱动内允许但行为不稳）。</summary>
    public static void ThrowIfUnsafe(string identifier)
    {
        // 快路径：ASCII 控制字符表内字符数极少（C0 32 个 + DEL 1 个），逐字符判断开销可忽略。
        foreach (char ch in identifier)
        {
            if (ch < ' ' || ch == '\x7F')
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "标识符包含控制字符 U+{0:X4}——驱动/服务端 C 层解析行为不稳（NUL 截断 / 换行穿透引号定界等）。拒绝以保安全。",
                        (int)ch),
                    nameof(identifier));
        }
    }
}
