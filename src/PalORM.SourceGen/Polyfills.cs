// Polyfill: netstandard2.0 上缺少的类型定义
// C# 9+ record/init → IsExternalInit
// （ITM-429：RequiredMember/CompilerFeatureRequired/StringSyntax 三个零使用 polyfill 已删）

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
