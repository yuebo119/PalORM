using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>版本化批量更新的受影响数锁定（D3，2026-11-23）。
/// <para>PG 的版本化批量更新走合并后的数组内核（UNNEST + 版本令牌数组）。合并若把版本槽的
/// 偏移算错一格，症状是驱动报「参数未被使用」，而<b>受影响行数</b>才是业务可观测的契约：
/// 应恰等于输入实体数，且提交后内存 version 与库内 version 同步 +1。MySQL 侧已有 5 条
/// 版本化用例（<c>MySqlBatchedUpdatePathTests</c>），本用例补 PG 数组形态这一条。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class VersionedBulkUpdateKernelTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_VersionedBulkUpdate_ReturnsInputCountAndIncrementsVersion()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        await db.ExecuteAsync($"DELETE FROM cf_versioned");

        try
        {
            // 逐行显式调用（非循环）避免 PALORM005；三行足以覆盖"整批一次 UNNEST"路径
            CfVersionedEntity first = await InsertSeedAsync(db, "v0");
            CfVersionedEntity second = await InsertSeedAsync(db, "v1");
            CfVersionedEntity third = await InsertSeedAsync(db, "v2");
            var entities = new List<CfVersionedEntity> { first, second, third };

            foreach (CfVersionedEntity entity in entities)
                entity.Name = "updated";

            long affected = await db.BulkUpdateAsync(entities);

            await Assert.That(affected).IsEqualTo(3L)
                .Because("版本化批量更新按驱动口径返回受影响行数，应等于输入实体数");
            await Assert.That(entities.TrueForAll(static e => e.Version == 2)).IsTrue()
                .Because("提交成功后内存 version 统一回填 +1（ITM-556 语义）");

            List<CfVersionedEntity> loaded = await db.GetAllAsync<CfVersionedEntity>();
            await Assert.That(loaded.Count).IsEqualTo(3);
            await Assert.That(loaded.TrueForAll(static e => e.Version == 2 && e.Name == "updated")).IsTrue()
                .Because("库内 version 与 name 均须更新，且版本只增一次");
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM cf_versioned");
        }
    }

    private static async Task<CfVersionedEntity> InsertSeedAsync(
        DataSession<PostgreSqlProvider> db, string name)
    {
        var entity = new CfVersionedEntity { Name = name, Version = 1 };
        await db.InsertAsync(entity);
        return entity;
    }
}
