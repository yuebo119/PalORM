using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

[Projection]
internal readonly record struct PipelinePgStruct(long id, string name, long value);

[Table("palorm_pipeline_probe")]
internal sealed partial class PipelinePgRow
{
    [Key(AutoIncrement = false)][Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("value")] public long value { get; set; }
}

/// <summary>查询管道的 PostgreSQL 真批处理路径：一次 <c>NpgsqlBatch</c> 承载多条独立查询，
/// 结果按入队顺序返回；参数命名在各批命令内独立；事务绑定与回滚可见性。</summary>
[NotInParallel("ExtBulkTable")]
internal sealed class QueryPipelineDialectTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task PostgreSql_PipelineStruct_ReturnsRows()
    {
        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pipeline_struct_probe");
        await session.ExecuteAsync(
            $"CREATE TABLE palorm_pipeline_struct_probe (id BIGINT PRIMARY KEY, name TEXT NOT NULL, value BIGINT NOT NULL)");
        try
        {
            await session.ExecuteAsync(
                $"INSERT INTO palorm_pipeline_struct_probe VALUES (1, 'a', 10), (2, 'b', 20), (3, 'c', 30)");
            var pipeline = session.CreatePipeline();
            PipelineStructQuery<PipelinePgStruct, PostgreSqlProvider> filtered =
                pipeline.QueryStructs<PipelinePgStruct>(
                    $"SELECT id, name, value FROM palorm_pipeline_struct_probe WHERE id >= {2L} ORDER BY id");
            PipelineStructQuery<PipelinePgStruct, PostgreSqlProvider> single =
                pipeline.QueryStructs<PipelinePgStruct>(
                    $"SELECT id, name, value FROM palorm_pipeline_struct_probe WHERE id = {1L}");
            await pipeline.ExecuteAsync();
            await Assert.That(filtered.Value.Count).IsEqualTo(2);
            await Assert.That(filtered.Value[0]).IsEqualTo(new PipelinePgStruct(2, "b", 20));
            await Assert.That(filtered.Value[1]).IsEqualTo(new PipelinePgStruct(3, "c", 30));
            await Assert.That(single.Value.Count).IsEqualTo(1);
            await Assert.That(single.Value[0]).IsEqualTo(new PipelinePgStruct(1, "a", 10));
            pipeline.Dispose();
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pipeline_struct_probe");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task PostgreSql_Pipeline_MatchesSequential_AndHonorsTransaction()
    {
        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pipeline_probe");
        await session.ExecuteAsync(
            $"CREATE TABLE palorm_pipeline_probe (id BIGINT PRIMARY KEY, name TEXT NOT NULL, value BIGINT NOT NULL)");
        try
        {
            await session.ExecuteAsync(
                $"INSERT INTO palorm_pipeline_probe (id, name, value) VALUES (1, 'a', 10), (2, 'b', 20), (3, 'c', 30)");

            List<PipelinePgRow> expectedList =
                await session.From<PipelinePgRow>().Where($"\"value\" >= {20L}").ToListAsync();
            PipelinePgRow? expectedFirst =
                await session.From<PipelinePgRow>().Where($"\"id\" = {2L}").FirstOrDefaultAsync();

            var pipeline = session.CreatePipeline();
            PipelineQuery<PipelinePgRow, PostgreSqlProvider> list =
                pipeline.Query(session.From<PipelinePgRow>().Where($"\"value\" >= {20L}"));
            PipelineFirst<PipelinePgRow, PostgreSqlProvider> first =
                pipeline.QueryFirst(session.From<PipelinePgRow>().Where($"\"id\" = {2L}"));
            PipelineScalar<PostgreSqlProvider> count =
                pipeline.Count(session.From<PipelinePgRow>());
            await pipeline.ExecuteAsync();
            await Assert.That(list.Value.Select(static row => row.id).ToArray())
                .IsEquivalentTo(expectedList.Select(static row => row.id).ToArray());
            await Assert.That(first.Value?.id).IsEqualTo(expectedFirst?.id);
            await Assert.That(count.Value).IsEqualTo(3L);
            pipeline.Dispose();

            await using (var transaction = await session.BeginTransactionAsync())
            {
                await session.InsertAsync(new PipelinePgRow { id = 4, name = "d", value = 40 });
                var inTransaction = session.CreatePipeline();
                PipelineScalar<PostgreSqlProvider> inCount =
                    inTransaction.Count(session.From<PipelinePgRow>());
                await inTransaction.ExecuteAsync();
                await Assert.That(inCount.Value).IsEqualTo(4L);
                inTransaction.Dispose();
                await transaction.RollbackAsync();
            }

            var afterRollback = session.CreatePipeline();
            PipelineScalar<PostgreSqlProvider> afterCount =
                afterRollback.Count(session.From<PipelinePgRow>());
            await afterRollback.ExecuteAsync();
            await Assert.That(afterCount.Value).IsEqualTo(3L);
            afterRollback.Dispose();
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pipeline_probe");
        }
    }
}
