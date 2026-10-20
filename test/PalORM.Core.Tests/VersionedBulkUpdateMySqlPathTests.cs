using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace PalORM.Core.Tests;

/// <summary>版本化单语句的 MySQL 内核覆盖（ADR-P JoinValuesRow 分支）。
/// <para>夹具报 MySQL 方言 + 版本探测返回 8.4.11，连接为脚本化假实现：不执行 SQL，
/// 按语句内 <c>ROW(</c> 数量返回受影响行数。SQL 形状与参数布局由本用例锁定，
/// 真实执行由 Integration.Tests 的 MySqlBatchedUpdatePathTests 覆盖。</para></summary>
internal sealed class VersionedBulkUpdateMySqlPathTests
{
    [Test]
    public async Task BulkUpdate_VersionedEntity_JoinValuesRow_CountsAndIncrements()
    {
        using var connection = new ScriptedMySqlConnection();
        await using var session = new DataSession<ScriptedMySqlProvider>(
            connection, new DbOptions { ConnectionString = "scripted-versioned" }, []);

        var rows = new List<ConcurrencyRow>();
        for (int i = 1; i <= 5; i++)
            rows.Add(new ConcurrencyRow { Id = i, Name = $"m{i}", Version = 0 });

        long affected = await session.BulkUpdateAsync(rows);

        await Assert.That(affected).IsEqualTo(5);
        await Assert.That(rows.All(static r => r.Version == 1)).IsTrue();
        await Assert.That(connection.Statements.Count).IsEqualTo(1);
        string sql = connection.Statements[0];
        await Assert.That(sql).Contains("JOIN (VALUES ROW(");
        await Assert.That(sql).Contains("`version` = v.ver");
        await Assert.That(sql).Contains("`version` = tgt.`version` + 1");
        // 每行 SET 列(1) + 主键(1) + 版本(1) = 3 个参数
        await Assert.That(connection.ParameterCounts[^1]).IsEqualTo(15);
    }

    [Test]
    public async Task BulkUpdate_VersionedEntity_JoinValuesRow_PartialMatchThrowsAndKeepsVersions()
    {
        using var connection = new ScriptedMySqlConnection { ForcedAffectedRows = 4 };
        await using var session = new DataSession<ScriptedMySqlProvider>(
            connection, new DbOptions { ConnectionString = "scripted-versioned-conflict" }, []);

        var rows = new List<ConcurrencyRow>();
        for (int i = 1; i <= 5; i++)
            rows.Add(new ConcurrencyRow { Id = i, Name = $"c{i}", Version = 0 });

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            async () => await session.BulkUpdateAsync(rows));

        await Assert.That(rows.All(static r => r.Version == 0)).IsTrue();
    }
}

internal sealed class ScriptedMySqlConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;

    [AllowNull]
    public override string ConnectionString { get; set; } = "scripted";
    public override string Database => "scripted";
    public override string DataSource => "scripted";
    public override string ServerVersion => "8.4.11";
    public override ConnectionState State => _state;
    internal List<string> Statements { get; } = [];
    internal List<int> ParameterCounts { get; } = [];
    internal int? ForcedAffectedRows { get; init; }

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => new ScriptedTransaction(this);

    protected override DbCommand CreateDbCommand() => new ScriptedCommand(this);

    internal int RecordAndGetAffected(string sql, int parameterCount)
    {
        Statements.Add(sql);
        ParameterCounts.Add(parameterCount);
        if (ForcedAffectedRows is { } forced) return forced;
        int rows = 0;
        int index = 0;
        while ((index = sql.IndexOf("ROW(", index, StringComparison.Ordinal)) >= 0)
        {
            rows++;
            index += 4;
        }
        return rows == 0 ? 1 : rows;
    }
}

internal sealed class ScriptedTransaction(DbConnection connection) : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => connection;
    public override void Commit() { }
    public override void Rollback() { }
}

internal sealed class ScriptedCommand(ScriptedMySqlConnection connection) : DbCommand
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
    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(default).GetAwaiter().GetResult();
    public override object? ExecuteScalar() => ExecuteScalarAsync(default).GetAwaiter().GetResult();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new FlakyParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => throw new NotSupportedException();

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        => Task.FromResult(connection.RecordAndGetAffected(CommandText, _parameters.Count));

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        => Task.FromResult<object?>(CommandText.Contains("VERSION()", StringComparison.OrdinalIgnoreCase)
            ? "8.4.11"
            : null);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Generic type argument only — static abstract interface members are never invoked on an instance.")]
internal sealed class ScriptedMySqlProvider : IDbProvider
{
    public static string Name => "ScriptedMySql";
    public static SqlDialect Dialect => SqlDialect.MySql;
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
        => new ScriptedMySqlConnection();
    public static string QuoteIdentifier(string identifier)
        => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";
    public static bool SupportsReturningClause => false;
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";
    public static DbParameter CreateParameter(string name, object? value)
        => new FlakyParameter { ParameterName = name, Value = value ?? DBNull.Value };
    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
        => 0;
}
