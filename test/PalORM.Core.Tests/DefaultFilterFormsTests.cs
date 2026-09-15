using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>默认过滤三形态的行为契约。
/// <para><b>为什么需要</b>：v5.6 把「默认过滤条件」缓存升级为 <c>DefaultFilterForms</c>
/// （Condition / AndFragment / WhereClause 三形态一次产出），分别服务 CountAsync 与聚合
/// （裸条件）、GetAsync（追加片段）、GetAllAsync（独立 WHERE 子句）。三者由同一处派生，
/// 任一处拼接错误都会让过滤静默失效——过滤失效在结果集上表现为"多出几行"，不抛异常。
/// 故用可观察的软删 + 租户组合把三条路径都钉住。</para>
/// <para>每用例独立内存库名：TUnit 默认并行执行，共用库会因各用例的 DELETE/INSERT 互相污染。</para></summary>
public sealed class DefaultFilterFormsTests
{
    [Test]
    public async Task CountAsync_AppliesBareConditionForm()
    {
        const string tag = "count";
        await using SqliteConnection keeper = await OpenKeeperAsync(tag);
        await using var session = await CreateSessionAsync(tag);
        session.WithTenant(1L);

        // 裸条件形态：软删 + 租户同时生效（5 行中仅 2 行属本租户且未软删）
        await Assert.That(await session.CountAsync<FilteredEntity>()).IsEqualTo(2L);

        session.IgnoreFilters();
        await Assert.That(await session.CountAsync<FilteredEntity>()).IsEqualTo(5L);
        _ = keeper;
    }

    [Test]
    public async Task GetAllAsync_AppliesWhereClauseForm()
    {
        const string tag = "getall";
        await using SqliteConnection keeper = await OpenKeeperAsync(tag);
        await using var session = await CreateSessionAsync(tag);
        session.WithTenant(1L);

        List<FilteredEntity> visible = await session.GetAllAsync<FilteredEntity>();
        await Assert.That(visible.Count).IsEqualTo(2);
        await Assert.That(visible.All(static e => e.DeletedAt is null && e.TenantId == 1L)).IsTrue();

        session.IgnoreFilters();
        await Assert.That((await session.GetAllAsync<FilteredEntity>()).Count).IsEqualTo(5);
        _ = keeper;
    }

    [Test]
    public async Task GetAsync_AppliesAndFragmentForm()
    {
        const string tag = "get";
        await using SqliteConnection keeper = await OpenKeeperAsync(tag);
        await using var session = await CreateSessionAsync(tag);
        session.WithTenant(1L);

        // 主键命中但已软删 → 追加片段形态必须把它排除
        await Assert.That(await session.GetAsync<FilteredEntity>(2L)).IsNull();
        // 主键命中但属他租户 → 同样排除
        await Assert.That(await session.GetAsync<FilteredEntity>(3L)).IsNull();
        // 命中且在范围内
        FilteredEntity? live = await session.GetAsync<FilteredEntity>(1L);
        await Assert.That(live).IsNotNull();
        await Assert.That(live!.Value).IsEqualTo(10L);

        session.IgnoreFilters();
        await Assert.That(await session.GetAsync<FilteredEntity>(2L)).IsNotNull();
        _ = keeper;
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync(string tag)
        => await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = ConnectionString(tag),
            MaxRetries = 0,
            CircuitBreakerThreshold = 0,
        });

    private static string ConnectionString(string tag)
        => $"Data Source=default_filter_forms_{tag};Mode=Memory;Cache=Shared";

    private static async Task<SqliteConnection> OpenKeeperAsync(string tag)
    {
        var keeper = new SqliteConnection(ConnectionString(tag));
        await keeper.OpenAsync();
        await using SqliteCommand cmd = keeper.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE filtered_entities " +
            "(id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, value INTEGER NOT NULL, deleted_at TEXT NULL);" +
            "INSERT INTO filtered_entities (id, tenant_id, value, deleted_at) VALUES " +
            "(1, 1, 10, NULL)," +            // 本租户、未删 → 可见
            "(2, 1, 20, '2026-01-01')," +    // 本租户、已软删 → 不可见
            "(3, 2, 30, NULL)," +            // 他租户、未删 → 不可见
            "(4, 1, 40, NULL)," +            // 本租户、未删 → 可见
            "(5, 2, 50, NULL)";              // 他租户、未删 → 不可见
        await cmd.ExecuteNonQueryAsync();
        return keeper;
    }
}

[SoftDelete]
[TenantAware]
[Table("filtered_entities")]
internal sealed partial class FilteredEntity
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("value")] public long Value { get; set; }
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}
