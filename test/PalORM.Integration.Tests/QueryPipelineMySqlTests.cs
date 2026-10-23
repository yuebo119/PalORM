using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

[Table("palorm_pipeline_mysql_probe")]
internal sealed partial class PipelineMySqlRow
{
    [Key(AutoIncrement = false)][Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("value")] public long value { get; set; }
}

/// <summary>查询管道的 MySQL 批处理路径：一次 <c>MySqlBatch</c> 承载多条独立查询，
/// 结果集顺序/参数命名/事务绑定与 PostgreSQL 路径一致。</summary>
[NotInParallel("ExtBulkTable")]
internal sealed class QueryPipelineMySqlTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task MySql_Pipeline_MatchesSequential_AndHonorsTransaction()
    {
        await using var session = await DataSession<MySqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pipeline_mysql_probe");
        await session.ExecuteAsync(
            $"CREATE TABLE palorm_pipeline_mysql_probe (id BIGINT PRIMARY KEY, name VARCHAR(64) NOT NULL, value BIGINT NOT NULL)");
        try
        {
            await session.ExecuteAsync(
                $"INSERT INTO palorm_pipeline_mysql_probe (id, name, value) VALUES (1, 'a', 10), (2, 'b', 20), (3, 'c', 30)");

            var pipeline = session.CreatePipeline();
            PipelineQuery<PipelineMySqlRow, MySqlProvider> list =
                pipeline.Query(session.From<PipelineMySqlRow>().Where($"`value` >= {20L}"));
            PipelineFirst<PipelineMySqlRow, MySqlProvider> first =
                pipeline.QueryFirst(session.From<PipelineMySqlRow>().Where($"`id` = {2L}"));
            PipelineScalar<MySqlProvider> count =
                pipeline.Count(session.From<PipelineMySqlRow>());
            await pipeline.ExecuteAsync();
            await Assert.That(list.Value.Select(static row => row.id).ToArray()).IsEquivalentTo([2L, 3L]);
            await Assert.That(first.Value?.id).IsEqualTo(2L);
            await Assert.That(count.Value).IsEqualTo(3L);
            pipeline.Dispose();

            await using (var transaction = await session.BeginTransactionAsync())
            {
                await session.InsertAsync(new PipelineMySqlRow { id = 4, name = "d", value = 40 });
                var inTransaction = session.CreatePipeline();
                PipelineScalar<MySqlProvider> inCount =
                    inTransaction.Count(session.From<PipelineMySqlRow>());
                await inTransaction.ExecuteAsync();
                await Assert.That(inCount.Value).IsEqualTo(4L);
                inTransaction.Dispose();
                await transaction.RollbackAsync();
            }

            var afterRollback = session.CreatePipeline();
            PipelineScalar<MySqlProvider> afterCount =
                afterRollback.Count(session.From<PipelineMySqlRow>());
            await afterRollback.ExecuteAsync();
            await Assert.That(afterCount.Value).IsEqualTo(3L);
            afterRollback.Dispose();
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pipeline_mysql_probe");
        }
    }
}
