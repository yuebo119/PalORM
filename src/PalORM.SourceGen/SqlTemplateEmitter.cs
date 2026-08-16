using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalORM.SourceGen;

/// <summary>[SqlTemplate("name")] 源生成器——为高频查询生成静态 FormattableString 常量。
/// 与 AsPrepared() 配合时，执行管线在参数绑定后调用 DbCommand.PrepareAsync。
/// SQL 提取走 Roslyn 语法树而非文本扫描（ITM-411：注释中的 $" 误取、\" 截断、
/// 插值洞引用方法参数生成 CS0103 三类脆弱一并消除）。</summary>
internal static class SqlTemplateEmitter
{
    /// <summary>PALORM041：[SqlTemplate] 同名模板冲突——同命名空间内模板名必须唯一；
    /// 去重静默丢弃会让用户拿到另一段 SQL 而不自知（ITM-662，改显式报错）。</summary>
    internal static readonly DiagnosticDescriptor DuplicateSqlTemplateName = new(
        id: "PALORM041",
        title: "Duplicate SqlTemplate name",
        messageFormat: "Duplicate [SqlTemplate] name '{0}' in namespace '{1}': template names must be unique within a namespace",
        category: "PalORM.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>单个模板的生成模型——生成器端按 (Namespace, TemplateName) 聚合去重
    /// （ITM-573：两个方法挂同名模板会生成两个同名字段 → CS0102；ITM-662：重名现在
    /// 发射 PALORM041 而非静默丢弃）。</summary>
    internal sealed record SqlTemplateModel(string Namespace, string TemplateName, string Literal, string MethodIdentity);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "Roslyn 语法树提取插值串——多分支守卫是必要的（属性过滤/语法节点判定/方法参数绑定验证）。"
            + "拆分会让单遍语法扫描逻辑跨方法跳跃。")]
    internal static SqlTemplateModel? Generate(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method)
            return null;

        var attr = method.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name is "SqlTemplateAttribute" or "SqlTemplate"
            && a.AttributeClass?.ContainingNamespace?.ToDisplayString() is "PalORM");
        if (attr?.ConstructorArguments.Length != 1)
            return null;

        string templateName = attr.ConstructorArguments[0].Value?.ToString() ?? "";
        if (string.IsNullOrEmpty(templateName)
            || !SyntaxFacts.IsValidIdentifier(templateName))
            return null;

        var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef?.GetSyntax(ct) is not MethodDeclarationSyntax methodSyntax)
            return null;

        // ITM-521：取 return 语句（含表达式体箭头 =>）中的插值串，而非方法体首个插值串。
        // 方法体前置日志/诊断的插值（如 logger.LogDebug($"...")）会污染"首个"选择，
        // 提升为模板常量后语义错误。以 return/箭头表达式的插值为准。
        InterpolatedStringExpressionSyntax? interpolated = FindReturnedInterpolatedString(methodSyntax);
        if (interpolated is null)
            return null;

        // ITM-573：插值洞引用非静态可解析符号（方法参数、局部变量、实例成员）时，
        // 提升为静态字段会产生 CS0103/CS0120——语义模型逐符号判定，命中即拒绝生成
        // （此前只查参数名集合，局部变量漏网，生成物编译失败且错误指向 .g.cs 难定位）。
        SemanticModel semanticModel = ctx.SemanticModel;
        foreach (var interpolation in interpolated.Contents.OfType<InterpolationSyntax>())
        {
            foreach (var identifier in interpolation.Expression
                // r14-S2：泛型调用的方法名是 GenericNameSyntax 非 IdentifierNameSyntax——
                // .OfType<IdentifierNameSyntax>() 漏枚举，{M<int>()} 三代同漏。并集两形态
                .DescendantNodesAndSelf().OfType<SyntaxNode>()
                .Where(n => n is IdentifierNameSyntax or GenericNameSyntax))
            {
                ISymbol? symbol = semanticModel.GetSymbolInfo(identifier, ct).Symbol;
                // r11.5-D1(P2)：所在类静态成员的非限定引用（{TablePrefix}）同样在生成物
                // SqlTemplates（namespace 级静态类）内不可解析——CS0103 仍是 ITM-573 要
                // 消除的"错误指向 .g.cs"形态。放行的静态成员仅限生成类自身可解析的：
                // 限定引用（{Repos.X}/{Math.PI}——右 identifier 父为 MemberAccess，全部放行）
                if (symbol is ILocalSymbol or IParameterSymbol
                    || (symbol is IFieldSymbol or IPropertySymbol && !symbol.IsStatic)
                    // r5-A3：实例方法调用（{GetId()}）漏滤会生成静态类内 CS0120——
                    || (symbol is IMethodSymbol invokedMethod && !invokedMethod.IsStatic)
                    // r11.5-D1：非限定静态引用（{TablePrefix}——identifier 直接为洞表达式，
                    // 非 X.Y 成员访问的右操作数）在生成类 SqlTemplates 内不可解析。
                    // 限定引用（{Repos.TablePrefix}/{Math.PI}）的右 identifier 父节点是
                    // MemberAccess，不受本分支影响（保持放行）
                    || (symbol is (IFieldSymbol or IPropertySymbol) and { IsStatic: true }
                        && identifier.Parent is not MemberAccessExpressionSyntax)
                    // r13-A（D-1 同族方法分支）：非限定静态方法调用（{M()}）——同类静态
                    // 方法用户源码可解析，生成到 SqlTemplates 同样 CS0103
                    || (symbol is IMethodSymbol and { IsStatic: true }
                        && identifier.Parent is not MemberAccessExpressionSyntax))
                {
                    return null;
                }
            }
        }

        string ns = method.ContainingNamespace?.IsGlobalNamespace == true
            ? "PalORM.Generated"
            : method.ContainingNamespace?.ToDisplayString() ?? "PalORM.Generated";

        // 原样搬运插值表达式语法——转义、嵌套引号、插值洞由 Roslyn 保证合法
        string literal = interpolated.ToFullString().Trim();
        return new SqlTemplateModel(ns, templateName, literal,
            method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    /// <summary>渲染单个模板文件（去重后由生成器逐个调用）。</summary>
    internal static string Render(SqlTemplateModel model)
        => $@"// <auto-generated/>
#nullable enable
// r13-B：限定引用依赖 using 指令解析（{Math.PI}/{DayOfWeek.Monday}）——生成物零 using
// 时无 ImplicitUsings 项目将 CS0246。全局 using 覆盖 BCL 主命名空间（ITM-573 家族：
// 错误不得指向 .g.cs）
global using System;

namespace {model.Namespace};

/// <summary>编译期 SQL 模板——与 QueryBuilder.AsPrepared() 配合调用 DbCommand.PrepareAsync。</summary>
public static partial class SqlTemplates
{{
    /// <summary>模板: {model.TemplateName}</summary>
    public static readonly global::System.FormattableString {model.TemplateName} = {model.Literal};
}}
";

    /// <summary>取方法返回值中的插值串（ITM-521）：仅接受"返回表达式直接是单个插值串"
    /// 的单一形态。ITM-661：条件表达式（b ? $"A" : $"B"）或多 return 会静默取首个、
    /// 模板与运行时方法语义分叉——改为整体返回 null（模板不生成、方法保持手写实现），
    /// 永不生成错误 SQL。前置日志插值不再可达（只取直接表达式，不搜索子树）。</summary>
    private static InterpolatedStringExpressionSyntax? FindReturnedInterpolatedString(
        MethodDeclarationSyntax methodSyntax)
    {
        // 表达式体方法（箭头语法）——返回表达式必须直接是插值串
        if (methodSyntax.ExpressionBody is { } arrow)
        {
            return arrow.Expression as InterpolatedStringExpressionSyntax;
        }

        // 块体方法：恰一个 return 语句且其表达式直接是插值串
        ReturnStatementSyntax? singleReturn = null;
        foreach (var returnStatement in methodSyntax.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (singleReturn is not null) return null;  // 多 return：跳过生成
            singleReturn = returnStatement;
        }
        return singleReturn?.Expression as InterpolatedStringExpressionSyntax;
    }
}
