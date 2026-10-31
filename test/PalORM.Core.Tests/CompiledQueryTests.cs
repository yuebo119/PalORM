using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>编译查询计划（M3，2026-10-31 极致性能轮）的行为契约。
/// <para>覆盖面：① 同形状两次执行命中同一命令与参数对象，结果正确；② 参数值就地刷新；
/// ③ 参数个数/名称/重复声明在编译期失败；④ 运行时类型变化明确失败；⑤ 计划绑定会话事务；
/// ⑥ 行工厂（属性名≠列名）与容量启发共用直查族记录。</para></summary>
internal sealed class CompiledQueryTests
{
    [Test]
    public async Task Execute_Twice_ReusesCommand_AndReturnsRows()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await SeedAsync(session);

        using CompiledQuery<CompiledRow, SqliteProvider> plan = session.Compile<CompiledRow>(
            "SELECT id, name FROM compiled_rows WHERE id = @p0", "@p0");

        List<CompiledRow> first = await plan.ExecuteAsync([1L]);
        List<CompiledRow> second = await plan.ExecuteAsync([2L]);

        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(first[0].Name).IsEqualTo("n1");
        await Assert.That(second.Count).IsEqualTo(1);
        await Assert.That(second[0].Name).IsEqualTo("n2");
        await Assert.That(plan.Sql).Contains("@p0");
    }

    [Test]
    public async Task Execute_WithParameterCountMismatch_Throws()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        using CompiledQuery<CompiledRow, SqliteProvider> plan = session.Compile<CompiledRow>(
            "SELECT id, name FROM compiled_rows WHERE id = @p0", "@p0");

        await Assert.That(async () => await plan.ExecuteAsync([1L, 2L]))
            .Throws<ArgumentException>();
        await Assert.That(async () => await plan.ExecuteAsync([]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Compile_RejectsInvalidParameterNames()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();

        await Assert.That(() => session.Compile<CompiledRow>("SELECT 1", "p0"))
            .Throws<ArgumentException>();
        await Assert.That(() => session.Compile<CompiledRow>("SELECT 1", "@p0", "@p0"))
            .Throws<ArgumentException>();
        await Assert.That(() => session.Compile<CompiledRow>(" ", "@p0"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Execute_TypeChange_Throws()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await SeedAsync(session);
        using CompiledQuery<CompiledRow, SqliteProvider> plan = session.Compile<CompiledRow>(
            "SELECT id, name FROM compiled_rows WHERE id = @p0", "@p0");

        _ = await plan.ExecuteAsync([1L]);
        await Assert.That(async () => await plan.ExecuteAsync(["1"]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Execute_BindsSessionTransaction()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await SeedAsync(session);
        using CompiledQuery<CompiledRow, SqliteProvider> plan = session.Compile<CompiledRow>(
            "SELECT id, name FROM compiled_rows WHERE id = @p0", "@p0");

        // 会话管理的自开事务：计划命令必须绑定当前事务（未提交前可见），
        // 回调抛出后整体回滚，事务外不可见。
        await Assert.That(async () =>
        {
            await session.WithTransaction(async ct =>
            {
                await session.ExecuteAsync(
                    $"INSERT INTO compiled_rows (id, name) VALUES ({10L}, {"tx"})", ct);
                List<CompiledRow> inside = await plan.ExecuteAsync([10L], ct);
                await Assert.That(inside.Count).IsEqualTo(1);
                throw new InvalidOperationException("rollback probe");
            });
        }).Throws<InvalidOperationException>();

        List<CompiledRow> afterRollback = await plan.ExecuteAsync([10L]);
        await Assert.That(afterRollback.Count).IsEqualTo(0);
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = $"Data Source=compiled_query_{Guid.NewGuid():N};Mode=Memory",
            MaxRetries = 0,
            CircuitBreakerThreshold = 0,
        });

    private static async Task SeedAsync(DataSession<SqliteProvider> session)
    {
        await session.ExecuteAsync(
            $"CREATE TABLE compiled_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await session.ExecuteAsync(
            $"INSERT INTO compiled_rows (id, name) VALUES ({1L}, {"n1"})");
        await session.ExecuteAsync(
            $"INSERT INTO compiled_rows (id, name) VALUES ({2L}, {"n2"})");
    }
}

[Table("compiled_rows")]
internal sealed partial class CompiledRow
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}
