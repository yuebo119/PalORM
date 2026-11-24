using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>参数池路径的 DateTime 归一一（DEC-4 兜底面，2026-11-23）。
/// <para><b>背景</b>：<c>TProvider.CreateParameter</c> 的 DateTime→UTC 归一只在「创建参数时值已知」
/// 才生效。写入路径有两类绕开它：①生成 binder 用 <c>cmd.CreateParameter()</c> 建参后再写值
/// （单条 Insert/Update/Delete）；②参数池先建对象（值是 DBNull），值由 binder 逐行填（批量
/// UPDATE/UPSERT）。因此同一会话里 <c>InsertAsync</c>（值在创建期可知）能写 <c>Kind=Local</c>，
/// 而 <c>UpdateAsync</c> 与 <c>BulkUpdateAsync</c> 会抛 <c>InvalidCastException</c>——
/// 同一数据经不同 API 得到不同结果。</para>
/// <para>修法：<c>IDbProvider.NormalizeCommandParameters</c> 挂到写执行叶子，执行前对已绑定参数
/// 补一次归一（PG 覆盖为 DateTime 与 DateTime[]；幂等）。本用例锁定两条此前失败的路径。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class PooledDateTimeNormalizationTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_UpdateAsync_NormalizesLocalDateTimeToUtc()
    {
        await using DataSession<PostgreSqlProvider> db = await ResetAsync();

        CfTimeMatrixEntity row = await InsertSeedAsync(db, new DateTime(2026, 7, 18, 2, 30, 0, DateTimeKind.Utc));
        DateTime localInput = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Local);

        row.Dt = localInput;
        await db.UpdateAsync(row);

        CfTimeMatrixEntity read = (await db.GetAsync<CfTimeMatrixEntity>(row.Id))!;
        await Assert.That(read.Dt.Ticks).IsEqualTo(localInput.ToUniversalTime().Ticks)
            .Because("单条 UPDATE 的参数由生成 binder 建（驱动工厂），执行叶子须补归一；"
                + "修复前此处抛 InvalidCastException（timestamptz 拒绝 Kind=Local）");
        await Assert.That(read.Dt.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkUpdate_NormalizesLocalDateTimeToUtc()
    {
        await using DataSession<PostgreSqlProvider> db = await ResetAsync();

        DateTime seedUtc = new(2026, 7, 18, 2, 30, 0, DateTimeKind.Utc);
        CfTimeMatrixEntity first = await InsertSeedAsync(db, seedUtc);
        CfTimeMatrixEntity second = await InsertSeedAsync(db, seedUtc);
        DateTime localInput = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Local);

        first.Dt = localInput;
        second.Dt = localInput;
        long affected = await db.BulkUpdateAsync([first, second]);

        await Assert.That(affected).IsEqualTo(2L);
        long expectedTicks = localInput.ToUniversalTime().Ticks;
        CfTimeMatrixEntity firstRead = (await db.GetAsync<CfTimeMatrixEntity>(first.Id))!;
        CfTimeMatrixEntity secondRead = (await db.GetAsync<CfTimeMatrixEntity>(second.Id))!;
        await Assert.That(firstRead.Dt.Ticks).IsEqualTo(expectedTicks)
            .Because("批量参数池建池时值是 DBNull，创建期归一生效不了，须在执行叶子补齐");
        await Assert.That(secondRead.Dt.Ticks).IsEqualTo(expectedTicks);
    }

    /// <summary>2026-11-24 补救锁定：UNNEST 数组路径的 DateTime 元素类型必须是 TimestampTz，
    /// 且可空 DateTime?[] 必须归一。修复前真库实测两种失败形态：Kind=Utc 被驱动拒绝
    ///（"Cannot write DateTime with Kind=UTC to timestamp without time zone"），
    /// Kind=Unspecified 静默偏移会话时区（8 小时）。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkUpdate_UnnestArray_NormalizesDateTimeElements()
    {
        // 显式设置非 UTC 会话时区：测试库连接默认 Etc/UTC，Unspecified 按 UTC 解释恰好不偏移，
        // 缺陷会被掩盖。Asia/Shanghai 下修复前会偏移 8 小时或被驱动拒绝。
        await using DataSession<PostgreSqlProvider> db = await CreateShanghaiSessionAsync();

        DateTime localInput = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Local);
        CfTimeMatrixEntity first = await InsertSeedAsync(db, new DateTime(2026, 7, 18, 2, 30, 0, DateTimeKind.Utc));
        CfTimeMatrixEntity second = await InsertSeedAsync(db, new DateTime(2026, 7, 18, 2, 30, 0, DateTimeKind.Utc));

        first.Dt = localInput;
        second.Dt = localInput;
        long affected = await db.BulkUpdateAsync([first, second]);

        await Assert.That(affected).IsEqualTo(2L);
        long expectedTicks = localInput.ToUniversalTime().Ticks;
        CfTimeMatrixEntity firstRead = (await db.GetAsync<CfTimeMatrixEntity>(first.Id))!;
        CfTimeMatrixEntity secondRead = (await db.GetAsync<CfTimeMatrixEntity>(second.Id))!;
        await Assert.That(firstRead.Dt.Ticks).IsEqualTo(expectedTicks)
            .Because("UNNEST 数组元素必须是 TimestampTz 且经归一；修复前这里偏移或抛异常");
        await Assert.That(secondRead.Dt.Ticks).IsEqualTo(expectedTicks);
    }

    /// <summary>非 UTC 会话时区的测试会话：显式 Sql 设置，确保修复前的时区偏移缺陷可见。</summary>
    private static async Task<DataSession<PostgreSqlProvider>> CreateShanghaiSessionAsync()
    {
        var options = new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString(),
            SessionSetupSql = "SET TIME ZONE 'Asia/Shanghai'"
        };
        return await DataSession<PostgreSqlProvider>.CreateAsync(options);
    }

    private static async Task<DataSession<PostgreSqlProvider>> ResetAsync()
    {
        DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        // 旧库的 cf_time_matrix 可能是 timestamp 列（CREATE TABLE IF NOT EXISTS 不改列型）
        await db.ExecuteAsync($"DROP TABLE IF EXISTS cf_time_matrix");
        await db.MigrateAsync();
        return db;
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
