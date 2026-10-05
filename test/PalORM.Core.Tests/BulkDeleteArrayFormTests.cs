using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>UNNEST-1（2026-10-02）：<c>BulkDeleteAsync</c> 的「数组形态」锁定测试。
/// <para><b>为什么用方言重映射夹具</b>：数组形态的判据是<b>能力检测</b>（Provider 的
/// <c>CreateArrayParameter</c> 对真实元素类型返回非 null），不是方言枚举。本夹具报 PG 方言
/// 而底层连接是 SQLite——SQLite 原生支持 <c>x = ANY(?)</c>（参数为数组时展开为元素集合），
/// 故 Core 侧的分支选择、参数构造、批循环全部可在无真库环境下逐条锁定。
/// 真库侧（Npgsql 的 <c>NpgsqlDbType.Array</c>）由 ExternalDatabase 档覆盖。</para>
/// <para>与 <c>BatchedDialectProvider</c> 同一手法，风险面相同：本路径不依赖 PG 专属语法
/// （<c>= ANY</c> 是两方言共有），只依赖参数的数组绑定能力。</para>
/// <para><b>组键 ArrayFormFixtures</b>：与 BulkUpdateArrayFormTests 共享静态采集钩子，必须同组串行。</para></summary>
[NotInParallel("ArrayFormFixtures")]
internal sealed class BulkDeleteArrayFormTests
{
    /// <summary>夹具执行的语句形状记录（SQL 文本 + 参数个数）——由
    /// <see cref="ArrayAnySqliteCommand"/> 在 ExecuteNonQuery 前采集。
    /// <para><b>为什么必须有形状断言</b>：数组形态与 IN 形态在"结果对不对"上完全一致
    /// （都能删干净），只有语句形状不同——<c>= ANY(单个数组参数)</c> 对 <c>IN (N 个参数)</c>。
    /// 没有形状断言，"能力检测写错而静默回退到 IN"不会有任何红（B95 家族：数字对但没测到目标）。</para></summary>
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
                    ConnectionString = $"Data Source=arrayform_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                });
        await session.ExecuteAsync(
            $"CREATE TABLE array_form_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        return session;
    }

    /// <summary>播种 <paramref name="count"/> 行。逐行参数化 INSERT——<c>ExecuteAsync</c> 只有
    /// <see cref="FormattableString"/> 重载（其内插值一律参数化，拼出的 SQL 文本无法直传），
    /// 故多值 INSERT 的裸 SQL 走不通；本夹具只需"行存在"，播种走最直白的形式。
    /// 不调 <c>BulkInsertAsync</c>：那是另一条被测路径，不应为播种给夹具加 override。</summary>
    private static async Task SeedAsync(
        DataSession<ArrayFormDialectProvider> session, int count)
    {
        for (int i = 1; i <= count; i++)
            await session.ExecuteAsync(
                $"INSERT INTO array_form_rows (id, name) VALUES ({i}, {"row" + i})");
    }

    /// <summary>数组形态断言：每次执行的语句都必须是 <c>= ANY(@ids)</c> 且整批只有一个参数
    /// （IN 形态会是每键一个参数）。<b>为什么每个数组形态用例都要调用它</b>：只断"删干净了"
    /// 无法区分两条形态——两者都能删干净；mutation probe（把 <c>useArrayForm</c> 强制 false）
    /// 实测：只在 DeletesEveryKey 一处断形状时，9 个用例仅 1 个变红，其余照样绿。</summary>
    private static async Task AssertAllStatementsAreArrayFormAsync(
        List<(string Sql, int ParameterCount)> shapes, int expectedBatchCount)
    {
        await Assert.That(shapes.Count).IsEqualTo(expectedBatchCount);
        foreach ((string sql, int parameterCount) in shapes)
        {
            await Assert.That(sql).Contains("= ANY(@ids)");
            await Assert.That(sql).DoesNotContain("IN (");
            await Assert.That(parameterCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task BulkDelete_ArrayForm_DeletesEveryKey()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 10);
        Shapes.Clear();

        long deleted = await session.BulkDeleteAsync<ArrayFormRow>(
            [1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L]);

        await Assert.That(deleted).IsEqualTo(10);
        await Assert.That((await session.GetAllAsync<ArrayFormRow>()).Count).IsEqualTo(0);
        await AssertAllStatementsAreArrayFormAsync(Shapes, expectedBatchCount: 1);
    }

    /// <summary>反向断言（对齐 S3 反向验证）：同一操作在<em>不支持数组</em>的 Provider 上
    /// 必须落到 IN 形态——形状与参数个数都要变。缺这条，"数组形态"与"永远走 IN"
    /// 两种代码在同一批测试下都会绿。</summary>
    [Test]
    public async Task BulkDelete_InForm_ProducesInPlaceholdersNotAny()
    {
        Shapes.Clear();
        ArrayAnySqliteCommand.SetStatementRecorder((sql, parameterCount)
            => Shapes.Add((sql, parameterCount)));
        await using DataSession<NoArraySqliteProvider> session =
            await DataSession<NoArraySqliteProvider>.CreateAsync(
                new DbOptions
                {
                    ConnectionString = $"Data Source=noarray_shape_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                });
        await session.ExecuteAsync(
            $"CREATE TABLE no_array_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        for (int i = 1; i <= 3; i++)
            await session.ExecuteAsync(
                $"INSERT INTO no_array_rows (id, name) VALUES ({i}, {"v" + i})");
        Shapes.Clear();

        long deleted = await session.BulkDeleteAsync<NoArrayRow>([1L, 2L, 3L]);

        await Assert.That(deleted).IsEqualTo(3);
        await Assert.That(Shapes.Count).IsEqualTo(1);
        await Assert.That(Shapes[0].Sql).Contains("IN (@p0, @p1, @p2)");
        await Assert.That(Shapes[0].Sql).DoesNotContain("ANY");
        await Assert.That(Shapes[0].ParameterCount).IsEqualTo(3);
    }

    [Test]
    public async Task BulkDelete_ArrayForm_MultiBatch_DeletesEveryKey()
    {
        // 批大小 = SqlLimits.MaxRowsPerBatch（数组形态不受语句参数个数约束）；
        // 键数远大于它 → 多批循环。批边界算错会在这里留下残行。
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        const int total = 12_000;
        await SeedAsync(session, total);
        Shapes.Clear();

        var keys = new List<object>(total);
        for (int i = 1; i <= total; i++) keys.Add((long)i);
        long deleted = await session.BulkDeleteAsync<ArrayFormRow>(keys);

        await Assert.That(deleted).IsEqualTo(total);
        await Assert.That((await session.GetAllAsync<ArrayFormRow>()).Count).IsEqualTo(0);
        // 12000 键 / 批大小 5000 → 3 批（5000+5000+2000）
        await AssertAllStatementsAreArrayFormAsync(Shapes, expectedBatchCount: 3);
    }

    [Test]
    public async Task BulkDelete_ArrayForm_UnknownKeys_CountZeroRows()
    {
        // 未命中的键不影响计数口径（DELETE 的 return 是受影响行数，不是键数）
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 3);
        Shapes.Clear();

        long deleted = await session.BulkDeleteAsync<ArrayFormRow>([1L, 999L, 1000L]);

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That((await session.GetAllAsync<ArrayFormRow>()).Count).IsEqualTo(2);
        await AssertAllStatementsAreArrayFormAsync(Shapes, expectedBatchCount: 1);
    }

    [Test]
    public async Task BulkDelete_ArrayForm_LastBatchShorterThanFull_DeletesEveryKey()
    {
        // 5001 键（SqlLimits.MaxRowsPerBatch = 5000）：末批只剩 1 个元素——数组构造器的
        // count 参数若误用批大小（而非实际批长度）会越界或多删，此用例专锁该边界。
        const int total = 5_001;
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, total);
        Shapes.Clear();

        var keys = new List<object>(total);
        for (int i = 1; i <= total; i++) keys.Add((long)i);
        long deleted = await session.BulkDeleteAsync<ArrayFormRow>(keys);

        await Assert.That(deleted).IsEqualTo(total);
        await Assert.That((await session.GetAllAsync<ArrayFormRow>()).Count).IsEqualTo(0);
        await AssertAllStatementsAreArrayFormAsync(Shapes, expectedBatchCount: 2);
    }

    [Test]
    public async Task BulkDelete_ArrayForm_NonNativeKeyType_NormalizedLikeBindDelete()
    {
        // 键传 int（非 long）——数组构造器与 BindDelete 同用 Convert.ToInt64 归一。
        // 若数组形态忘记归一，会抛 InvalidCastException（同 ITM-587 家族）。
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 4);
        Shapes.Clear();

        long deleted = await session.BulkDeleteAsync<ArrayFormRow>([1, 2]);

        await Assert.That(deleted).IsEqualTo(2);
        await Assert.That((await session.GetAllAsync<ArrayFormRow>()).Count).IsEqualTo(2);
        await AssertAllStatementsAreArrayFormAsync(Shapes, expectedBatchCount: 1);
    }

    [Test]
    public async Task BulkDelete_ArrayForm_SoftDeleteEntity_MarksDeletedAt()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync(
            $"CREATE TABLE array_form_soft_rows (id INTEGER PRIMARY KEY, deleted_at TEXT NULL)");
        await session.ExecuteAsync(
            $"INSERT INTO array_form_soft_rows (id, deleted_at) VALUES (1, NULL), (2, NULL), (3, NULL)");
        Shapes.Clear();

        long affected = await session.BulkDeleteAsync<ArrayFormSoftRow>([1L, 2L]);

        await Assert.That(affected).IsEqualTo(2);
        List<ArrayFormSoftRow> rows = await session.IgnoreFilters()
            .From<ArrayFormSoftRow>().OrderBy(row => row.Id).ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(3);  // 软删不物理删行
        await Assert.That(rows[0].DeletedAt).IsNotNull();
        await Assert.That(rows[1].DeletedAt).IsNotNull();
        await Assert.That(rows[2].DeletedAt).IsNull();
        // 软删也走数组形态（UPDATE ... SET deleted_at = ... WHERE pk = ANY(@ids) AND deleted_at IS NULL）
        await AssertAllStatementsAreArrayFormAsync(Shapes, expectedBatchCount: 1);
        await Assert.That(Shapes[0].Sql).StartsWith("UPDATE ");
        await Assert.That(Shapes[0].Sql).Contains("IS NULL");
    }

    [Test]
    public async Task BulkDelete_ArrayForm_SoftDeleteEntity_SecondCallIsIdempotent()
    {
        // 软删语句带 deleted_at IS NULL 条件——重复删除计数为 0（与 IN 形态同语义）
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await session.ExecuteAsync(
            $"CREATE TABLE array_form_soft_rows (id INTEGER PRIMARY KEY, deleted_at TEXT NULL)");
        await session.ExecuteAsync(
            $"INSERT INTO array_form_soft_rows (id, deleted_at) VALUES (1, NULL)");
        Shapes.Clear();

        await Assert.That(await session.BulkDeleteAsync<ArrayFormSoftRow>([1L])).IsEqualTo(1);
        await Assert.That(await session.BulkDeleteAsync<ArrayFormSoftRow>([1L])).IsEqualTo(0);
        // 两次调用都走数组形态（幂等由 SQL 的 deleted_at IS NULL 保证，非靠回退）
        await AssertAllStatementsAreArrayFormAsync(Shapes, expectedBatchCount: 2);
    }

    [Test]
    public async Task BulkDelete_ArrayForm_EmptyKeys_ShortCircuitsWithoutStatement()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 2);
        Shapes.Clear();

        long deleted = await session.BulkDeleteAsync<ArrayFormRow>([]);

        await Assert.That(deleted).IsEqualTo(0);
        await Assert.That((await session.GetAllAsync<ArrayFormRow>()).Count).IsEqualTo(2);
        // 空键集在构造语句之前就短路——连一条语句都不该发（空数组的 ANY 语义各库不一）
        await Assert.That(Shapes).IsEmpty();
    }

    /// <summary>回退路径锁定：Provider 未实现数组参数（能力检测为假）时，行为必须与优化前
    /// 逐位一致——IN 占位符形态、逐键绑定、多批分片全部照旧。缺这条测试，将来把
    /// <c>useArrayForm</c> 的判据写错（例如硬编码方言）不会有任何红。</summary>
    [Test]
    public async Task BulkDelete_InFormFallback_WhenProviderLacksArraySupport()
    {
        await using DataSession<NoArraySqliteProvider> session =
            await DataSession<NoArraySqliteProvider>.CreateAsync(
                new DbOptions
                {
                    ConnectionString = $"Data Source=noarray_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                });
        await session.ExecuteAsync(
            $"CREATE TABLE no_array_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        for (int i = 1; i <= 1_200; i++)
            await session.ExecuteAsync(
                $"INSERT INTO no_array_rows (id, name) VALUES ({i}, {"r" + i})");

        // 1200 键 > SQLite 999 参数上限 → IN 形态必然分 2 批（数组形态会一批完成）
        var keys = new List<object>(1_200);
        for (int i = 1; i <= 1_200; i++) keys.Add((long)i);
        long deleted = await session.BulkDeleteAsync<NoArrayRow>(keys);

        await Assert.That(deleted).IsEqualTo(1_200);
        await Assert.That((await session.GetAllAsync<NoArrayRow>()).Count).IsEqualTo(0);
    }

    /// <summary>数组构造器与单值 binder 的**逐位一致**断言（两条路径的唯一真源契约）。
    /// <para>生成物里 <c>BuildDeleteKeyArray</c> 与 <c>BindDelete</c> 共用
    /// <c>BuildKeyCastExpression</c>——若将来只改一处（B120 同构不对称族），数组形态会
    /// 静默改变绑定语义（例如丢归一化、丢转换器 ToProvider），而"删掉了吗"这类端到端断言
    /// 在键类型恰好同型时看不出差别。这里直接对同一组键逐位比对两条路径的产出。</para>
    /// <para>覆盖 int（非 long，触发 <c>Convert.ToInt64</c> 归一）与原生 long 两条分支。</para></summary>
    [Test]
    public async Task BuildDeleteKeyArray_MatchesBindDelete_PerElement()
    {
        await using DataSession<ArrayFormDialectProvider> session = await CreateSessionAsync();
        await SeedAsync(session, 4);

        var keys = new object[] { 1, 3L, 2, 4L };
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        CrudMetadata metadata = state._crudMetadatas[typeof(ArrayFormRow)];
        Action<DbCommand, object> bindDelete = state._bindDelete[typeof(ArrayFormRow)];
        Func<IReadOnlyList<object>, int, int, Array?> builder = metadata.BuildDeleteKeyArray
            ?? throw new InvalidOperationException("数组构造器缺失（单列主键应当发射）。");

        Array built = builder(keys, 0, keys.Length)
            ?? throw new InvalidOperationException("数组构造器返回 null（单列主键不应回退）。");
        await Assert.That(built).IsTypeOf<long[]>();

        // 单值 binder 的产出：用夹具 Provider 自己的连接建命令（会话的连接访问器是 internal）
        var expected = new long[keys.Length];
        await using (var connection = new ArrayAnySqliteConnection("Data Source=:memory:"))
        {
            await connection.OpenAsync();
            for (int i = 0; i < keys.Length; i++)
            {
                await using DbCommand scratch = connection.CreateCommand();
                bindDelete(scratch, keys[i]);
                await Assert.That(scratch.Parameters.Count).IsEqualTo(1);
                expected[i] = (long)scratch.Parameters[0].Value!;
            }
        }

        await Assert.That((long[])built).IsEquivalentTo(expected);

        // 区间参数（start/count）与数组下标的关系——调用方按批传入非零 start
        Array offset = builder(keys, 1, 2)
            ?? throw new InvalidOperationException("数组构造器返回 null（区间调用）。");
        await Assert.That((long[])offset).IsEquivalentTo([expected[1], expected[2]]);
    }
}

/// <summary>数组形态夹具：报 PG 方言、底层 SQLite，并实现
/// <see cref="IDbProvider.CreateArrayParameter"/>。
/// <para><b>为什么能把 PG 的 <c>= ANY(@ids)</c> 跑在 SQLite 上</b>：Core 生成的 SQL 保持原样
/// （<c>WHERE "id" = ANY(@ids)</c> 的语法在 SQLite 上不被识别），夹具在参数侧把数组编码成
/// JSON 文本，并让该 SQL 在 SQLite 上等价成立——见 <see cref="ArrayAnySqliteConnection"/>。
/// 这样测试锁的是**真实的数组形态路径**（真实的 SQL 文本、真实的单参数契约、真实的批循环），
/// 而不是被改写成 IN 列表的近似物。</para>
/// <para><b>不用于</b>：依赖 PG 专属能力（<c>NpgsqlDbType.Array</c> 的真绑定、Binary COPY）的路径——
/// 那些由 ExternalDatabase 档覆盖。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Generic type argument only — static abstract interface members are never invoked on an instance.")]
internal sealed class ArrayFormDialectProvider : IDbProvider
{
    public static string Name => "ArrayFormDialect";

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

    /// <summary>数组参数：数组 → JSON 数组文本。Core 生成的 <c>= ANY(@ids)</c> 在 SQLite 上
    /// 无对应运算，由 <see cref="ArrayAnySqliteConnection"/> 把该子句重写为
    /// <c>IN (SELECT value FROM json_each(@ids))</c>——语义与 PG 的 <c>= ANY(数组)</c> 一致
    /// （含 NULL 元素时两者都按 SQL 三值逻辑不匹配，故行为同构）。</summary>
    public static DbParameter? CreateArrayParameter(string name, Array values)
        => new SqliteParameter(name, ToJsonArray(values));

    /// <summary>UNNEST 阶段 B：显式元素类型的数组参数——同样编码为 JSON 数组文本，
    /// 由连接把 <c>UNNEST(@u0, @u1, …)</c> 重写为 <c>json_each</c> 的多列连接。</summary>
    public static DbParameter? CreateTypedArrayParameter(string name, Array values, Type elementType)
    {
        _ = elementType;  // 夹具不区分元素类型（JSON 文本承载一切），仅锁调用契约
        return new SqliteParameter(name, ToJsonArray(values));
    }

    internal static string ToJsonArray(Array values)
    {
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            object? item = values.GetValue(i);
            sb.Append(item switch
            {
                null or DBNull => "null",
                string s => System.Text.Json.JsonSerializer.Serialize(s),
                bool b => b ? "true" : "false",
                _ => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        return sb.Append(']').ToString();
    }

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
#pragma warning disable S2077, CA2100 // 表名经 QuoteIdentifier 转义（标识符面）；测试夹具无用户输入
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
#pragma warning restore S2077, CA2100
        return 1;
    }
}

/// <summary>把 <c>= ANY(@ids)</c> / <c>= ANY($1)</c> 重写为 SQLite 的
/// <c>IN (SELECT value FROM json_each(&lt;参数名&gt;))</c>，其余语句原样透传。
/// <para>只在测试夹具里成立：参数值必须是 <see cref="ArrayFormDialectProvider.ToJsonArray"/>
/// 产出的 JSON 数组文本。</para></summary>
internal sealed class ArrayAnySqliteConnection(string connectionString) : DbConnection
{
    private readonly SqliteConnection _inner = new(connectionString);

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value ?? "";
    }

    public override string Database => _inner.Database;

    public override string DataSource => _inner.DataSource;

    public override string ServerVersion => _inner.ServerVersion;

    public override System.Data.ConnectionState State => _inner.State;

    public override void Open() => _inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);

    public override void Close() => _inner.Close();

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

    protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
        => _inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() => new ArrayAnySqliteCommand(_inner.CreateCommand());

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class ArrayAnySqliteCommand(SqliteCommand inner) : DbCommand
{
    /// <summary>语句执行前的采集钩子（SQL 文本 + 参数个数）。测试夹具专用——Core 的批量路径
    /// 不通知拦截器（实测 <c>AddInterceptor</c> 在该路径记录 0 条），故只能用命令层采集。
    /// </summary>
    private static Action<string, int>? _onStatement;

    /// <summary>最近一次设置的 <b>Core 生成的原始语句</b>（改写前）——形状断言测的是
    /// Core 产出了什么，不是夹具把它改写成了什么（记录改写后文本会让"= ANY"永远测不到）。</summary>
    private string _originalSql = "";

    /// <summary>注册采集钩子（测试串行使用，见类级 <c>NotInParallel</c>）。</summary>
    internal static void SetStatementRecorder(Action<string, int> recorder)
        => _onStatement = recorder;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => _originalSql;
#pragma warning disable CA2100 // value 是 Core 生成的语句文本（本夹具的改写输入），非用户输入
        set
        {
            _originalSql = value ?? "";
            inner.CommandText = Rewrite(_originalSql);
        }
#pragma warning restore CA2100
    }

    public override int CommandTimeout
    {
        get => inner.CommandTimeout;
        set => inner.CommandTimeout = value;
    }

    public override System.Data.CommandType CommandType
    {
        get => inner.CommandType;
        set => inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => inner.DesignTimeVisible;
        set => inner.DesignTimeVisible = value;
    }

    public override System.Data.UpdateRowSource UpdatedRowSource
    {
        get => inner.UpdatedRowSource;
        set => inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => inner.Connection;
        set => inner.Connection = (SqliteConnection?)value;
    }

    protected override DbParameterCollection DbParameterCollection => inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => inner.Transaction;
        set => inner.Transaction = (SqliteTransaction?)value;
    }

    public override void Cancel() => inner.Cancel();

    public override int ExecuteNonQuery()
    {
        _onStatement?.Invoke(_originalSql, inner.Parameters.Count);
        return inner.ExecuteNonQuery();
    }

    public override object? ExecuteScalar() => inner.ExecuteScalar();

    public override void Prepare() => inner.Prepare();

    protected override DbParameter CreateDbParameter() => inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior)
        => inner.ExecuteReader(behavior);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        System.Data.CommandBehavior behavior, CancellationToken cancellationToken)
        // SqliteDataReader 是 DbDataReader 的子类，但 Task<T> 不变——需显式桥接。
        => WrapAsync(inner.ExecuteReaderAsync(behavior, cancellationToken));

    private static async Task<DbDataReader> WrapAsync(Task<SqliteDataReader> pending)
        => await pending.ConfigureAwait(false);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        _onStatement?.Invoke(_originalSql, inner.Parameters.Count);
        return inner.ExecuteNonQueryAsync(cancellationToken);
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        => inner.ExecuteScalarAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary><c>= ANY(@p)</c> → <c>IN (SELECT value FROM json_each(@p))</c>。
    /// 参数名限定为 <c>@</c>/<c>$</c> 起始的标识符序列，避免吞掉后续字符。</summary>
    /// <summary>把 PG 语法改写为 SQLite 可执行的等价形态（夹具专有，产品代码不含此类逻辑）：
    /// <list type="bullet">
    /// <item><c>= ANY(@p)</c> → <c>IN (SELECT value FROM json_each(@p))</c>（阶段 A 的删除/键集）；</item>
    /// <item><c>FROM UNNEST(@u0, @u1, …) AS v(c0, c1, …)</c> → 一组按 <c>key</c> 等值连接的
    /// <c>json_each</c> 子查询（阶段 B 的 UPDATE/UPSERT）；</item>
    /// <item><c>FROM (VALUES (@p0, @p1, …), (@p2, …)) AS v(c0, c1, …)</c> → 把各 <c>VALUES</c> 行
    /// 改写为等价的 <c>UNION ALL</c> 形式（VALUES 形态的回退路径也要能在 SQLite 上跑）。</item>
    /// </list>
    /// <para>改写后参数名不变（仍引用原参数），故绑定契约不变——测的仍是 Core 生成的形态。</para></summary>
    private static string Rewrite(string sql)
    {
        string rewritten = RewriteAny(sql);
        // UPDATE 形态带 "AS v(...)"，UPSERT 形态无别名（RewriteUnnest 对无别名形态原样返回）
        rewritten = rewritten.Contains("FROM UNNEST(", StringComparison.Ordinal)
            ? RewriteUnnest(rewritten)
            : rewritten;
        rewritten = rewritten.Contains("SELECT * FROM UNNEST(", StringComparison.Ordinal)
            ? RewriteSelectUnnest(rewritten)
            : rewritten;
        return rewritten.Contains("FROM (VALUES ", StringComparison.Ordinal)
            ? RewriteValues(rewritten)
            : rewritten;
    }

    /// <summary><c>SELECT * FROM UNNEST(@u0, @u1, …)</c>（UPSERT 数组形态，无别名）→
    /// <c>SELECT j0.value, j1.value, … FROM json_each(@u0) j0 JOIN … WHERE true</c>。
    /// <para><b>WHERE true 不是装饰</b>：SQLite 文档要求 INSERT … SELECT … ON CONFLICT 的
    /// SELECT 必须带 WHERE 子句（否则 ON 与 SELECT 的解析歧义直接报语法错）。</para></summary>
    internal static string RewriteSelectUnnest(string sql)
    {
        const string marker = "SELECT * FROM UNNEST(";
        int at = sql.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return sql;

        var parameterNames = new List<string>();
        int cursor = CollectParameterNames(sql, at + marker.Length, ')', parameterNames);
        if (cursor >= sql.Length || parameterNames.Count == 0) return sql;

        var sub = new System.Text.StringBuilder("SELECT j0.value");
        for (int i = 1; i < parameterNames.Count; i++)
            sub.Append(", j").Append(i).Append(".value");
        sub.Append(" FROM json_each(").Append(parameterNames[0]).Append(") j0");
        for (int i = 1; i < parameterNames.Count; i++)
            sub.Append(" JOIN json_each(").Append(parameterNames[i])
                .Append(") j").Append(i).Append(" ON j").Append(i).Append(".key = j0.key");
        sub.Append(" WHERE true");

        return string.Concat(sql[..at], sub.ToString(), sql[(cursor + 1)..]);
    }

    private static string RewriteAny(string sql)
    {
        const string marker = "= ANY(";
        int at = sql.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return sql;

        int nameStart = at + marker.Length;
        int nameEnd = nameStart;
        while (nameEnd < sql.Length
            && (char.IsLetterOrDigit(sql[nameEnd]) || sql[nameEnd] is '@' or '$' or '_'))
        {
            nameEnd++;
        }
        if (nameEnd >= sql.Length || sql[nameEnd] != ')') return sql;

        string parameterName = sql[nameStart..nameEnd];
        return string.Concat(
            sql[..at],
            "IN (SELECT value FROM json_each(", parameterName, "))",
            sql[(nameEnd + 1)..]);
    }

    /// <summary><c>FROM (VALUES (@p0, @p1, @p2), (@p3, …)) AS v(col0, col1, col_pk)</c> →
    /// <c>FROM (SELECT @p0 AS col0, @p1 AS col1, @p2 AS col_pk UNION ALL SELECT @p3, …) v</c>。
    /// <para><b>为什么必须改写</b>：SQLite 不支持 <c>UPDATE … FROM (VALUES …)</c>（PG 的回退形态），
    /// 不改写则回退路径的用例无法在本机跑——而"回退路径行为不变"恰恰是要锁的东西。</para>
    /// <para>UNION ALL 的列名取自首个 SELECT 的别名，故只给第一行写 <c>AS colN</c>。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S3776:CognitiveComplexity",
        Justification = "测试夹具的文本改写：逐行解析 VALUES + 解析别名列名 + 重组 UNION ALL 三步"
            + "是必然复杂度；拆分会引入跨方法的游标状态传递。")]
    private static string RewriteValues(string sql)
    {
        const string marker = "FROM (VALUES ";
        int at = sql.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return sql;
        int rowsStart = at + marker.Length;

        // 逐行解析 (@p…, @p…) 直到遇到 ") AS v("
        var rows = new List<List<string>>();
        int cursor = rowsStart;
        while (cursor < sql.Length && sql[cursor] == '(')
        {
            int rowEnd = sql.IndexOf(')', cursor, StringComparison.Ordinal);
            if (rowEnd < 0) return sql;
            rows.Add([.. sql[(cursor + 1)..rowEnd]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]);
            cursor = rowEnd + 1;
            while (cursor < sql.Length && (sql[cursor] == ',' || sql[cursor] == ' ')) cursor++;
        }
        if (rows.Count == 0) return sql;

        const string asMarker = ") AS v(";
        int asAt = sql.IndexOf(asMarker, cursor, StringComparison.Ordinal);
        if (asAt < 0) return sql;
        int columnsStart = asAt + asMarker.Length;
        int columnsEnd = sql.IndexOf(')', columnsStart, StringComparison.Ordinal);
        if (columnsEnd < 0) return sql;
        string[] columnNames = sql[columnsStart..columnsEnd]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (columnNames.Length != rows[0].Count) return sql;

        var sub = new System.Text.StringBuilder("FROM (");
        for (int r = 0; r < rows.Count; r++)
        {
            sub.Append(r == 0 ? "SELECT " : " UNION ALL SELECT ");
            for (int c = 0; c < rows[r].Count; c++)
            {
                if (c > 0) sub.Append(", ");
                sub.Append(rows[r][c]);
                if (r == 0) sub.Append(" AS ").Append(columnNames[c]);
            }
        }
        sub.Append(") v");

        return string.Concat(sql[..at], sub.ToString(), sql[(columnsEnd + 1)..]);
    }

    /// <summary>从 <paramref name="start"/> 起收集 <c>@name</c>/<c>$name</c> 形式的参数名
    /// （到 <paramref name="stopAt"/> 前），返回收集结束位置。</summary>
    private static int CollectParameterNames(string sql, int start, char stopAt, List<string> names)
    {
        int cursor = start;
        while (cursor < sql.Length && sql[cursor] != stopAt)
        {
            if (sql[cursor] is not ('@' or '$'))
            {
                cursor++;
                continue;
            }
            int nameEnd = cursor;
            while (nameEnd < sql.Length
                && (char.IsLetterOrDigit(sql[nameEnd]) || sql[nameEnd] is '@' or '$' or '_'))
            {
                nameEnd++;
            }
            names.Add(sql[cursor..nameEnd]);
            cursor = nameEnd;
        }
        return cursor;
    }

    /// <summary><c>FROM UNNEST(@u0, @u1, …) AS v(c0, c1, …)</c> → <c>FROM (SELECT j0.value AS c0,
    /// j1.value AS c1, … FROM json_each(@u0) j0 JOIN json_each(@u1) j1 ON j1.key = j0.key …) v</c>。
    /// <para><b>为什么按 key 等值连接而非逗号连接</b>：PG 的 <c>UNNEST(a, b)</c> 按位置把多个数组
    /// 拉平成行集（第 i 行取各数组第 i 个元素，各数组长度必须相等）；SQLite 的 <c>json_each</c>
    /// 行号是 <c>key</c>，故按 <c>key</c> 等值连接才与之一一对应——逗号连接会得到笛卡尔积，
    /// 行数被放大成 N²。</para>
    /// <para>数组长度由 Core 保证相等（同批同长），故连接后行数 = 数组长度，与 PG 逐位对应。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S3776:CognitiveComplexity",
        Justification = "测试夹具的文本改写：定位 UNNEST 子句 + 收集参数 + 解析列名三步是必然复杂度，"
            + "拆分会引入跨方法的游标状态传递。")]
    private static string RewriteUnnest(string sql)
    {
        const string marker = "FROM UNNEST(";
        int at = sql.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return sql;

        var parameterNames = new List<string>();
        int cursor = CollectParameterNames(
            sql, at + marker.Length, ')', parameterNames);
        if (cursor >= sql.Length || parameterNames.Count == 0) return sql;

        const string asMarker = " AS v(";
        int asAt = sql.IndexOf(asMarker, cursor + 1, StringComparison.Ordinal);
        if (asAt < 0) return sql;
        int columnsStart = asAt + asMarker.Length;
        int columnsEnd = sql.IndexOf(')', columnsStart, StringComparison.Ordinal);
        if (columnsEnd < 0) return sql;
        string[] columnNames = sql[columnsStart..columnsEnd]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (columnNames.Length != parameterNames.Count) return sql;

        var sub = new System.Text.StringBuilder("FROM (SELECT ");
        for (int i = 0; i < parameterNames.Count; i++)
        {
            if (i > 0) sub.Append(", ");
            sub.Append('j').Append(i).Append(".value AS ").Append(columnNames[i]);
        }
        sub.Append(" FROM json_each(").Append(parameterNames[0]).Append(") j0");
        for (int i = 1; i < parameterNames.Count; i++)
        {
            sub.Append(" JOIN json_each(").Append(parameterNames[i])
                .Append(") j").Append(i).Append(" ON j").Append(i).Append(".key = j0.key");
        }
        sub.Append(')').Append(" v");

        return string.Concat(
            sql[..at],
            sub.ToString(),
            sql[(columnsEnd + 1)..]);
    }
}

/// <summary>不支持数组参数的夹具——<c>CreateArrayParameter</c> 保持接口默认实现（恒 null），
/// 用于锁 IN 占位符回退路径（能力检测为假时行为必须与优化前逐位一致）。
/// 连接同样经 <see cref="ArrayAnySqliteConnection"/> 包装以采集语句形状——该包装只做
/// <c>= ANY</c> 重写，对 IN 形态逐字节透传（回退路径的形状断言据此成立）。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Generic type argument only — static abstract interface members are never invoked on an instance.")]
internal sealed class NoArraySqliteProvider : IDbProvider
{
    public static string Name => "NoArraySqlite";

    public static SqlDialect Dialect => SqlDialect.Sqlite;

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

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
#pragma warning disable S2077, CA2100 // 表名经 QuoteIdentifier 转义；测试夹具无用户输入
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
#pragma warning restore S2077, CA2100
        return 1;
    }
}

[Table("array_form_rows")]
internal sealed partial class ArrayFormRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";
}

[Table("no_array_rows")]
internal sealed partial class NoArrayRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";
}

[SoftDelete]
[Table("array_form_soft_rows")]
internal sealed partial class ArrayFormSoftRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("deleted_at")]
    public string? DeletedAt { get; set; }
}
