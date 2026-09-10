namespace PalORM.SourceGen;

/// <summary>生成物元数据常量——单一真源，避免多处字面量漂移（ITM-727）。
/// <para><b>GeneratedCodeAttribute 版本语义</b>：该参数是"生成工具的版本"，用于诊断工具识别
/// 生成物来源。本仓库把它与包版本对齐（<c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c>），
/// 使"看生成物头部即知由哪个版本生成"成立。二者一致性由
/// <c>.ai/scripts/doc-consistency-check.sh</c> 的 D11 项机械校验——改版本号必须同改此处。</para></summary>
internal static class GeneratedCodeMetadata
{
    /// <summary>生成工具（本包）版本——须与 Directory.Build.props 的 Version 一致（D11 校验）。</summary>
    internal const string ToolVersion = "5.4.0";

    /// <summary>生成物头部的 GeneratedCode 特性行。五处 Emitter 共用此常量——版本只在上一行改一次。</summary>
    internal const string GeneratedCodeAttribute =
        "[global::System.CodeDom.Compiler.GeneratedCode(\"PalORM.SourceGen\", \"" + ToolVersion + "\")]";
}
