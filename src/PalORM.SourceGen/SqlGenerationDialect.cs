namespace PalORM.SourceGen;

internal enum SqlGenerationDialect
{
    Sqlite,
    PostgreSql,
    MySql
}

internal static class SqlGeneration
{
    /// <summary>编译期标识符引用（引用符翻倍转义）。
    /// <para>ITM-618：内联运行时 IdentifierSafety 等价守卫（SourceGen 零依赖 Core 不可复用）——
    /// [Table]/[Column] 名是编译期常量，可含 C0/DEL/C1 控制字符；不经守卫直接进入生成 SQL
    /// 会绕过运行时三 Provider 的 ThrowIfUnsafe（NUL 截断驱动 C 层，ITM-584/593 动机）。
    /// 抛异常而非诊断的取舍：上游 TableModel.FromContext 已对全部消费面（表/列/索引/FK 名）
    /// 先过 HasUnsafeSqlIdentifier 快检并经 PALORM043 + PALORM045 兜底诊断呈现（评审 2026-09-02），
    /// 本 throw 是共享 helper 的不可达不变量断言——新增消费点必须先过该快检。</para></summary>
    internal static string QuoteIdentifier(string identifier, SqlGenerationDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new System.ArgumentException("Identifier must be non-empty.", nameof(identifier));
        foreach (char ch in identifier)
        {
            if (ch < ' ' || (ch >= '\x7F' && ch <= '\x9F'))
                // ITM-739(r20)：异常类型与运行时 IdentifierSafety.ThrowIfUnsafe 对齐（ArgumentException
                // 而非 InvalidOperationException）——两侧拒绝面/异常形态一致，避免调用方分流判据分叉。
                throw new System.ArgumentException(
                    $"Identifier '{identifier}' contains control character U+{(int)ch:X4}; " +
                    "generated SQL cannot embed control characters (mirrors runtime IdentifierSafety).",
                    nameof(identifier));
        }
        char quote = dialect == SqlGenerationDialect.MySql ? '`' : '"';
        string escaped = identifier.Replace(
            quote.ToString(),
            new string(quote, 2));
        return $"{quote}{escaped}{quote}";
    }
}
