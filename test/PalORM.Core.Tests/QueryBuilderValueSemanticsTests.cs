using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

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
        await using DataSession<SqliteProvider> session =
            await CreateSessionAsync();

        QueryBuilder<ValueSemanticsEntity> root =
            session.From<ValueSemanticsEntity>();
        root.Where($"price > {10m}");
        QueryBuilder<ValueSemanticsEntity> branch = root;

        IReadOnlyList<DbParameter> rootParams = root.GetQueryParameters();
        IReadOnlyList<DbParameter> branchParams = branch.GetQueryParameters();

        await Assert.That(rootParams.Count).IsEqualTo(1);
        await Assert.That(branchParams.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ToPage_DoesNotMutateOriginalBuilder()
    {
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
}
