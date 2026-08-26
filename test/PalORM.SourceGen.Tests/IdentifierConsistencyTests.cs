using PalORM;

namespace PalORM.SourceGen.Tests;

/// <summary>约定式复制防漂移测试（评审 P2-5 同源面）。
/// SourceGen 的标识符守卫（SqlGeneration.QuoteIdentifier / SourceGenerationValidation.HasUnsafeSqlIdentifier）
/// 与运行时 IdentifierSafety.ThrowIfUnsafe 是两份手工镜像——netstandard2.0 不能引用 Core
/// （ITM-618），数值范围靠注释约定同步。本测试在全 BMP 码位上锁定两侧判定一致，
/// 任一侧扩展范围而另一侧未跟时立即失败。
/// <para><b>已登记的有意分歧</b>：编译期额外拒绝空串/纯空白（Roslyn 注解字符串可携带任意值，
/// 属编译面加固）；运行时仅拒控制字符（历史契约，动态标识符防御层）。镜像承诺的范围是
/// 控制字符区间本身。</para></summary>
public sealed class IdentifierConsistencyTests
{
    [Test]
    public async Task ControlCharacters_RejectedByBothRuntimeAndSourceGen()
    {
        // C0（U+0000-U+001F）+ DEL（U+007F）+ C1（U+0080-U+009F）：两侧必须一致拒绝
        List<char> controlViolations = [];
        for (int codeUnit = 0; codeUnit <= 0xFFFF; codeUnit++)
        {
            char ch = (char)codeUnit;
            bool inControlRange = ch is (< ' ') or (>= '\x7F' and <= '\x9F');
            if (!inControlRange) continue;

            bool runtimeRejects = RuntimeRejects(ch);
            bool sourceGenRejects = SourceGenRejects(ch);
            if (!runtimeRejects || !sourceGenRejects)
                controlViolations.Add(ch);
        }

        // 失败时 TUnit 会打印集合内容（漂移码位清单）
        await Assert.That(controlViolations).IsEmpty();
    }

    [Test]
    public async Task NonControlPrintableCharacters_AcceptedByBothSides()
    {
        // 非控制字符且非空白：两侧必须一致放行（含引号——引号走转义而非拒绝）
        List<char> mismatches = [];
        for (int codeUnit = 0; codeUnit <= 0xFFFF; codeUnit++)
        {
            char ch = (char)codeUnit;
            if (ch is (< ' ') or (>= '\x7F' and <= '\x9F')) continue;
            if (char.IsWhiteSpace(ch)) continue;  // 已登记的有意分歧面，见类文档

            if (RuntimeRejects(ch) != SourceGenRejects(ch))
                mismatches.Add(ch);
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task WhitespaceOnlyIdentifier_CompileTimeRejects_RuntimeAccepts()
    {
        // 锁定已登记的有意分歧：编译期对空串/纯空白加固拒绝；运行时历史契约接受非控制字符。
        // 若未来任一侧语义变更，本用例强迫同步更新类文档与另一侧决策。
        await Assert.That(SourceGenRejects(' ')).IsTrue();
        await Assert.That(RuntimeRejects(' ')).IsFalse();
    }

    private static bool RuntimeRejects(char ch)
    {
        try
        {
            global::PalORM.IdentifierSafety.ThrowIfUnsafe(ch.ToString());
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool SourceGenRejects(char ch)
    {
        try
        {
            global::PalORM.SourceGen.SqlGeneration.QuoteIdentifier(
                ch.ToString(), global::PalORM.SourceGen.SqlGenerationDialect.Sqlite);
            return false;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }
}
