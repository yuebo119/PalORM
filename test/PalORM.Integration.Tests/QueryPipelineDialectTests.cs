using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

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
