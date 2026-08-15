using Microsoft.Data.Sqlite;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>ITM-403 下沉防线：真库触发 UNIQUE/PK/NOT NULL/FK 四类约束异常，
/// 断言 IsUniqueViolation 仅对前两类为真。前轮用手工构造异常（无扩展码）验证是
/// 验证方法缺陷——本测试确保扩展码在真驱动路径实际生效。</summary>
public sealed class SqliteErrorCodeMatrixTests
{
    [Test]
    public async Task UniqueViolation_IsDetected_ByRealDriver()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new UniqueMatrixEntity { Code = "A", RequiredValue = "x" });
        try
        {
            await db.InsertAsync(new UniqueMatrixEntity { Code = "A", RequiredValue = "y" });
            throw new InvalidOperationException("Expected unique violation");
        }
        catch (SqliteException ex)
        {
            await Assert.That(SqliteProvider.IsUniqueViolation(ex)).IsTrue();
        }
    }

    [Test]
    public async Task NotNullViolation_IsNotMisreportedAsUnique()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var raw = db.GetRawConnection();
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = "INSERT INTO uniq_matrix (code, required_value) VALUES ('B', NULL)";
        try
        {
            await cmd.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Expected NOT NULL violation");
        }
        catch (SqliteException ex)
        {
            await Assert.That(ex.SqliteErrorCode).IsEqualTo(19);
            await Assert.That(SqliteProvider.IsUniqueViolation(ex)).IsFalse();
        }
    }

    [Test]
    public async Task ForeignKeyViolation_IsNotMisreportedAsUnique()
    {
        await using var db = await TestDb.SqliteAsync();
        var raw = db.GetRawConnection();
        await using (var ddl = raw.CreateCommand())
        {
            ddl.CommandText =
                "CREATE TABLE fk_parent (id INTEGER PRIMARY KEY); " +
                "CREATE TABLE fk_child (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES fk_parent(id))";
            await ddl.ExecuteNonQueryAsync();
        }
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = "INSERT INTO fk_child (parent_id) VALUES (999)";
        try
        {
            await cmd.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Expected FK violation");
        }
        catch (SqliteException ex)
        {
            await Assert.That(ex.SqliteErrorCode).IsEqualTo(19);
            await Assert.That(SqliteProvider.IsUniqueViolation(ex)).IsFalse();
        }
    }

    [Test]
    public async Task PrimaryKeyViolation_IsDetected_ByRealDriver()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var raw = db.GetRawConnection();
        await using (var seed = raw.CreateCommand())
        {
            seed.CommandText = "INSERT INTO uniq_matrix (Id, code, required_value) VALUES (7, 'C', 'x')";
            await seed.ExecuteNonQueryAsync();
        }
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = "INSERT INTO uniq_matrix (Id, code, required_value) VALUES (7, 'D', 'y')";
        try
        {
            await cmd.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Expected PK violation");
        }
        catch (SqliteException ex)
        {
            await Assert.That(SqliteProvider.IsUniqueViolation(ex)).IsTrue();
        }
    }
}

#region Test Entities
[Table("uniq_matrix")]
public partial class UniqueMatrixEntity
{
    [Key] public long Id { get; set; }
    [Column("code")] [Unique] public string Code { get; set; } = "";
    [Column("required_value")] public string RequiredValue { get; set; } = "";
}
#endregion
