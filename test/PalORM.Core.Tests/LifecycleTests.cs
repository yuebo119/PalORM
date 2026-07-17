using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace PalORM.Core.Tests;

[NotInParallel]
public sealed class LifecycleTests
{
    [Test]
    public async Task CreateAsync_RetriesOnceAndDisposesFailedConnection()
    {
        LifecycleProvider.Reset(false, true);
        var options = new DbOptions
        {
            ConnectionString = "fake",
            MaxRetries = 1,
            RetryBackoff = static _ => TimeSpan.Zero
        };

        await using var session = await DataSession<LifecycleProvider>.CreateAsync(options);

        await Assert.That(LifecycleProvider.Connections.Count).IsEqualTo(2);
        await Assert.That(LifecycleProvider.Connections[0].DisposeCount).IsEqualTo(1);
        await Assert.That(LifecycleProvider.Connections[1].DisposeCount).IsEqualTo(0);
        await session.DisposeAsync();
        await Assert.That(LifecycleProvider.Connections[1].DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task CreateAsync_ConnectionTimeoutIsRetried()
    {
        LifecycleProvider.Reset(null, true);
        var options = new DbOptions
        {
            ConnectionString = "fake",
            ConnectionTimeout = TimeSpan.FromMilliseconds(20),
            MaxRetries = 1,
            RetryBackoff = static _ => TimeSpan.Zero
        };

        await using var session = await DataSession<LifecycleProvider>.CreateAsync(options);

        await Assert.That(LifecycleProvider.Connections.Count).IsEqualTo(2);
        await Assert.That(LifecycleProvider.Connections[0].DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task CreateAsync_CancellationIsNotRetriedAndConnectionIsDisposed()
    {
        LifecycleProvider.Reset((bool?)null);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var options = new DbOptions { ConnectionString = "fake", MaxRetries = 3 };

        await Assert.That(async () => await DataSession<LifecycleProvider>.CreateAsync(options, cancellation.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(LifecycleProvider.Connections.Count).IsEqualTo(1);
        await Assert.That(LifecycleProvider.Connections[0].DisposeCount).IsEqualTo(1);
    }
}

public sealed class LifecycleProvider : IDbProvider
{
    private static readonly Queue<bool?> _openResults = new();
    internal static List<LifecycleConnection> Connections { get; } = [];

    internal static void Reset(params bool?[] openResults)
    {
        _openResults.Clear();
        foreach (bool? result in openResults) _openResults.Enqueue(result);
        Connections.Clear();
    }

    public static string Name => "Lifecycle";
    public static char ParameterPrefix => '@';
    public static SqlDialect Dialect => SqlDialect.Sqlite;
    public static DbConnection CreateConnection(string connectionString) => CreateConnection(connectionString, new DbOptions { ConnectionString = connectionString });
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        var connection = new LifecycleConnection(_openResults.Dequeue());
        Connections.Add(connection);
        return connection;
    }
    public static string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
    public static string QuoteQualifiedIdentifier(string? schema, string identifier) => QuoteIdentifier(identifier);
    public static string GetLimitOffsetClause(int? limit, int? offset) => "";
    public static bool SupportsReturningClause => false;
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";
    public static string GetParameterPlaceholder(int index) => $"@p{index}";
    public static DbParameter CreateParameter(string name, object? value) => throw new NotSupportedException();
    public static bool IsTransient(Exception exception)
        => exception is InvalidOperationException { Message: "open failed" };
    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null) => throw new NotSupportedException();
}

internal sealed class LifecycleConnection(bool? openResult) : DbConnection
{
    private ConnectionState _state;
    internal int DisposeCount { get; private set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = "fake";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "1";
    public override ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => throw new NotSupportedException();
    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (openResult is null) return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (openResult == false) throw new InvalidOperationException("open failed");
        _state = ConnectionState.Open;
        return Task.CompletedTask;
    }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; base.Dispose(disposing); }
}
