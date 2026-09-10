using PalORM;

namespace PalORM.SourceGen.Tests;

/// <summary>约定式复制防漂移测试（评审 P2-5 同源面）。
/// SourceGen 的标识符守卫（SqlGeneration.QuoteIdentifier / SourceGenerationValidation.HasUnsafeSqlIdentifier）
/// 与运行时 IdentifierSafety.ThrowIfUnsafe 是两份手工镜像——netstandard2.0 不能引用 Core
/// （ITM-618），数值范围靠注释约定同步。本测试在全 BMP 码位上锁定两侧判定一致，
/// 任一侧扩展范围而另一侧未跟时立即失败。
/// <para><b>ITM-738/739(r20) 收口</b>：原登记的有意分歧（编译期额外拒绝空串/纯空白、
/// 运行时接受）已消除——两侧现在都拒绝空/纯空白，且控制字符异常类型统一为
/// <see cref="ArgumentException"/>。镜像范围 = 控制字符区间 + 空/空白。</para></summary>
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
            if (char.IsWhiteSpace(ch)) continue;  // 空白由 WhitespaceIdentifier 专项覆盖

            if (RuntimeRejects(ch) != SourceGenRejects(ch))
                mismatches.Add(ch);
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task WhitespaceOnlyIdentifier_RejectedByBothSides()
    {
        // ITM-738/739(r20)：空串/纯空白两侧一致拒绝（原分歧已消除，ITM-739）
        await Assert.That(SourceGenRejects(' ')).IsTrue();
        await Assert.That(RuntimeRejects(' ')).IsTrue();
        await Assert.That(EmptyRejectedByBothSides()).IsTrue();
    }

    private static bool EmptyRejectedByBothSides()
    {
        bool runtimeRejects;
        try
        {
            global::PalORM.IdentifierSafety.ThrowIfUnsafe("");
            runtimeRejects = false;
        }
        catch (ArgumentException) { runtimeRejects = true; }

        return runtimeRejects && SourceGenRejects("");
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

    private static bool SourceGenRejects(char ch) => SourceGenRejects(ch.ToString());

    private static bool SourceGenRejects(string identifier)
    {
        try
        {
            global::PalORM.SourceGen.SqlGeneration.QuoteIdentifier(
                identifier, global::PalORM.SourceGen.SqlGenerationDialect.Sqlite);
            return false;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }
}
