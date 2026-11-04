using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>故障类型（FLT-1）。只覆盖三件事：抛异常、拖时间、取消——它们对应 ORM 里
/// 三类真实失效（驱动报错、超时、调用方取消），其余（OOM/磁盘满）属阶段 2 之后的资源类。</summary>
internal enum FaultKind
{
    Exception,
    Delay,
    Cancel,
}

/// <summary>一条注入计划：作用于第 <paramref name="CommandOrdinal"/> 条命令（1 起）。
/// 序号是「命令被创建的顺序」而不是「执行成功的次数」——这样即使前一条命令失败，
/// 后续规则的序号也不会漂移（按执行次数计的话，一次失败会让后面所有规则错位一格）。</summary>
internal sealed record FaultPlan(int CommandOrdinal, FaultKind Kind, Exception? Exception, TimeSpan Delay);

/// <summary>按命令序号注入故障的 <see cref="DbConnection"/> 包装（FLT-1）。
/// <para>设计取舍：包装而不替换——被测代码拿到的是真实的驱动连接，故障只在我们选的
/// 那一层、那一序号上发生，其余行为逐位交给驱动。这样断言的是「产品在驱动异常时的行为」，
/// 不是「产品和假驱动配合得怎么样」。</para></summary>
internal sealed class FaultyConnection : DbConnection
{
    private readonly DbConnection _inner;
    private readonly List<FaultPlan> _plans = [];
    private readonly List<(int CommandOrdinal, int Row, Exception Exception)> _readerFaults = [];
    private int _commandOrdinal;

    internal FaultyConnection(DbConnection inner)
    {
        _inner = inner;
    }

    /// <summary>已创建的命令数（测试用来断言序号真的只作用于目标命令）。</summary>
    internal int CommandsCreated => Volatile.Read(ref _commandOrdinal);

    internal FaultyConnection FailOnCommand(int ordinal, Exception exception)
    {
        _plans.Add(new FaultPlan(ordinal, FaultKind.Exception, exception, default));
        return this;
    }

    internal FaultyConnection DelayOnCommand(int ordinal, TimeSpan delay)
    {
        _plans.Add(new FaultPlan(ordinal, FaultKind.Delay, null, delay));
        return this;
    }

    internal FaultyConnection CancelOnCommand(int ordinal)
    {
        _plans.Add(new FaultPlan(ordinal, FaultKind.Cancel, null, default));
        return this;
    }

    /// <summary>让该命令返回的 reader 在读到第 <paramref name="row"/> 行时报错
    /// （1 起：1 = 第一次 Read 就失败）。</summary>
    internal FaultyConnection FailReaderAtRow(int commandOrdinal, int row, Exception exception)
    {
        _readerFaults.Add((commandOrdinal, row, exception));
        return this;
    }

    internal IReadOnlyList<FaultPlan> Plans => _plans;

    /// <summary>底层真实连接。命令包装把 <see cref="DbConnection"/> 属性回设时必须换成它——
    /// 驱动只认自己的连接实例，把包装层交给底层命令会让 Open/事务状态判定全部错位。</summary>
    internal DbConnection InnerConnection => _inner;

    internal (int CommandOrdinal, int Row, Exception Exception)? ReaderFaultFor(int ordinal)
        => _readerFaults.FirstOrDefault(f => f.CommandOrdinal == ordinal) is var fault && fault.CommandOrdinal > 0
            ? fault
            : null;

    protected override DbCommand CreateDbCommand()
        => new FaultyCommand(
            this,
            _inner.CreateCommand(),
            Interlocked.Increment(ref _commandOrdinal),
            _plans);

    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    public override string Database => _inner.Database;

    public override string DataSource => _inner.DataSource;

    public override string ServerVersion => _inner.ServerVersion;

    public override ConnectionState State => _inner.State;

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

    public override void Close() => _inner.Close();

    public override void Open() => _inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => _inner.BeginTransaction(isolationLevel);

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

/// <summary>命令层包装：执行前按序号查计划，命中就注入。</summary>
internal sealed class FaultyCommand : DbCommand
{
    private readonly FaultyConnection _owner;
    private readonly DbCommand _inner;
    private readonly int _ordinal;
    private readonly IReadOnlyList<FaultPlan> _plans;

    internal FaultyCommand(FaultyConnection owner, DbCommand inner, int ordinal, IReadOnlyList<FaultPlan> plans)
    {
        _owner = owner;
        _inner = inner;
        _ordinal = ordinal;
        _plans = plans;
    }

    internal int Ordinal => _ordinal;

    private FaultPlan? Plan => _plans.FirstOrDefault(p => p.CommandOrdinal == _ordinal);

    private async ValueTask ApplyFaultAsync(CancellationToken cancellationToken)
    {
        if (Plan is not { } plan) return;
        switch (plan.Kind)
        {
            case FaultKind.Exception:
                throw plan.Exception!;
            case FaultKind.Delay:
                await Task.Delay(plan.Delay, cancellationToken).ConfigureAwait(false);
                break;
            case FaultKind.Cancel:
                throw new OperationCanceledException("注入：命令被取消（FLT-1）");
            default:
                break;
        }
    }

    [AllowNull]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "测试夹具：命令文本由被测代码原样透传，本类不构造 SQL")]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _inner.Connection;
        set => _inner.Connection = value is FaultyConnection faulty ? faulty.InnerConnection : value;
    }

    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    [AllowNull]
    protected override DbTransaction? DbTransaction
    {
        get => _inner.Transaction;
        set => _inner.Transaction = value;
    }

    public override void Cancel() => _inner.Cancel();

    public override void Prepare() => _inner.Prepare();

    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    public override int ExecuteNonQuery()
    {
        ApplyFaultAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return _inner.ExecuteNonQuery();
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        await ApplyFaultAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override object? ExecuteScalar()
    {
        ApplyFaultAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return _inner.ExecuteScalar();
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await ApplyFaultAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        ApplyFaultAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return Wrap(_inner.ExecuteReader(behavior));
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
    {
        await ApplyFaultAsync(cancellationToken).ConfigureAwait(false);
        DbDataReader reader = await _inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
        return Wrap(reader);
    }

    private DbDataReader Wrap(DbDataReader reader)
    {
        var fault = _owner.ReaderFaultFor(_ordinal);
        return fault is { } f ? new FaultyReader(reader, f.Row, f.Exception) : reader;
    }

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
