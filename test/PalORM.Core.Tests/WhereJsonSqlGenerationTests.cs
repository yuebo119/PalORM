using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>r21 批次C：WhereJson 的 DryRun 测试迁移。
/// <para>背景（ITM-770）：WhereJson 增加方言守卫（非 PG 方言构建期拒绝）后，原先借 SQLite
/// 会话验证 PG SQL 文本的测试模式失效。QueryBuilderContext 为 internal（Core.Tests 经
/// InternalsVisibleTo 可见）——此处构造**PG 方言** builder 做 DryRun（不触库，连接仅为
/// 构造所需），验证的语义与原测试一致。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "DryRun 从不触库——占位连接移交 QueryBuilder 构造后无执行路径，泄漏面不存在。")]
public sealed class WhereJsonSqlGenerationTests
{
    /// <summary>构造 PG 方言的 QueryBuilder——DryRun 不触库，连接用 SQLite 实例占位。
    /// 组装口径照抄 DataSession.From&lt;T&gt;（真源）；仅 Dialect/参数工厂/引号器换 PG。</summary>
    private static QueryBuilder<T> PgBuilder<T>() where T : class, new()
    {
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._rowFactories.TryGetValue(typeof(T), out var factory)
            || !state._tableNames.TryGetValue(typeof(T), out var tableName)
            || !state._columnNames.TryGetValue(typeof(T), out var columnNames))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered.");
        // DryRun 不触库：连接仅为构造所需的占位（所有权移交 builder，类级 CA2000 豁免）
        DbConnection placeholder = new SqliteConnection("Data Source=:memory:");
        return new QueryBuilder<T>(new QueryBuilderContext<T>(
            placeholder,
            new QueryBuilderServices<T>(
                SqlDialect.PostgreSql,
                (Func<System.Data.Common.DbDataReader, T>)factory,
                [],
                PostgreSqlProvider.CreateParameter,
                PostgreSqlProvider.QuoteIdentifier,
                new SessionOperationState(),
                new ResilienceExecutor(new DbOptions
                {
                    ConnectionString = "dryrun",
                    MaxRetries = 0,
                    CircuitBreakerThreshold = 0
                }),
                TimeSpan.FromSeconds(30)),
            tableName, columnNames));
    }



    [Test]
    public async Task GeneratesQuotedColumnWithBoundPathAndValue()
    {
        var dry = PgBuilder<WjProduct>()
            .WhereJson("payload", "name", "Alice")
            .AsDryRun();

        await Assert.That(dry.Sql).Contains("\"payload\"->>@p0 = @p1");
        await Assert.That(dry.Parameters.Count).IsEqualTo(2);
        await Assert.That(dry.Parameters[0].Value).IsEqualTo("name");
        await Assert.That(dry.Parameters[1].Value).IsEqualTo("Alice");
    }

    [Test]
    public async Task QuotesColumnIdentifier_DoubleQuoteEscaped()
    {
        var dry = PgBuilder<WjProduct>()
            .WhereJson("payload\"x", "k", 1)
            .AsDryRun();

        await Assert.That(dry.Sql).Contains("\"payload\"\"x\"->>@p0");
    }

    [Test]
    public async Task NonStringValue_NormalizedToInvariantString()
    {
        var dry = PgBuilder<WjProduct>()
            .WhereJson("payload", "count", 42)
            .AsDryRun();

        await Assert.That(dry.Parameters[1].Value).IsEqualTo("42");
    }

    [Test]
    public async Task BoolValue_NormalizedToLowercase()
    {
        // ITM-610：jsonb ->> 提取的布尔 text 恒为小写
        var dry = PgBuilder<WjProduct>()
            .WhereJson("payload", "active", true)
            .AsDryRun();

        await Assert.That(dry.Parameters[1].Value).IsEqualTo("true");
    }

    [Test]
    public async Task EnumValue_ThrowsExplicitly()
    {
        // ITM-771(r21)：enum/char/TimeSpan/byte[] 归一缺口——显式拒绝
        await Assert.That(() => PgBuilder<WjProduct>()
            .WhereJson("payload", "status", System.DayOfWeek.Monday))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task CharValue_ThrowsExplicitly()
        => await Assert.That(() => PgBuilder<WjProduct>()
            .WhereJson("payload", "initial", 'x'))
            .Throws<NotSupportedException>();

    [Test]
    public async Task TimeSpanValue_ThrowsExplicitly()
        => await Assert.That(() => PgBuilder<WjProduct>()
            .WhereJson("payload", "elapsed", TimeSpan.FromMinutes(1)))
            .Throws<NotSupportedException>();

    [Test]
    public async Task ByteArrayValue_ThrowsExplicitly()
        => await Assert.That(() => PgBuilder<WjProduct>()
            .WhereJson("payload", "blob", new byte[] { 1 }))
            .Throws<NotSupportedException>();

    [Test]
    public async Task DateTimeValue_ThrowsExplicitly()
        // ITM-641：区域格式与 jsonb ISO text 恒不相等——显式拒绝
        => await Assert.That(() => PgBuilder<WjProduct>()
            .WhereJson("payload", "when", DateTime.Now))
            .Throws<NotSupportedException>();

    [Test]
    public async Task DateOnlyValue_ThrowsExplicitly()
        // r19/ITM-683：DateOnly invariant 输出与 ISO text 恒不相等
        => await Assert.That(() => PgBuilder<WjProduct>()
            .WhereJson("payload", "day", new DateOnly(2026, 8, 16)))
            .Throws<NotSupportedException>();

    [Test]
    public async Task NulInValue_ThrowsArgumentException()
        // r19/ITM-701：value 与 column/path 同口径 NUL 显式拒绝
        => await Assert.That(() => PgBuilder<WjProduct>()
            .WhereJson("payload", "k", "a\0b"))
            .Throws<ArgumentException>();

    [Test]
    public async Task NulInColumn_ThrowsArgumentException()
        => await Assert.That(() => PgBuilder<WjProduct>()
            .WhereJson("pay\0load", "k", "v"))
            .Throws<ArgumentException>();

    [Test]
    public async Task OnSqliteDialect_ThrowsNotSupportedException()
    {
        // ITM-770 主契约：非 PG 方言构建期明确拒绝（原在 Integration 的守卫用例，迁此统一）
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        await Assert.That(() => session.From<WjProduct>()
            .WhereJson("payload", "k", "v"))
            .Throws<NotSupportedException>();
    }
}

#region Test Entity
[Table("wj_product")]
internal sealed partial class WjProduct
{
    [Key]
    public long Id { get; set; }
    [Column("payload")]
    public string Payload { get; set; } = "";
}
#endregion
