using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

public sealed class QueryBuilderValueSemanticsTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

    [Test]
    public async Task Where_OnBranch_DoesNotPolluteRoot()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> root =
            session.From<ValueSemanticsEntity>();
        QueryBuilder<ValueSemanticsEntity> branch = root;
        branch.Where($"price > {100m}");

        string rootSql = root.BuildSql();
        string branchSql = branch.BuildSql();

        await Assert.That(rootSql).DoesNotContain("price >");
        await Assert.That(branchSql).Contains("price >");
    }

    [Test]
    public async Task Set_OnBranch_DoesNotPolluteRoot()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> root =
            session.From<ValueSemanticsEntity>();
        QueryBuilder<ValueSemanticsEntity> branch = root;
        branch.Set(entity => entity.Price, 50m);

        // root 无 Set → BuildUpdateSql 应抛异常
        await Assert.That(() => root.BuildUpdateSql())
            .Throws<InvalidOperationException>();
        string branchSql = branch.BuildUpdateSql();

        await Assert.That(branchSql).Contains("price");
    }

    [Test]
    public async Task TwoBranches_FromSameRoot_AreIndependent()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> root =
            session.From<ValueSemanticsEntity>();
        QueryBuilder<ValueSemanticsEntity> a = root;
        a.Where($"name = {"Alice"}");
        QueryBuilder<ValueSemanticsEntity> b = root;
        b.Where($"price > {10m}");

        string aSql = a.BuildSql();
        string bSql = b.BuildSql();

        await Assert.That(aSql)
            .Contains("name = @")
            .And.DoesNotContain("price >");
        await Assert.That(bSql)
            .Contains("price >")
            .And.DoesNotContain("name = @");
    }

    [Test]
    public async Task Parameters_ClonedOnBranch_DoNotAffectRoot()
    {
        // 2026-11-03 修复：原实现在 `branch = root` 之后**没有任何变更**，只断言两侧各 1 个
        // 参数——"参数列表共享引用"与"写时复制"两种实现下都通过（T21 空转绿）。
        // 现在在 branch 上追加一个带参数的条件，再断言 root 的参数数不变。
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> root =
            session.From<ValueSemanticsEntity>();
        root.Where($"price > {10m}");
        QueryBuilder<ValueSemanticsEntity> branch = root;

        branch.Where($"name = {"probe"}");

        IReadOnlyList<DbParameter> rootParams = root.GetQueryParameters();
        IReadOnlyList<DbParameter> branchParams = branch.GetQueryParameters();

        // root 只有自己那一个参数；branch 两个。若参数列表被共享，root 会看到 2 个。
        await Assert.That(rootParams.Count).IsEqualTo(1);
        await Assert.That(branchParams.Count).IsEqualTo(2);
        // 值也逐位核对：root 的参数值不得被 branch 的追加覆盖
        await Assert.That(rootParams[0].Value).IsEqualTo(10m);
        await Assert.That(branchParams[0].Value).IsEqualTo(10m);
        await Assert.That(branchParams[1].Value).IsEqualTo("probe");
    }

    [Test]
    public async Task Where_AfterSharingReference_LeavesEarlierBuilderUnchanged()
    {
        // r19/T-P3-04：原名 ToPage_DoesNotMutateOriginalBuilder 未调用 ToPage——按实际
        // 行为（Where 写时复制）改名，避免名实不符
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> root =
            session.From<ValueSemanticsEntity>();
        QueryBuilder<ValueSemanticsEntity> before = root;
        root.Where($"name = {"test"}");

        string beforeSql = before.BuildSql();

        await Assert.That(beforeSql).DoesNotContain("name = @");
    }

    // ─── QUERY-001 场景 B：复制已写入条件的 builder ───

    [Test]
    public async Task Branch_OfAlreadyWrittenRoot_DoesNotPolluteRoot()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> root =
            session.From<ValueSemanticsEntity>();
        root.Where($"name = {"Alice"}");
        QueryBuilder<ValueSemanticsEntity> branch = root;
        branch.Where($"price > {100m}");

        string rootSql = root.BuildSql();
        string branchSql = branch.BuildSql();

        await Assert.That(rootSql)
            .Contains("name = @")
            .And.DoesNotContain("price >");
        await Assert.That(branchSql)
            .Contains("name = @")
            .And.Contains("price >");
    }

    [Test]
    public async Task TwoBranches_OfAlreadyWrittenRoot_AreIndependent()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> root =
            session.From<ValueSemanticsEntity>();
        root.Where($"name = {"Alice"}");
        QueryBuilder<ValueSemanticsEntity> a = root;
        a.Where($"price > {10m}");
        QueryBuilder<ValueSemanticsEntity> b = root;
        b.Where($"price < {5m}");

        string aSql = a.BuildSql();
        string bSql = b.BuildSql();

        await Assert.That(aSql)
            .Contains("price >")
            .And.DoesNotContain("price <");
        await Assert.That(bSql)
            .Contains("price <")
            .And.DoesNotContain("price >");
    }

    // ─── QUERY-001 场景 C：[SoftDelete] 实体 From<T>() 出生即含默认过滤器 ───

    [Test]
    public async Task SoftDeleteEntity_BranchAfterFrom_DoesNotPolluteRoot()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<SoftDeleteValueSemanticsEntity> root =
            session.From<SoftDeleteValueSemanticsEntity>();
        QueryBuilder<SoftDeleteValueSemanticsEntity> branch = root;
        branch.Where($"name = {"Alice"}");

        string rootSql = root.BuildSql();
        string branchSql = branch.BuildSql();

        await Assert.That(rootSql)
            .Contains("deleted_at")
            .And.DoesNotContain("name = @");
        await Assert.That(branchSql)
            .Contains("deleted_at")
            .And.Contains("name = @");
    }

    [Test]
    public async Task SoftDeleteEntity_TwoBranches_AreIndependent()
    {
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<SoftDeleteValueSemanticsEntity> root =
            session.From<SoftDeleteValueSemanticsEntity>();
        QueryBuilder<SoftDeleteValueSemanticsEntity> a = root;
        a.Where($"name = {"Alice"}");
        QueryBuilder<SoftDeleteValueSemanticsEntity> b = root;
        b.Where($"name = {"Bob"}");

        IReadOnlyList<DbParameter> aParams = a.GetQueryParameters();
        IReadOnlyList<DbParameter> bParams = b.GetQueryParameters();

        // 各分支 = 默认过滤器（无参数）+ 自己的 1 个条件参数
        await Assert.That(aParams.Count).IsEqualTo(1);
        await Assert.That(bParams.Count).IsEqualTo(1);
        await Assert.That(aParams[0].Value).IsEqualTo("Alice");
        await Assert.That(bParams[0].Value).IsEqualTo("Bob");
    }

    [Test]
    public async Task BuildCountSql_IncludesRawClause_AlignedWithBuildSql()
    {
        // ITM-609：Raw 子句必须进 COUNT——否则 .Raw("AND ...") 后 ToPageAsync 的
        // 页查询生效而 Total 虚高（COUNT 与 SELECT 过滤语义分叉）。
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> builder =
            session.From<ValueSemanticsEntity>().Where($"name = {"Bob"}").Raw("AND price > 10");

        string selectSql = builder.BuildSql();
        string countSql = builder.BuildCountSql();

        await Assert.That(selectSql).Contains("price > 10");
        await Assert.That(countSql).Contains("price > 10");
    }

    [Test]
    public async Task BuildCountSql_WithSetClause_ThrowsAtBuildTime()
    {
        // ITM-642(r4)/662 锁定：Set 守卫构建期拒绝——COUNT 不再先真实执行后页构建才抛
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> builder =
            session.From<ValueSemanticsEntity>().Set(e => e.Name, "x");

        await Assert.That(() => builder.BuildCountSql()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CloneForExecution_PreservesIsolationLevel()
    {
        // r6-N1 锁定：克隆透传隔离级别——r5-S2 曾因 Services 7 参构造丢失致条件分支死代码
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> builder = session.From<ValueSemanticsEntity>();
        QueryBuilder<ValueSemanticsEntity> clone = builder.CloneForExecution();

        // T-P3-01 白盒豁免（T2）：_isolationLevel 是纯内部透传字段，SQLite 内存库
        // 单隔离模型无法观测事务隔离级别差异；消费点（ToPageAsync 自开事务）只能经
        // 字段断言锁定 r5-S2 修复（Services 构造丢失致条件分支死代码）。
        await Assert.That(clone._isolationLevel).IsEqualTo(builder._isolationLevel);
    }

    [Test]
    public async Task BuildUpdateSql_WithTakeOrSkip_ThrowsAtBuildTime()
    {
        // r8-D1(P1) 锁定：Take/Skip 是字段非子句位掩码——守卫族曾结构性看不见，
        // 静默丢 LIMIT 扩大更新范围（Set+Take 组合应构建期拒绝）
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> limited =
            session.From<ValueSemanticsEntity>().Set(e => e.Name, "x").Take(5);
        await Assert.That(() => limited.BuildUpdateSql()).Throws<NotSupportedException>();

        QueryBuilder<ValueSemanticsEntity> skipped =
            session.From<ValueSemanticsEntity>().Set(e => e.Name, "x").Skip(5);
        await Assert.That(() => skipped.BuildUpdateSql()).Throws<NotSupportedException>();
    }
}

#region Test Entities
[Table("value_semantics")]
internal sealed partial class ValueSemanticsEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = string.Empty;
    [Column("price")]
    public decimal Price { get; set; }
}

[Table("value_semantics_sd")]
[SoftDelete]
internal sealed partial class SoftDeleteValueSemanticsEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = string.Empty;
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
#endregion
