using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>SessionBatch 参数工厂走 Provider 的锁定（2026-11-23，语义分叉收口）。
/// <para><b>背景</b>：<see cref="SessionBatch{TProvider}.Append(FormattableString)"/> 的实参原由
/// scratch 命令的驱动工厂（<c>_scratch.CreateParameter()</c>）物化，绕过
/// <see cref="IDbProvider.CreateParameter"/> 的方言归一：PostgreSQL 的 <c>DateTime</c> → UTC 归一
/// （ADR-X DEC-4）与 <c>timestamptz</c> 类型声明都在那里。Npgsql 对 <c>timestamptz</c> 拒绝
/// <c>Kind=Local</c>，而 <c>DbBatch</c> 又在客户端做严格类型解析，因此修复前本用例在批量写入处
/// 抛 <c>InvalidCastException</c>。</para>
/// <para><b>与单条路径的对照不在此断言</b>：单条 UPDATE 的参数池在<b>建池</b>时值未知（DBNull），
/// 归一同样不生效，属另一个既有缺口（见本轮报告的「池 binder 归一缺口」），与本用例锁定的
/// 工厂一致性无关，故不混入同一条断言。</para>
/// <para><b>断言口径</b>：比对时刻 Ticks 与 Kind（PG 一侧恒 Utc），这才是用户可观测的数据。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class SessionBatchParameterFactoryTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_SessionBatch_DateTimeNormalizesToUtc()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        // 诊断锚：确认参数工厂确实产出 timestamptz 类型（否则 DbBatch 的客户端类型解析会拒绝绑定）
        var probe = (Npgsql.NpgsqlParameter)PostgreSqlProvider.CreateParameter(
            "@p", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await Assert.That(probe.NpgsqlDbType.ToString()).IsEqualTo("TimestampTz");
        await db.MigrateAsync();
        // 旧库的 cf_time_matrix 可能是 timestamp 列（CREATE TABLE IF NOT EXISTS 不改列型）
        await db.ExecuteAsync($"DROP TABLE IF EXISTS cf_time_matrix");
        await db.MigrateAsync();

        try
        {
            DateTime seedUtc = new(2026, 7, 18, 2, 30, 0, DateTimeKind.Utc);
            DateTime localInput = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Local);

            CfTimeMatrixEntity batchTarget = await InsertSeedAsync(db, seedUtc);

            // 批量路径：Kind=Local 经 CreateBatch().Append 写入
            using (SessionBatch<PostgreSqlProvider> batch = db.CreateBatch())
            {
                // 列名按实体 [Column]/属性名引用（PG 对未加引号标识符折叠小写，
                // 主键列是 "Id" 而非 id，裸写会报 42703 column "id" does not exist）
                batch.Append($"UPDATE cf_time_matrix SET dt = {localInput} WHERE \"Id\" = {batchTarget.Id}");
                int affected = await batch.ExecuteNonQueryAsync();
                await Assert.That(affected).IsEqualTo(1);
            }

            CfTimeMatrixEntity batchRead = (await db.GetAsync<CfTimeMatrixEntity>(batchTarget.Id))!;
            await Assert.That(batchRead.Dt.Ticks).IsEqualTo(localInput.ToUniversalTime().Ticks)
                .Because("批量语句的实参应经 TProvider.CreateParameter 归一为 UTC 时刻（修复前该值不归一，"
                    + "Npgsql 对 timestamptz 拒绝 Kind=Local 并抛 InvalidCastException）");
            await Assert.That(batchRead.Dt.Kind).IsEqualTo(DateTimeKind.Utc);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM cf_time_matrix");
        }
    }

    private static async Task<CfTimeMatrixEntity> InsertSeedAsync(
        DataSession<PostgreSqlProvider> db, DateTime utc)
    {
        var row = new CfTimeMatrixEntity
        {
            Dt = utc,
            Dto = new DateTimeOffset(utc),
            DateOnly = new DateOnly(2026, 7, 18),
            TimeOnly = new TimeOnly(10, 30, 0)
        };
        await db.InsertAsync(row);
        return row;
    }
}
