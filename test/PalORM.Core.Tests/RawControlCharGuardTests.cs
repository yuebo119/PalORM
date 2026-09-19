using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// 独立审计 2026-09-19 M1-1 防线——<see cref="QueryBuilder{T}.Raw"/> 的控制字符拒绝。
/// 与 <see cref="QueryBuilder{T}.Tag"/>（拒注释定界符+NUL）及三 Provider 标识符路径
/// （<see cref="IdentifierSafety.ThrowIfUnsafe"/>）对齐：NUL 截断向量 ITM-584 已被项目
/// 自己实测证明——片段含 NUL 时驱动截断语句，后续条件失效可扩大 UPDATE 影响面。
/// 反向用例锁定：引号/反引号不拒（Raw 的合法用途必然含它们）。
/// </summary>
public sealed class RawControlCharGuardTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

    [Test]
    [Arguments('\0')]
    [Arguments('\n')]
    [Arguments('\r')]
    [Arguments('\x7F')]   // DEL
    [Arguments('\x85')]   // C1 NEL
    public async Task Raw_ControlChar_IsRejected(char poison)
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        await Assert.That(() => session.From<RawProbeEntity>()
            .Raw($"AND id > 1 {poison} AND tenant = 'x'"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Raw_LegalQuotedFragment_Passes()
    {
        // 反向：含引号/反引号的合法片段必须通过（防线只拒控制字符，不做引号平衡——
        // 那是不可判定问题且会误伤合法片段）
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        string sql = session.From<RawProbeEntity>()
            .Raw("AND name = 'it''s fine' AND note = \"quoted\"").ToSql();

        await Assert.That(sql).Contains("AND name = 'it''s fine'");
    }
}

[Table("raw_guard_probe")]
internal sealed partial class RawProbeEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = "";
}
