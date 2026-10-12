namespace PalORM.Core.Tests;

/// <summary>C17（2026-10-12 全量审读）：FormattableString 引号内花括号与引号状态机。
/// <para>旧行为三缺陷：'{0}' 被替换为字面 '@p0' 文本（参数与文本错位）；JSON 文本
/// '{"a":1}' 直接抛 FormatException；'{"a":1}' 里的 " 打开标识符态永不关闭，其后
/// 占位符替换与裸 @pN 拦截全部失真。修复后引号内一律透传，{{ }} 转义全局语义不变。</para></summary>
public sealed class FormattableLiteralBraceTests
{
    [Test]
    public async Task PlaceholderInsideStringLiteral_PassesThroughVerbatim()
    {
        string formatted = FormattableSqlFormatter.Format("name = '{0}' AND id = {1}", 0, 2);
        await Assert.That(formatted).IsEqualTo("name = '{0}' AND id = @p1");
    }

    [Test]
    public async Task JsonLiteralWithDoubleQuotes_PassesThroughAndStateResets()
    {
        // 旧实现：'{"a":1}' 抛 FormatException；即便放进引号，" 使标识符态卡死，
        // 其后的 {0} 不再被替换。修复后：引号内透传 + 状态正确闭合 + 后续占位符正常替换。
        string formatted = FormattableSqlFormatter.Format("meta = '{\"a\":1}' AND id = {0}", 0, 1);
        await Assert.That(formatted).IsEqualTo("meta = '{\"a\":1}' AND id = @p0");
    }

    [Test]
    public async Task LoneCloseBraceInsideLiteral_PassesThrough()
    {
        string formatted = FormattableSqlFormatter.Format("x = 'a}b' AND id = {0}", 0, 1);
        await Assert.That(formatted).IsEqualTo("x = 'a}b' AND id = @p0");
    }

    [Test]
    public async Task EscapedBracesInsideLiteral_StillCollapse()
    {
        // 既有转义写法不回归：'{{' 全局折叠为单个 '{'（与旧实现一致）
        string formatted = FormattableSqlFormatter.Format("x = '{{0}}' AND id = {0}", 0, 1);
        await Assert.That(formatted).IsEqualTo("x = '{0}' AND id = @p0");
    }

    [Test]
    public async Task BareReservedParameter_OutsideLiteral_StillRejected()
    {
        // T9 拦截不放松：引号外的裸 @p1 仍然拒绝（引号内透传不构成新旁路）
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => FormattableSqlFormatter.Format("id = @p1 AND x = '{0}'", 0, 1)));
    }

    [Test]
    public async Task UnescapedCloseBrace_OutsideLiteral_StillThrows()
    {
        await Assert.ThrowsAsync<FormatException>(async () =>
            await Task.Run(() => FormattableSqlFormatter.Format("id = } AND x = {0}", 0, 1)));
    }

    [Test]
    public async Task DoubledQuoteInsideLiteral_StaysLiteral()
    {
        // 引号内成对续写：'it''s' 的 '' 仍是字面量数据，其后占位符正常替换
        string formatted = FormattableSqlFormatter.Format("name = 'it''s' AND id = {0}", 0, 1);
        await Assert.That(formatted).IsEqualTo("name = 'it''s' AND id = @p0");
    }
}
