using Npgsql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

public sealed class DiagPgTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task RawNpgsql_FourRows_ManualParams()
    {
        await using var conn = new NpgsqlConnection(TestEnvironment.ResolvePostgreSqlConnectionString());
        await conn.OpenAsync();
        await using (var ddl = new NpgsqlCommand("DROP TABLE IF EXISTS diag4; CREATE TABLE diag4 (id INT PRIMARY KEY, qty INT NOT NULL, label VARCHAR(32) NOT NULL); INSERT INTO diag4 VALUES (1,0,'a'),(2,0,'b'),(3,0,'c'),(4,0,'d')", conn))
            await ddl.ExecuteNonQueryAsync();
        // 4 行 × 3 参数 = @p0..@p11,与 builder 的 4 行版完全同构
        await using var cmd = new NpgsqlCommand(
            "UPDATE diag4 AS tgt SET qty = v.col0, label = v.col1 FROM (VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8), (@p9, @p10, @p11)) AS v(col0, col1, col_pk) WHERE tgt.id = v.col_pk", conn);
        for (int i = 0; i < 12; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@p" + i;
            p.Value = DBNull.Value;
            cmd.Parameters.Add(p);
        }
        for (int r = 0; r < 4; r++)
        {
            int row = r + 1;
            cmd.Parameters["@p" + (r * 3)].Value = 10L * row;
            cmd.Parameters[("@p" + (r * 3 + 1))].Value = ("u" + row);
            cmd.Parameters[("@p" + (r * 3 + 2))].Value = (long)row;
        }
        int affected = await cmd.ExecuteNonQueryAsync();
        throw new InvalidOperationException("RAW4_OK affected=" + affected);
    }
}
