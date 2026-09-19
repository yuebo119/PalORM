using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>INSERT RETURNING 收窄（v5.7）的行为契约。
/// <para>生成器静态判定「唯一自增主键之外全部列可直接插入且无转换器/OwnedJson/
/// IgnoreOnInsert/Computed/Timestamp」时，SQL 收窄为 <c>RETURNING "Id"</c> 且
/// Core 走标量路径——返回**调用方实体**（引用相等）+ 回填 ID。
/// 不满足条件的实体保持整行 RETURNING + 物化（DB 默认值/计算列以返回实例为准）。</para>
/// <para>这两个用例各钉一条路径；收窄条件回归（误收窄/漏收窄）都会让其一变红。</para></summary>
internal sealed class InsertReturningNarrowTests
{
    [Test]
    public async Task KeyOnlyEntity_InsertReturnsCallerEntity_WithBackfilledId()
    {
        using var keeper = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=narrow_tests;Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=narrow_tests;Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE narrow_insert (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        try
        {
            var entity = new NarrowInsertEntity { Name = "a", Qty = 1 };
            NarrowInsertEntity returned = await session.InsertAsync(entity);

            // 收窄路径返回调用方实体（结果集与插入值恒等——物化等价），ID 已回填
            await Assert.That(ReferenceEquals(returned, entity)).IsTrue();
            await Assert.That(returned.Id).IsGreaterThan(0);
            await Assert.That(returned.Name).IsEqualTo("a");

            // 值真实落库（标量路径没有丢列）
            NarrowInsertEntity? stored = await session.GetAsync<NarrowInsertEntity>(returned.Id);
            await Assert.That(stored).IsNotNull();
            await Assert.That(stored!.Qty).IsEqualTo(1);
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS narrow_insert");
        }
    }

    [Test]
    public async Task IgnoreOnInsertEntity_InsertKeepsMaterializedDefaultFromDb()
    {
        using var keeper = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=narrow_tests;Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=narrow_tests;Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE wide_insert (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, created_by TEXT NOT NULL DEFAULT 'database')");
        try
        {
            // IgnoreOnInsert 列不进 INSERT——DB 默认值与调用方实体值可能不同，
            // 物化返回才是真源（收窄条件正确地排除了此类实体）
            var entity = new WideInsertEntity { Name = "b", CreatedBy = "client" };
            WideInsertEntity returned = await session.InsertAsync(entity);

            await Assert.That(returned.Id).IsGreaterThan(0);
            await Assert.That(returned.CreatedBy).IsEqualTo("database");
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS wide_insert");
        }
    }
}

[Table("narrow_insert")]
internal sealed partial class NarrowInsertEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
}

[Table("wide_insert")]
internal sealed partial class WideInsertEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("created_by")][IgnoreOnInsert] public string CreatedBy { get; set; } = "client";
}
