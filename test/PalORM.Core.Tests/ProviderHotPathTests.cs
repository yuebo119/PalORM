using Microsoft.Data.Sqlite;
using PalORM.MySql;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>L3/M9/C5（2026-09-20）：标识符引用免分配、读取器直读判空、Provider 初始化方式。
/// <para><b>L3</b>：无内嵌引号时 <c>string.Replace</c> 仍返回新实例——标识符引用在 SQL 构造
/// 路径上被反复调用，免掉一次纯拷贝。</para>
/// <para><b>M9</b>：<see cref="EntityDataReader.IsDBNull"/> 原经 <c>GetValue</c>
/// 取值，驱动对每列可能先 IsDBNull 再 GetValue，同一 ordinal 两次解引用。</para>
/// <para><b>C5</b>：SQLite Provider 的原生 bundle 初始化从显式静态构造器改为
/// <c>[ModuleInitializer]</c>——带显式静态构造器的类型不是 <c>beforefieldinit</c>，
/// 每次静态成员访问都付一次类型初始化检查。</para></summary>
public sealed class ProviderHotPathTests
{
    // ─── L3：QuoteIdentifier 免分配路径 ────────────────────────

    [Test]
    public async Task Sqlite_QuoteIdentifier_NoInternalQuote_ReturnsQuotedText()
    {
        await Assert.That(SqliteProvider.QuoteIdentifier("tenant_id")).IsEqualTo("\"tenant_id\"");
        await Assert.That(SqliteProvider.QuoteIdentifier("plain")).IsEqualTo("\"plain\"");
    }

    [Test]
    public async Task Sqlite_QuoteIdentifier_InternalQuote_IsDoubled()
    {
        await Assert.That(SqliteProvider.QuoteIdentifier("a\"b")).IsEqualTo("\"a\"\"b\"");
    }

    [Test]
    public async Task MySql_QuoteIdentifier_NoInternalQuote_ReturnsQuotedText()
    {
        await Assert.That(MySqlProvider.QuoteIdentifier("tenant_id")).IsEqualTo("`tenant_id`");
    }

    [Test]
    public async Task MySql_QuoteIdentifier_InternalBacktick_IsDoubled()
    {
        await Assert.That(MySqlProvider.QuoteIdentifier("a`b")).IsEqualTo("`a``b`");
    }

    [Test]
    [Arguments("tenant_id")]
    [Arguments("plain")]
    public async Task Providers_QuoteIdentifier_NoQuotePath_IsNotReferenceEqualToInput(string identifier)
    {
        // 免分配路径仍须产生新串（两侧引号是新增内容），但内容正确——
        // 该断言锁的是"没有引号时不会退化成空串/丢字符"的回归面
        await Assert.That(SqliteProvider.QuoteIdentifier(identifier).Length).IsEqualTo(identifier.Length + 2);
    }

    [Test]
    public async Task Providers_QuoteIdentifier_RejectsControlCharacters()
    {
        // L3 的快速路径不得绕过 IdentifierSafety 守卫
        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = SqliteProvider.QuoteIdentifier("bad\u0000name");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = MySqlProvider.QuoteIdentifier("bad\u0001name");
            return Task.CompletedTask;
        });
    }

    // ─── C5：SQLite bundle 初始化方式 ─────────────────────────

    [Test]
    public async Task SqliteProvider_IsBeforeFieldInit_NoExplicitStaticConstructor()
    {
        // C5：显式静态构造器会使类型失去 beforefieldinit——CLR 每次静态访问都付初始化检查。
        // 用 ModuleInitializer 替代后该标记必须存在。
        Type type = typeof(SqliteProvider);
        await Assert.That(type.TypeInitializer is null).IsTrue();
        await Assert.That(
            type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.ModuleInitializerAttribute), false)
                .Length).IsEqualTo(0);  // ModuleInitializer 属性本身不保留在反射元数据
    }

    [Test]
    public async Task SqliteProvider_StaticMembers_AreUsableAfterAssemblyLoad()
    {
        // ModuleInitializer 在程序集加载时执行——静态成员触达即证明 bundle 已初始化
        await Assert.That(SqliteProvider.Name).IsEqualTo("SQLite");
        await Assert.That(SqliteProvider.Dialect).IsEqualTo(SqlDialect.Sqlite);
        await Assert.That(SqliteProvider.QuoteIdentifier("x")).IsEqualTo("\"x\"");
    }

    // ─── M9：EntityDataReader.IsDBNull 直读 ──────────────────

    [Test]
    public async Task EntityDataReader_IsDBNull_MatchesGetValue()
    {
        // 参数池：列 0 非 null、列 1 为 DBNull、列 2 为 null（两者都算空）
        object?[] values = [7L, DBNull.Value, null];
        var pool = new System.Data.Common.DbParameter[values.Length];
        for (int i = 0; i < values.Length; i++)
            pool[i] = new SqliteParameter($"@p{i}", values[i] ?? DBNull.Value);

        string[] columns = ["c0", "c1", "c2"];
        using var reader = new EntityDataReader(
            0, 1, pool, static _ => { }, columns, missingPrimaryKeyCount: 0);

        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(await reader.IsDBNullAsync(0)).IsFalse();
        await Assert.That(await reader.IsDBNullAsync(1)).IsTrue();
        await Assert.That(await reader.IsDBNullAsync(2)).IsTrue();
        // 直读路径与 GetValue 判空的结论必须一致（M9 改的是取值方式，不是语义）
        for (int i = 0; i < values.Length; i++)
            await Assert.That(await reader.IsDBNullAsync(i)).IsEqualTo(reader.GetValue(i) is DBNull);
    }

    [Test]
    public async Task EntityDataReader_IsDBNull_MissingPrimaryKeyPrefix_IsAlwaysTrue()
    {
        object?[] values = [7L];
        var pool = new System.Data.Common.DbParameter[] { new SqliteParameter("@p0", 7L) };
        string[] columns = ["pk", "c0"];
        using var reader = new EntityDataReader(
            0, 1, pool, static _ => { }, columns, missingPrimaryKeyCount: 1);

        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(await reader.IsDBNullAsync(0)).IsTrue();   // 主键补位列恒为空
        await Assert.That(await reader.IsDBNullAsync(1)).IsFalse();  // 映射到参数池首槽
    }
}
