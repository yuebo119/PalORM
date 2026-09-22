using System.Data;
using System.Data.Common;

namespace PalORM.PerfHub;

/// <summary>维度 8（往返与语句效率）的计数器：包一层 <see cref="DbConnection"/> 与
/// <see cref="DbCommand"/>，统计每操作的**往返次数**（命令执行次数）与 **prepared 复用率**。
/// <para><b>为什么装饰连接而不是按构造声明</b>：三臂的命令创建路径不同（ADO 臂自建、Dapper 在
/// 传入连接上 CreateCommand、PalORM 会话在调用方连接上 CreateCommand），只有包住连接才能对三臂
/// 用同一把尺子量；而且它能抓到"意外 N+1"（Dapper 的 multi-exec 会把 N 次执行如实计入）。</para>
/// <para><b>只计数不改语义</b>：命令、参数集合、读取器全部原样返回内部对象；唯一的包装是
/// 命令的 Execute* 与 Prepare 三个入口。</para>
/// <para>计数口径：一次逻辑执行 = 一次 <c>ExecuteReader</c>/<c>ExecuteNonQuery</c>；
/// <c>ExecuteScalar</c> 经基类走 ExecuteReader，只记一次（不重复计）。</para></summary>
internal sealed class CountingConnection(DbConnection inner) : DbConnection
{
    private readonly DbConnection _inner = inner;
    private long _executes;
    private long _prepares;

    /// <summary>命令执行次数（自上次 <see cref="Reset"/> 起）。并发路径下由多线程累加，
    /// 故走 Interlocked（并发项每线程一条被包装的连接，读取时跨线程求和）。</summary>
    public long Executes => Interlocked.Read(ref _executes);

    /// <summary>Prepare 调用次数（自上次 <see cref="Reset"/> 起）。</summary>
    public long Prepares => Interlocked.Read(ref _prepares);

    /// <summary>清零计数——每个测量项开始时调用（须在无并发访问时调用）。</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _executes, 0);
        Interlocked.Exchange(ref _prepares, 0);
    }

    /// <summary>prepared 复用率：Prepare 次数 / 执行次数（0 表示从不 prepare）。</summary>
    public double PreparedReuse => Executes == 0 ? 0 : (double)Prepares / Executes;

    internal void CountExecute() => Interlocked.Increment(ref _executes);

    internal void CountPrepare() => Interlocked.Increment(ref _prepares);

    [System.Diagnostics.CodeAnalysis.AllowNull]
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

    public override Task CloseAsync() => _inner.CloseAsync();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => _inner.BeginTransaction(isolationLevel);

    protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel, CancellationToken cancellationToken)
        => _inner.BeginTransactionAsync(isolationLevel, cancellationToken);

    protected override DbCommand CreateDbCommand() => new CountingCommand(_inner.CreateCommand(), this);

    public override int ConnectionTimeout => _inner.ConnectionTimeout;

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

    /// <summary>命令包装：只拦 Execute* 与 Prepare 三个入口。</summary>
    private sealed class CountingCommand(DbCommand inner, CountingConnection owner) : DbCommand
    {
        private readonly DbCommand _inner = inner;
        private readonly CountingConnection _owner = owner;

        [System.Diagnostics.CodeAnalysis.AllowNull]
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
            get => _owner;
            set => _ = value;   // 连接由 owner 持有；显式丢弃赋值以保持计数归属
        }

        protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        protected override DbTransaction? DbTransaction
        {
            get => _inner.Transaction;
            set => _inner.Transaction = value;
        }

        public override void Cancel() => _inner.Cancel();

        public override int ExecuteNonQuery()
        {
            _owner.CountExecute();
            return _inner.ExecuteNonQuery();
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            _owner.CountExecute();
            return _inner.ExecuteNonQueryAsync(cancellationToken);
        }

        public override object? ExecuteScalar()
        {
            _owner.CountExecute();
            return _inner.ExecuteScalar();
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            _owner.CountExecute();
            return _inner.ExecuteScalarAsync(cancellationToken);
        }

        public override void Prepare()
        {
            _owner.CountPrepare();
            _inner.Prepare();
        }

        public override Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            _owner.CountPrepare();
            return _inner.PrepareAsync(cancellationToken);
        }

        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            _owner.CountExecute();
            return _inner.ExecuteReader(behavior);
        }

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior, CancellationToken cancellationToken)
        {
            _owner.CountExecute();
            return _inner.ExecuteReaderAsync(behavior, cancellationToken);
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
}
