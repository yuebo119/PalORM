using System.Runtime.CompilerServices;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>版本化批量更新的真实业务形状验证（ADR-P 调用方重试方案）。
/// <para><b>形状</b>：订单行（order_no/customer/qty/amount/note/updated_at_unix + version），
/// 6 个 SET 列含可空字符串，覆盖 PG 数组填充与 MySQL 参数池的宽行布局；1000 与 5000 行
/// 两档覆盖 MySQL 2000 行/批的批边界。</para>
/// <para><b>场景</b>：① 外部写入者修改 3 行并抬版本；② 调用方用陈旧版本批量更新 →
/// ConcurrencyConflictException，验证整批回滚与实体版本不变；③ 调用方按 ADR-P 的诊断
/// 查询（GetAll 快照）识别冲突键、合并业务字段、回填当前版本后整批重试成功；
/// ④ 重复主键输入按冲突抛出，调用方按主键去重后重试成功。</para>
/// <para><b>不覆盖</b>：租户/软删组合（SQL 构造器单测与既有 BulkUpdateTenantRoutingTests /
/// BulkUpdateSoftDeleteRoutingTests 覆盖）；本组聚焦冲突映射与重试策略。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class VersionedBulkUpdateBusinessShapeTests
{
    private static readonly long[] ConflictIds = [1, 2, 3];

    [Test]
    [Arguments(1000)]
    [Arguments(5000)]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BusinessShape_ConflictRetryAndDuplicate(int rows)
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await RunScenarioAsync(db, rows);
    }

    [Test]
    [Arguments(1000)]
    [Arguments(5000)]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BusinessShape_ConflictRetryAndDuplicate(int rows)
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        await RunScenarioAsync(db, rows);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "端到端业务场景按顺序展开：播种、并发写入、首次冲突、诊断合并、重试、"
            + "重复键冲突、去重重试；拆分需跨方法传递整批实体与断言状态。")]
    private static async Task RunScenarioAsync<TProvider>(DataSession<TProvider> db, int rows)
        where TProvider : IDbProvider
    {
        string table = TProvider.QuoteIdentifier("palorm_biz");
        string qId = TProvider.QuoteIdentifier("id");
        string qCustomer = TProvider.QuoteIdentifier("customer");
        string qVersion = TProvider.QuoteIdentifier("version");
        try
        {
            await db.ExecuteAsync(FormattableStringFactory.Create($"DROP TABLE IF EXISTS {table}"));
            await db.ExecuteAsync(FormattableStringFactory.Create(
                $"CREATE TABLE {table} (id BIGINT PRIMARY KEY, order_no VARCHAR(64) NOT NULL, "
                + "customer VARCHAR(128) NOT NULL, qty INT NOT NULL, amount DECIMAL(18,2) NOT NULL, "
                + "note VARCHAR(255) NULL, updated_at_unix BIGINT NOT NULL, version INT NOT NULL)"));

            var seed = new List<BusinessOrder>(rows);
            for (int i = 1; i <= rows; i++)
            {
                seed.Add(new BusinessOrder
                {
                    Id = i,
                    OrderNo = $"ORD-{i:D5}",
                    Customer = "seed",
                    Qty = i % 10,
                    Amount = (i % 100) / 2m,
                    Note = i % 7 == 0 ? null : $"note-{i}",
                    UpdatedAtUnix = 1_700_000_000 + i,
                    Version = 0
                });
            }
            await db.BulkInsertAsync(seed);

            var caller = seed.Select(static e => new BusinessOrder
            {
                Id = e.Id,
                OrderNo = e.OrderNo,
                Customer = "caller",
                Qty = e.Qty + 1,
                Amount = e.Amount + 0.5m,
                Note = e.Note,
                UpdatedAtUnix = e.UpdatedAtUnix + 10,
                Version = e.Version
            }).ToList();

            // 外部写入者：3 行业务值变更 + version+1（模拟并发提交）
            await db.ExecuteAsync(FormattableStringFactory.Create(
                $"UPDATE {table} SET {qCustomer} = 'concurrent', {qVersion} = {qVersion} + 1 "
                + $"WHERE {qId} IN (1, 2, 3)"));

            ConcurrencyConflictException? conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                async () => await db.BulkUpdateAsync(caller));
            await Assert.That(conflict).IsNotNull();
            await Assert.That(conflict!.Message).Contains("was modified by another transaction");
            await Assert.That(conflict.InnerException).IsNull();
            // 失败批未回填实体版本
            await Assert.That(caller.Count(static e => e.Version == 0)).IsEqualTo(rows);

            List<BusinessOrder> afterFail = await db.GetAllAsync<BusinessOrder>();
            await Assert.That(afterFail.Count).IsEqualTo(rows);
            await Assert.That(afterFail.Count(r => ConflictIds.Contains(r.Id)
                && r.Customer == "concurrent" && r.Version == 1)).IsEqualTo(3);
            await Assert.That(afterFail.Count(r => !ConflictIds.Contains(r.Id)
                && r.Customer == "seed" && r.Version == 0)).IsEqualTo(rows - 3);

            // 调用方诊断与重试（ADR-P）：读取当前快照，版本不一致的键合并业务字段并回填版本
            var currentById = afterFail.ToDictionary(static r => r.Id);
            foreach (BusinessOrder entity in caller)
            {
                BusinessOrder current = currentById[entity.Id];
                if (current.Version == entity.Version) continue;
                entity.Version = current.Version;
                if (current.Customer == "concurrent")
                    entity.Customer = current.Customer + "+merged";
            }

            long retried = await db.BulkUpdateAsync(caller);
            await Assert.That(retried).IsEqualTo(rows);

            List<BusinessOrder> afterRetry = await db.GetAllAsync<BusinessOrder>();
            await Assert.That(afterRetry.Count(r => ConflictIds.Contains(r.Id)
                && r.Customer == "concurrent+merged" && r.Version == 2)).IsEqualTo(3);
            await Assert.That(afterRetry.Count(r => !ConflictIds.Contains(r.Id)
                && r.Customer == "caller" && r.Version == 1)).IsEqualTo(rows - 3);
            await Assert.That(afterRetry.Count(r => r.Qty != (int)(r.Id % 10) + 1)).IsEqualTo(0);
            await Assert.That(afterRetry.Count(r => r.Amount != (r.Id % 100) / 2m + 0.5m)).IsEqualTo(0);
            await Assert.That(caller.Count(e => e.Version == (ConflictIds.Contains(e.Id) ? 2 : 1)))
                .IsEqualTo(rows);

            // 重复主键：同一 id 出现两次 → 目标行只更新一次，受影响数 < 输入数 → 冲突
            List<BusinessOrder> duplicate = afterRetry.Select(static r => new BusinessOrder
            {
                Id = r.Id,
                OrderNo = r.OrderNo,
                Customer = "dup",
                Qty = r.Qty,
                Amount = r.Amount,
                Note = r.Note,
                UpdatedAtUnix = r.UpdatedAtUnix,
                Version = r.Version
            }).ToList();
            BusinessOrder duplicateOfFirst = new()
            {
                Id = duplicate[0].Id,
                OrderNo = duplicate[0].OrderNo,
                Customer = duplicate[0].Customer,
                Qty = duplicate[0].Qty,
                Amount = duplicate[0].Amount,
                Note = duplicate[0].Note,
                UpdatedAtUnix = duplicate[0].UpdatedAtUnix,
                Version = duplicate[0].Version
            };
            duplicate.Add(duplicateOfFirst);

            await Assert.ThrowsAsync<ConcurrencyConflictException>(
                async () => await db.BulkUpdateAsync(duplicate));

            List<BusinessOrder> afterDupFail = await db.GetAllAsync<BusinessOrder>();
            var retryVersions = afterRetry.ToDictionary(static r => r.Id, static r => r.Version);
            await Assert.That(afterDupFail.Count(r => r.Customer == "dup")).IsEqualTo(0);
            await Assert.That(afterDupFail.Count(r => r.Version != retryVersions[r.Id])).IsEqualTo(0);

            // 调用方按主键去重后重试
            List<BusinessOrder> deduped = duplicate
                .GroupBy(static r => r.Id)
                .Select(static group => group.First())
                .ToList();
            long dedupRetried = await db.BulkUpdateAsync(deduped);
            await Assert.That(dedupRetried).IsEqualTo(rows);

            List<BusinessOrder> afterDupRetry = await db.GetAllAsync<BusinessOrder>();
            await Assert.That(afterDupRetry.Count(r => r.Customer == "dup")).IsEqualTo(rows);
            await Assert.That(afterDupRetry.Count(r =>
                r.Version == (ConflictIds.Contains(r.Id) ? 3 : 2))).IsEqualTo(rows);
        }
        finally
        {
            await db.ExecuteAsync(FormattableStringFactory.Create($"DROP TABLE IF EXISTS {table}"));
        }
    }
}

[Table("palorm_biz")]
internal sealed partial class BusinessOrder
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("order_no")]
    public string OrderNo { get; set; } = "";

    [Column("customer")]
    public string Customer { get; set; } = "";

    [Column("qty")]
    public int Qty { get; set; }

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("updated_at_unix")]
    public long UpdatedAtUnix { get; set; }

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}
