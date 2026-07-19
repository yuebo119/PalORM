using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace PalORM.Core.Tests;

public sealed class QueryExecutionTests
{
    [Test]
    public async Task PrepareCommandAsync_Disabled_DoesNotPrepare()
    {
        await using var command = new PrepareTrackingCommand();

        await QueryBuilderExtensions.PrepareCommandAsync(command, false, CancellationToken.None);

        await Assert.That(command.PrepareCount).IsEqualTo(0);
    }

    [Test]
    public async Task PrepareCommandAsync_Enabled_PreparesAfterParametersAreBound()
    {
        await using var command = new PrepareTrackingCommand();
        command.Parameters.Add(new PrepareTrackingParameter());
        using var cancellation = new CancellationTokenSource();

        await QueryBuilderExtensions.PrepareCommandAsync(command, true, cancellation.Token);

        await Assert.That(command.PrepareCount).IsEqualTo(1);
        await Assert.That(command.ParameterCountAtPrepare).IsEqualTo(1);
        await Assert.That(command.PrepareToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    public async Task PrepareCommandAsync_Enabled_PropagatesCancellation()
    {
        await using var command = new PrepareTrackingCommand();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await QueryBuilderExtensions.PrepareCommandAsync(command, true, cancellation.Token));
    }

    [Test]
    public async Task FormatFormattableSql_ValidCompositeFormat_PreservesParameterIdentity()
    {
        object?[] arguments =
        [
            .. Enumerable.Range(0, 11).Select(index => (object?)index)
        ];
        FormattableString sql = FormattableStringFactory.Create(
            "{{json}} {{{0:N1}}} = {0} AND value = {10}",
            arguments);

        string formatted = QueryBuilder<object>.FormatFormattableSql(sql, 3);

        await Assert.That(formatted)
            .IsEqualTo("{json} {@p3} = @p3 AND value = @p13");
    }

    // ITM-546：SQL 文本中的字面 @p<n>（邮箱/LIKE 模式等）不得被误拒——原样透传。
    [Test]
    [Arguments("SELECT * FROM t WHERE email = 'a@p1.com'")]
    [Arguments("SELECT * FROM t WHERE note LIKE '%@p2%'")]
    [Arguments("SELECT * FROM t WHERE x = '@p0'")]
    public async Task FormatFormattableSql_LiteralAtPInText_IsPreservedNotRejected(string format)
    {
        FormattableString sql = FormattableStringFactory.Create(format);

        string formatted = QueryBuilder<object>.FormatFormattableSql(sql, 0);

        // 无插值项 → 原样返回，字面 @pN 保留
        await Assert.That(formatted).IsEqualTo(format);
    }

    [Test]
    [Arguments("SELECT {0,abc}")]
    [Arguments("SELECT {0:format")]
    [Arguments("SELECT value }")]
    [Arguments("SELECT {1}")]
    public async Task FormatFormattableSql_InvalidCompositeFormat_ThrowsBeforeExecution(
        string format)
    {
        FormattableString sql = FormattableStringFactory.Create(format, 1);

        await Assert.That(() => QueryBuilder<object>.FormatFormattableSql(sql, 0))
            .Throws<FormatException>();
    }
}

internal sealed class PrepareTrackingCommand : DbCommand
{
    private readonly PrepareTrackingParameterCollection _parameters = [];

    internal int PrepareCount { get; private set; }
    internal int ParameterCountAtPrepare { get; private set; }
    internal CancellationToken PrepareToken { get; private set; }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() => throw new NotSupportedException();
    protected override DbParameter CreateDbParameter() => new PrepareTrackingParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

    public override Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PrepareCount++;
        ParameterCountAtPrepare = Parameters.Count;
        PrepareToken = cancellationToken;
        return Task.CompletedTask;
    }
}

internal sealed class PrepareTrackingParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;
    public override int Size { get; set; }
    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override void ResetDbType() { }
}

internal sealed class PrepareTrackingParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot;
    public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
    public override void AddRange(Array values) { foreach (object value in values) Add(value); }
    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains((DbParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => _items.FindIndex(parameter => parameter.ParameterName == parameterName);
    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        int index = IndexOf(parameterName);
        if (index < 0) _items.Add(value); else _items[index] = value;
    }
}
