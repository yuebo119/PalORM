using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace PalORM.Core.Tests;

/// <summary>v5.4 弹性管线接入测试（评审 P1-a）。
/// 证明 WithRetry/WithCircuitBreaker 在只读内置管线真实生效，并锁定三类边界：
/// 事务内读取不重试、写入路径不重试、零配置直通零重试。</summary>
public sealed class ResiliencePipelineTests
{
    private static DbOptions FlakyOptions(int maxRetries, int circuitBreakerThreshold) => new()
    {
        ConnectionString = "flaky",
        MaxRetries = maxRetries,
        CircuitBreakerThreshold = circuitBreakerThreshold,
        // 排除退避等待对测试时长的影响
        RetryBackoff = static _ => TimeSpan.Zero
    };

    // 连接所有权移交 DataSession（会话 DisposeAsync 级联释放）——经返回值转移避免 CA2000
    private static (FlakyConnection Connection, DataSession<FlakyProvider> Session) CreateSession(
        DbOptions options)
    {
        var connection = new FlakyConnection();
        return (connection, new DataSession<FlakyProvider>(connection, options, []));
    }

    [Test]
    public async Task ReadQuery_TransientFailure_RetriedUntilSuccess()
    {
        var (connection, session) = CreateSession(FlakyOptions(maxRetries: 3, circuitBreakerThreshold: 0));
        connection.FailFirstNReads = 1;
        await using var _ = session;

        List<FlakyEntity> rows = await session.From<FlakyEntity>().ToListAsync();

        // 第一次尝试瞬时失败，第二次成功——重试真实发生
        await Assert.That(connection.ReaderAttempts).IsEqualTo(2);
        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task ReadQuery_ExhaustedRetries_ThrowsLastTransient()
    {
        var (connection, session) = CreateSession(FlakyOptions(maxRetries: 1, circuitBreakerThreshold: 0));
        connection.FailFirstNReads = int.MaxValue;
        await using var _ = session;

        await Assert.That(async () => await session.From<FlakyEntity>().ToListAsync())
            .Throws<FlakyTransientException>();
        // 1 次初始尝试 + 1 次重试 = 2 次后耗尽
        await Assert.That(connection.ReaderAttempts).IsEqualTo(2);
    }

    [Test]
    public async Task CircuitBreaker_Opens_AfterConsecutiveReadFailures()
    {
        var (connection, session) = CreateSession(FlakyOptions(maxRetries: 0, circuitBreakerThreshold: 1));
        connection.FailFirstNReads = int.MaxValue;
        await using var _ = session;

        await Assert.That(async () => await session.From<FlakyEntity>().ToListAsync())
            .Throws<FlakyTransientException>();
        // 熔断开启后直接快速失败——不再触达数据库
        await Assert.That(async () => await session.From<FlakyEntity>().ToListAsync())
            .Throws<CircuitBreakerOpenException>();
        await Assert.That(connection.ReaderAttempts).IsEqualTo(1);
    }

    [Test]
    public async Task InTransaction_Read_IsNotRetried()
    {
        // 边界锁定：事务内语句失败后重试会以次生异常掩盖根因（如 PG aborted transaction），
        // 且跨尝试快照语义不成立——事务内读取保持直连，即使配置了重试
        var (connection, session) = CreateSession(FlakyOptions(maxRetries: 3, circuitBreakerThreshold: 0));
        connection.FailFirstNReads = int.MaxValue;
        await using var _ = session;
        await using DbTransaction transaction = await session.BeginTransactionAsync();
        session.UseTransaction(transaction);

        await Assert.That(async () => await session.From<FlakyEntity>().ToListAsync())
            .Throws<FlakyTransientException>();
        await Assert.That(connection.ReaderAttempts).IsEqualTo(1);
    }

    [Test]
    public async Task WritePath_ExecuteNonQuery_IsNotRetried()
    {
        // 边界锁定：非幂等写不自动重试（ResilienceExecutor ITM-310 幂等性契约）——
        // 显式弹性需求用 ExecuteWithResilience 包裹
        var (connection, session) = CreateSession(FlakyOptions(maxRetries: 3, circuitBreakerThreshold: 0));
        connection.FailEveryNonQuery = true;
        await using var _ = session;

        await Assert.That(async () => await session.From<FlakyEntity>()
                .Set(entity => entity.Name, "updated")
                .ExecuteNonQueryAsync())
            .Throws<FlakyTransientException>();
        await Assert.That(connection.NonQueryAttempts).IsEqualTo(1);
    }

    [Test]
    public async Task ZeroConfig_Read_FailsFastWithoutRetry()
    {
        // 直通契约：MaxRetries=0 且熔断禁用（Testing 预设形态）时零重试，
        // 瞬时故障立即暴露——测试确定性优先
        var (connection, session) = CreateSession(FlakyOptions(maxRetries: 0, circuitBreakerThreshold: 0));
        connection.FailFirstNReads = 1;
        await using var _ = session;

        await Assert.That(async () => await session.From<FlakyEntity>().ToListAsync())
            .Throws<FlakyTransientException>();
        await Assert.That(connection.ReaderAttempts).IsEqualTo(1);
    }

    [Test]
    public async Task AggregateCount_TransientFailure_RetriedUntilSuccess()
    {
        var (connection, session) = CreateSession(FlakyOptions(maxRetries: 3, circuitBreakerThreshold: 0));
        connection.FailFirstNScalars = 1;
        await using var _ = session;

        long count = await session.CountAsync<FlakyEntity>();

        await Assert.That(connection.ScalarAttempts).IsEqualTo(2);
        await Assert.That(count).IsEqualTo(7L);
    }
}

/// <summary>瞬时故障标记异常——FlakyProvider 据此判定可重试。</summary>
public sealed class FlakyTransientException : Exception
{
    public FlakyTransientException() : base("flaky transient failure") { }
    public FlakyTransientException(string message) : base(message) { }
    public FlakyTransientException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>测试用 Provider——仅 IsTransient 覆盖为识别 FlakyTransientException。</summary>
public sealed class FlakyProvider : IDbProvider
{
    public static string Name => "Flaky";
    public static SqlDialect Dialect => SqlDialect.Sqlite;
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
        => new FlakyConnection();
    public static string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => QuoteIdentifier(identifier);
    public static bool SupportsReturningClause => true;
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";
    public static DbParameter CreateParameter(string name, object? value)
        => new FlakyParameter { ParameterName = name, Value = value };
    public static bool IsTransient(Exception exception) => exception is FlakyTransientException;
    public static int ConfigureSchemaCommand(
        DbCommand command, string tableName, string? schema = null)
        => throw new NotSupportedException();
}

public sealed class FlakyConnection : DbConnection
{
    internal int ReaderAttempts { get; private set; }
    internal int ScalarAttempts { get; private set; }
    internal int NonQueryAttempts { get; private set; }
    internal int FailFirstNReads { get; set; }
    internal int FailFirstNScalars { get; set; }
    internal bool FailEveryNonQuery { get; set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = "flaky";
    public override string Database => "flaky";
    public override string DataSource => "flaky";
    public override string ServerVersion => "1";
    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => new FlakyTransaction(this);
    protected override DbCommand CreateDbCommand() => new FlakyCommand(this);

    internal void CountReaderAttempt()
    {
        ReaderAttempts++;
        if (ReaderAttempts <= FailFirstNReads) throw new FlakyTransientException();
    }

    internal object? CountScalarAttempt()
    {
        ScalarAttempts++;
        if (ScalarAttempts <= FailFirstNScalars) throw new FlakyTransientException();
        return 7L;
    }

    internal int CountNonQueryAttempt()
    {
        NonQueryAttempts++;
        if (FailEveryNonQuery) throw new FlakyTransientException();
        return 0;
    }
}

internal sealed class FlakyTransaction(DbConnection connection) : DbTransaction
{
    private bool _completed;

    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => _completed ? null : connection;

    public override void Commit() => _completed = true;
    public override void Rollback() => _completed = true;
    public override ValueTask DisposeAsync()
    {
        // 与 ConcurrencyTransaction 同口径：Dispose 即断开与连接的关联（Connection 置空）
        _completed = true;
        return base.DisposeAsync();
    }
}

internal sealed class FlakyCommand(FlakyConnection connection) : DbCommand
{
    private readonly FlakyParameterCollection _parameters = [];

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; } = connection;
    protected override DbTransaction? DbTransaction { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;

    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new FlakyParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => throw new NotSupportedException();

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
    {
        connection.CountReaderAttempt();
        return Task.FromResult<DbDataReader>(new FlakyReader());
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        => Task.FromResult(connection.CountScalarAttempt());

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        => Task.FromResult(connection.CountNonQueryAttempt());
}

internal sealed class FlakyReader : DbDataReader
{
    public override int FieldCount => 0;
    public override bool HasRows => false;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int Depth => 0;
    public override object this[int ordinal] => throw new NotSupportedException();
    public override object this[string name] => throw new NotSupportedException();

    public override bool Read() => false;
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();
    public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
    public override double GetDouble(int ordinal) => throw new NotSupportedException();
    public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override Type GetFieldType(int ordinal) => throw new NotSupportedException();
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override int GetInt32(int ordinal) => throw new NotSupportedException();
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
    public override string GetName(int ordinal) => throw new NotSupportedException();
    public override int GetOrdinal(string name) => throw new NotSupportedException();
    public override string GetString(int ordinal) => throw new NotSupportedException();
    public override object GetValue(int ordinal) => throw new NotSupportedException();
    public override int GetValues(object[] values) => throw new NotSupportedException();
    public override bool IsDBNull(int ordinal) => throw new NotSupportedException();
}

public sealed class FlakyParameter : DbParameter
{
    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;
    public override object? Value { get; set; }
    public override int Size { get; set; }
    public override byte Precision { get; set; }
    public override byte Scale { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;
    public override DataRowVersion SourceVersion { get; set; }
    public override DbType DbType { get; set; }
    public override void ResetDbType() { }
}

internal sealed class FlakyParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _parameters = [];

    public override int Count => _parameters.Count;
    public override object SyncRoot => ((System.Collections.ICollection)_parameters).SyncRoot;

    public override int Add(object value)
    {
        _parameters.Add((DbParameter)value);
        return _parameters.Count - 1;
    }
    public override void AddRange(Array values)
    {
        foreach (object value in values) Add(value);
    }
    public override void Clear() => _parameters.Clear();
    public override bool Contains(object value) => _parameters.Contains(value);
    public override bool Contains(string value) => _parameters.Any(p => p.ParameterName == value);
    public override void CopyTo(Array array, int index) => throw new NotSupportedException();
    public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();
    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName)
        => _parameters.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _parameters.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _parameters.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _parameters.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => _parameters[index];
    protected override DbParameter GetParameter(string parameterName) => _parameters[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value)
        => _parameters[IndexOf(parameterName)] = value;
}

#region Test Entity

[Table("flaky_entities")]
internal sealed partial class FlakyEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = string.Empty;
}

#endregion
