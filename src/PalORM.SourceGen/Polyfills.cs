// Polyfill: netstandard2.0 上缺少的类型定义
// C# 9+ record/init → IsExternalInit
// C# 11 required → RequiredMemberAttribute, CompilerFeatureRequiredAttribute

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    internal sealed class RequiredMemberAttribute : Attribute { }

    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string name) { }
    }
}

// Polyfill: StringSyntaxAttribute for modern syntax highlighting
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class StringSyntaxAttribute : Attribute
    {
        public StringSyntaxAttribute(string syntax) { }
    }
}
