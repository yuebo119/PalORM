using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>2026-10-22 终审 P1-4：plain readonly struct 投影的端到端物化。
/// 生成器 predicate 已收 StructDeclarationSyntax；本用例锁定 QueryStructsAsync 能查到
/// 该类型的 RowFactory 并按序号物化。</summary>
public sealed class PlainStructProjectionTests
{
    [Test]
    public async Task QueryStructsAsync_PlainReadonlyStruct_MapsByOrdinal()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:"
        });
        await db.ExecuteAsync($"CREATE TABLE plain_probe (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)");
        await db.ExecuteAsync($"INSERT INTO plain_probe (Id, Name) VALUES (1, 'a')");
        await db.ExecuteAsync($"INSERT INTO plain_probe (Id, Name) VALUES (2, 'b')");

        var rows = await db.QueryStructsAsync<PlainSummary>(
            $"SELECT Id, Name FROM plain_probe ORDER BY Id");

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].Id).IsEqualTo(1L);
        await Assert.That(rows[0].Name).IsEqualTo("a");
        await Assert.That(rows[1].Id).IsEqualTo(2L);
    }
}

[Projection]
public readonly struct PlainSummary : IEquatable<PlainSummary>
{
    public long Id { get; init; }
    public string Name { get; init; }

    public bool Equals(PlainSummary other)
        => Id == other.Id && string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PlainSummary other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Id, Name);

    public static bool operator ==(PlainSummary left, PlainSummary right) => left.Equals(right);

    public static bool operator !=(PlainSummary left, PlainSummary right) => !left.Equals(right);
}
