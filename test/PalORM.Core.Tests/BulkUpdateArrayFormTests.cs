using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>UNNEST 阶段 B（2026-10-02）：PG 批量 UPDATE / UPSERT 的**数组形态**锁定测试。
/// <para><b>手法与阶段 A 同源</b>：夹具报 PG 方言、底层 SQLite，Provider 实现
/// <c>CreateTypedArrayParameter</c>，命令层把 <c>UNNEST(@u0,…)</c> 展开为多值形态，
/// 使 Core 生成的 SQL 保持原样（测的是真实数组路径，不是近似物）。</para>
/// <para><b>为什么每个用例都断语句形状</b>：数组形态与 VALUES 形态在"结果对不对"上完全一致
/// （都能更新对），只有语句形状不同——阶段 A 的 mutation probe 已证明"只断结果"的测试对
/// 形态改动无判别力（10 个用例只 1 个变红）。</para>
/// <para><b>与 BulkDeleteArrayFormTests 同组串行</b>：两个夹具共享静态语句采集钩子
/// （<c>ArrayAnySqliteCommand.SetStatementRecorder</c>，last-write-wins），并行运行会互串
/// Shapes 记录——实测偶发（同轮 6 个用例假红，重跑全绿）。</para></summary>
[NotInParallel("ArrayFormFixtures")]
internal sealed class BulkUpdateArrayFormTests
{
    private static readonly List<(string Sql, int ParameterCount)> Shapes = [];

    private static async Task<DataSession<ArrayFormDialectProvider>> CreateSessionAsync()
    {
        Shapes.Clear();
        ArrayAnySqliteCommand.SetStatementRecorder((sql, parameterCount)
            => Shapes.Add((sql, parameterCount)));
        DataSession<ArrayFormDialectProvider> session =
            await DataSession<ArrayFormDialectProvider>.CreateAsync(
                new DbOptions
                {
                    // 共享内存库：连接包装器内层为 SQLite，跨连接可见须 Cache=Shared
                    ConnectionString = $"Data Source=unnestarr_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                });
        await session.ExecuteAsync(
            $"CREATE TABLE unnest_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        return session;
    }

    private static async Task SeedAsync(
        DataSession<ArrayFormDialectProvider> session, int count)
    {
        for (int i = 1; i <= count; i++)
            await session.ExecuteAsync(
                $"INSERT INTO unnest_rows (id, name, qty) VALUES ({i}, {"n" + i}, {i})");
    }

    /// <summary>形状断言：每条语句都是 UNNEST 形态且参数个数 == 列数（每列一个数组参数，与批宽无关）。</summary>
    private static void AssertAllStatementsAreUnnestForm(int expectedBatchCount, int expectedColumnCount)
    {
        if (Shapes.Count != expectedBatchCount)
            throw new InvalidOperationException(
                $"期望 {expectedBatchCount} 条语句，实际 {Shapes.Count} 条。");
        foreach ((string sql, int parameterCount) in Shapes)
        {
            if (!sql.Contains("FROM UNNEST(", StringComparison.Ordinal))
                throw new InvalidOperationException($"语句不是 UNNEST 形态：{sql}");
            if (sql.Contains("(VALUES", StringComparison.Ordinal))
                throw new InvalidOperationException($"语句仍是 VALUES 形态：{sql}");
            if (parameterCount != expectedColumnCount)
                throw new InvalidOperationException(
                    $"期望 {expectedColumnCount} 个数组参数（每列一个），实际 {parameterCount} 个：{sql}");
        }
    }

    [Test]
    public async Task BulkUpdate_ArrayForm_UpdatesEveryRow()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 10);
        List<UnnestRow> rows = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        foreach (UnnestRow row in rows)
        {
            row.Name = $"u{row.Id}";
            row.Qty = row.Id * 10;
        }
        Shapes.Clear();

        long affected = await session.BulkUpdateAsync(rows);

        await Assert.That(affected).IsEqualTo(10);
        List<UnnestRow> updated = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        await Assert.That(updated.All(r => r.Name == $"u{r.Id}")).IsTrue();
        await Assert.That(updated.All(r => r.Qty == r.Id * 10)).IsTrue();
        AssertAllStatementsAreUnnestForm(expectedBatchCount: 1, expectedColumnCount: 3);
    }

    /// <summary>多批与末批：5001 行 → 批大小 5000，两批（5000 + 1）。末批数组长度必须精确等于
    /// 实际行数——UNNEST 按数组长度展开，复用满批长数组会把上一批的残留值一起写回。</summary>
    [Test]
    public async Task BulkUpdate_ArrayForm_MultiBatchWithShortLastBatch_UpdatesEveryRow()
    {
        const int total = 5_001;
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, total);
        List<UnnestRow> rows = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        foreach (UnnestRow row in rows)
        {
            row.Name = $"m{row.Id}";
            row.Qty = 7;
        }
        Shapes.Clear();

        long affected = await session.BulkUpdateAsync(rows);

        await Assert.That(affected).IsEqualTo(total);
        List<UnnestRow> updated = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        await Assert.That(updated.Count).IsEqualTo(total);
        await Assert.That(updated.All(r => r.Name == $"m{r.Id}" && r.Qty == 7)).IsTrue();
        AssertAllStatementsAreUnnestForm(expectedBatchCount: 2, expectedColumnCount: 3);
    }

    /// <summary>单元素批次（批大小 1）：数组形态下数组长度 1 仍需正确。
    /// <para><b>为什么要造"批大小 = 1"而不是传一行</b>：<c>BulkUpdateAsync</c> 的自动路由要求
    /// <c>entities.Count &gt; 1</c>（单行走逐条池化路径，那条路径用 <c>DbBatch</c>，SQLite 夹具
    /// 不可达）。故以"两行实体、其中一行值不变"的方式让本批只改一行，锁定长度为 1 的数组路径。</para></summary>
    [Test]
    public async Task BulkUpdate_ArrayForm_SingleElementBatch_Updates()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 2);
        List<UnnestRow> rows = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        // 只改第二行，第一行原值回写（同值）——两行同时进批，锁定逐位对应
        rows[1].Name = "single";
        rows[1].Qty = 99;
        Shapes.Clear();

        long affected = await session.BulkUpdateAsync(rows);

        await Assert.That(affected).IsEqualTo(2);
        List<UnnestRow> updated = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        await Assert.That(updated[0].Name).IsEqualTo("n1");
        await Assert.That(updated[1].Name).IsEqualTo("single");
        await Assert.That(updated[1].Qty).IsEqualTo(99);
        AssertAllStatementsAreUnnestForm(expectedBatchCount: 1, expectedColumnCount: 3);
    }

    /// <summary>空批次：数组形态下不应发任何语句（空数组的 UNNEST 语义各库不一，且无意义）。</summary>
    [Test]
    public async Task BulkUpdate_ArrayForm_EmptyBatch_IssuesNoStatement()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 3);
        Shapes.Clear();

        long affected = await session.BulkUpdateAsync(Array.Empty<UnnestRow>());

        await Assert.That(affected).IsEqualTo(0);
        await Assert.That(Shapes).IsEmpty();
    }

    /// <summary>可空列的全 NULL：数组元素为 null（值类型走 T?[]），UPDATE 后列应为 SQL NULL。
    /// 探针已验证 PG 接受全 NULL 数组；此处锁 Core 侧的数组构造与绑定。</summary>
    [Test]
    public async Task BulkUpdate_ArrayForm_AllNullNullableColumn_WritesNull()
    {
        await using DataSession<ArrayFormDialectProvider> session =
            await DataSession<ArrayFormDialectProvider>.CreateAsync(
                new DbOptions
                {
                    ConnectionString = $"Data Source=unnestnull_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                });
        await session.ExecuteAsync(
            $"CREATE TABLE unnest_null_rows (id INTEGER PRIMARY KEY, marker INTEGER NULL)");
        for (int i = 1; i <= 4; i++)
            await session.ExecuteAsync(
                $"INSERT INTO unnest_null_rows (id, marker) VALUES ({i}, {i * 100})");
        List<UnnestNullRow> rows = await session.From<UnnestNullRow>().OrderBy(r => r.Id).ToListAsync();
        foreach (UnnestNullRow row in rows) row.Marker = null;
        Shapes.Clear();

        long affected = await session.BulkUpdateAsync(rows);

        await Assert.That(affected).IsEqualTo(4);
        List<UnnestNullRow> updated = await session.From<UnnestNullRow>().OrderBy(r => r.Id).ToListAsync();
        await Assert.That(updated.All(r => r.Marker is null)).IsTrue();
        AssertAllStatementsAreUnnestForm(expectedBatchCount: 1, expectedColumnCount: 2);
    }

    /// <summary>混合 null 与非 null：数组元素逐位对应（null 位保持 NULL，有值位写对）。</summary>
    [Test]
    public async Task BulkUpdate_ArrayForm_MixedNulls_MapsPerElement()
    {
        await using DataSession<ArrayFormDialectProvider> session =
            await DataSession<ArrayFormDialectProvider>.CreateAsync(
                new DbOptions
                {
                    ConnectionString = $"Data Source=unnestmix_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                });
        await session.ExecuteAsync(
            $"CREATE TABLE unnest_null_rows (id INTEGER PRIMARY KEY, marker INTEGER NULL)");
        for (int i = 1; i <= 6; i++)
            await session.ExecuteAsync(
                $"INSERT INTO unnest_null_rows (id, marker) VALUES ({i}, 0)");
        List<UnnestNullRow> rows = await session.From<UnnestNullRow>().OrderBy(r => r.Id).ToListAsync();
        // 偶数行置 null，奇数行给值
        foreach (UnnestNullRow row in rows)
            row.Marker = row.Id % 2 == 0 ? null : row.Id * 11;
        Shapes.Clear();

        long affected = await session.BulkUpdateAsync(rows);

        await Assert.That(affected).IsEqualTo(6);
        List<UnnestNullRow> updated = await session.From<UnnestNullRow>().OrderBy(r => r.Id).ToListAsync();
        for (int i = 0; i < updated.Count; i++)
        {
            long id = updated[i].Id;
            long? expected = id % 2 == 0 ? null : id * 11;
            await Assert.That(updated[i].Marker).IsEqualTo(expected);
        }
        AssertAllStatementsAreUnnestForm(expectedBatchCount: 1, expectedColumnCount: 2);
    }

    /// <summary>公开的 <c>BulkUpdateBatchAsync</c> 入口也走数组形态——两个 UPDATE 入口对称
    /// （B120 族纪律：同操作两形态是不对称缺陷的温床，形状断言各自锁死）。</summary>
    [Test]
    public async Task BulkUpdateBatchAsync_ArrayForm_UsesUnnestShape()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 5);
        List<UnnestRow> rows = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        foreach (UnnestRow row in rows)
        {
            row.Name = $"b{row.Id}";
            row.Qty = row.Id * 3;
        }
        Shapes.Clear();

        long affected = await session.BulkUpdateBatchAsync(rows);

        await Assert.That(affected).IsEqualTo(5);
        List<UnnestRow> updated = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        await Assert.That(updated.All(r => r.Name == $"b{r.Id}" && r.Qty == r.Id * 3)).IsTrue();
        AssertAllStatementsAreUnnestForm(expectedBatchCount: 1, expectedColumnCount: 3);
    }

    /// <summary>生成器契约（含并发令牌实体也成立）：数组列数 == SET 列数 + 1（主键）。
    /// <para><b>为什么这条必须独立存在</b>：带 <c>[ConcurrencyCheck]</c> 的实体走不到数组形态
    /// （两条 UPDATE 入口都拒绝），所以执行路径测不到它的列数；而并发令牌列曾被发射进数组
    /// （个数多 1），一旦前置条件放宽就会以驱动的「参数未被使用」异常暴露。这里直接读元数据
    /// 断言契约，把漂移挡在编译后的第一次元数据加载上。</para></summary>
    [Test]
    public async Task UpdateArrayContract_ColumnCountMatchesSetColumnsPlusKey()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        foreach (Type entityType in new[] { typeof(UnnestRow), typeof(ConcurrencyRow) })
        {
            CrudMetadata metadata = state._crudMetadatas[entityType];
            IReadOnlyList<Type> elementTypes = metadata.UpdateColumnArrayElementTypes
                ?? throw new InvalidOperationException($"'{entityType.Name}' 缺数组元素类型表。");
            await Assert.That(elementTypes.Count)
                .IsEqualTo(metadata.UpdateColumns.Count + 1)
                .Because($"{entityType.Name}：数组列序应为 SET 列（{metadata.UpdateColumns.Count}）+ 主键 1");
        }
    }

    /// <summary>回退路径锁定：Provider 未实现 <c>CreateTypedArrayParameter</c>（能力检测为假）
    /// 时行为必须与优化前一致（VALUES 形态）。缺这条测不出"能力检测写错"。</summary>
    [Test]
    public async Task BulkUpdate_UnsupportedProvider_FallsBackToValuesForm()
    {
        Shapes.Clear();
        ArrayAnySqliteCommand.SetStatementRecorder((sql, parameterCount)
            => Shapes.Add((sql, parameterCount)));
        // NoArraySqliteProvider 是 SQLite 方言——BulkUpdateAsync 的自动路由本就排除 SQLite，
        // 故用支持数组方言语义但**不实现** typed 数组参数的夹具：ArrayFormDialectProvider 的
        // 兄弟形态（见 UnnestNoTypedArrayProvider）。
        await using DataSession<UnnestNoTypedArrayProvider> session =
            await DataSession<UnnestNoTypedArrayProvider>.CreateAsync(
                new DbOptions
                {
                    ConnectionString = $"Data Source=unnestfb_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                });
        await session.ExecuteAsync(
            $"CREATE TABLE unnest_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        for (int i = 1; i <= 5; i++)
            await session.ExecuteAsync(
                $"INSERT INTO unnest_rows (id, name, qty) VALUES ({i}, {"n" + i}, {i})");
        List<UnnestRow> rows = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        foreach (UnnestRow row in rows) row.Name = $"f{row.Id}";
        Shapes.Clear();

        long affected = await session.BulkUpdateAsync(rows);

        await Assert.That(affected).IsEqualTo(5);
        List<UnnestRow> updated = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        await Assert.That(updated.All(r => r.Name == $"f{r.Id}")).IsTrue();
        // 回退形态：VALUES 多值（每行一组占位符）
        await Assert.That(Shapes.Count).IsEqualTo(1);
        await Assert.That(Shapes[0].Sql).Contains("(VALUES");
        await Assert.That(Shapes[0].Sql).DoesNotContain("UNNEST");
        // 每行 (qty, name, pk) 三个参数 × 5 行
        await Assert.That(Shapes[0].ParameterCount).IsEqualTo(15);
    }

    // ─────────────────────────────────────────────────────────────
    // UPSERT（BulkMergeAsync）的数组形态
    // ─────────────────────────────────────────────────────────────

    /// <summary>UPSERT 形状断言：语句是 <c>SELECT * FROM UNNEST(…)</c> + 冲突子句，
    /// 参数个数 == 列数（每列一个数组参数）。</summary>
    private static void AssertAllStatementsAreUpsertUnnestForm(int expectedBatchCount, int expectedColumnCount)
    {
        if (Shapes.Count != expectedBatchCount)
            throw new InvalidOperationException(
                $"期望 {expectedBatchCount} 条语句，实际 {Shapes.Count} 条。");
        foreach ((string sql, int parameterCount) in Shapes)
        {
            if (!sql.Contains("SELECT * FROM UNNEST(", StringComparison.Ordinal))
                throw new InvalidOperationException($"语句不是 UPSERT 数组形态：{sql}");
            if (sql.Contains("(VALUES", StringComparison.Ordinal))
                throw new InvalidOperationException($"语句仍是多值 VALUES 形态：{sql}");
            if (parameterCount != expectedColumnCount)
                throw new InvalidOperationException(
                    $"期望 {expectedColumnCount} 个数组参数，实际 {parameterCount} 个：{sql}");
        }
    }

    /// <summary>UPSERT 两分支：首轮全新主键走插入、次轮同主键换值走冲突更新——
    /// 行数不膨胀、值被覆盖，与既有形态语义一致。</summary>
    [Test]
    public async Task BulkMerge_ArrayForm_InsertThenConflictUpdate()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        var first = new List<UnnestRow>();
        for (int i = 1; i <= 5; i++)
            first.Add(new UnnestRow { Id = i, Name = $"ins{i}", Qty = i });
        Shapes.Clear();
        long inserted = await session.BulkMergeAsync(first);
        AssertAllStatementsAreUpsertUnnestForm(expectedBatchCount: 1, expectedColumnCount: 3);

        foreach (UnnestRow row in first)
        {
            row.Name = $"upd{row.Id}";
            row.Qty = row.Id * 100;
        }
        Shapes.Clear();
        long second = await session.BulkMergeAsync(first);
        AssertAllStatementsAreUpsertUnnestForm(expectedBatchCount: 1, expectedColumnCount: 3);

        List<UnnestRow> rows = await session.From<UnnestRow>().OrderBy(r => r.Id).ToListAsync();
        await Assert.That(inserted).IsEqualTo(5);
        await Assert.That(second).IsEqualTo(5);
        await Assert.That(rows.Count).IsEqualTo(5);  // 冲突更新不膨胀
        await Assert.That(rows.All(r => r.Name == $"upd{r.Id}" && r.Qty == r.Id * 100)).IsTrue();
    }

    /// <summary>多批与末批：5001 行 → 5000 + 1 两批，末批数组长度精确。</summary>
    [Test]
    public async Task BulkMerge_ArrayForm_MultiBatchWithShortLastBatch()
    {
        const int total = 5_001;
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        var entities = new List<UnnestRow>(total);
        for (long i = 1; i <= total; i++)
            entities.Add(new UnnestRow { Id = i, Name = $"s{i}", Qty = (int)i });
        Shapes.Clear();

        long merged = await session.BulkMergeAsync(entities);

        await Assert.That(merged).IsEqualTo(total);
        await Assert.That((await session.From<UnnestRow>().ToListAsync()).Count).IsEqualTo(total);
        AssertAllStatementsAreUpsertUnnestForm(expectedBatchCount: 2, expectedColumnCount: 3);
    }

    /// <summary>回退路径：Provider 不支持 typed 数组参数时保持多值 VALUES 形态。</summary>
    [Test]
    public async Task BulkMerge_UnsupportedProvider_FallsBackToValuesForm()
    {
        Shapes.Clear();
        ArrayAnySqliteCommand.SetStatementRecorder((sql, parameterCount)
            => Shapes.Add((sql, parameterCount)));
        await using DataSession<UnnestNoTypedArrayProvider> session =
            await DataSession<UnnestNoTypedArrayProvider>.CreateAsync(
                new DbOptions
                {
                    ConnectionString = $"Data Source=unnestmfb_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                });
        await session.ExecuteAsync(
            $"CREATE TABLE unnest_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        var entities = new List<UnnestRow>();
        for (long i = 1; i <= 5; i++)
            entities.Add(new UnnestRow { Id = i, Name = $"v{i}", Qty = (int)i });
        Shapes.Clear();

        long merged = await session.BulkMergeAsync(entities);

        await Assert.That(merged).IsEqualTo(5);
        await Assert.That((await session.From<UnnestRow>().ToListAsync()).Count).IsEqualTo(5);
        await Assert.That(Shapes.Count).IsEqualTo(1);
        // UPSERT 回退形态：多值 VALUES（注意与 UPDATE 回退的 "FROM (VALUES" 不同——
        // 这里是 INSERT 的 "… ) VALUES (@p0, …)"）
        await Assert.That(Shapes[0].Sql).Contains("VALUES (@p0");
        await Assert.That(Shapes[0].Sql).DoesNotContain("UNNEST");
        // 回退形态每行 3 个参数（列数）× 5 行
        await Assert.That(Shapes[0].ParameterCount).IsEqualTo(15);
    }
}

[Table("unnest_rows")]
internal sealed partial class UnnestRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("qty")]
    public long Qty { get; set; }
}

/// <summary>数组列数契约的对照实体：带并发令牌列（该实体走不到数组形态，但契约仍须成立——
/// 并发列不得进入数组列序，见 CommandFactoryEmitter.UpdateArrayColumns 的说明）。</summary>
[Table("unnest_concurrency_rows")]
internal sealed partial class ConcurrencyRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public long Version { get; set; }
}

[Table("unnest_null_rows")]
internal sealed partial class UnnestNullRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("marker")]
    public long? Marker { get; set; }
}

/// <summary>报 PG 方言、底层 SQLite、实现 <c>CreateArrayParameter</c>（阶段 A 形态）
/// 但**不实现** <c>CreateTypedArrayParameter</c>——用于锁 BulkUpdate 的回退路径。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Generic type argument only — static abstract interface members are never invoked on an instance.")]
internal sealed class UnnestNoTypedArrayProvider : IDbProvider
{
    public static string Name => "UnnestNoTypedArray";

    public static SqlDialect Dialect => SqlDialect.PostgreSql;

    public static DbConnection CreateConnection(string connectionString, DbOptions options)
        => new ArrayAnySqliteConnection(connectionString);

    public static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    public static bool SupportsReturningClause => true;

    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    public static DbParameter CreateParameter(string name, object? value)
        => new SqliteParameter(name, value ?? DBNull.Value);

    public static DbParameter? CreateArrayParameter(string name, Array values)
        => new SqliteParameter(name, ArrayFormDialectProvider.ToJsonArray(values));

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
#pragma warning disable S2077, CA2100 // 表名经 QuoteIdentifier 转义（标识符面）；测试夹具无用户输入
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
#pragma warning restore S2077, CA2100
        return 1;
    }
}
