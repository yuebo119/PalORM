using System.Data;
using System.Data.Common;

namespace PalORM;

/// <summary>存储过程构建器——链式绑定参数+执行。
/// <para><b>参数工厂</b>: 构造时注入 TProvider.CreateParameter——不再为每个参数创建临时 DbCommand。</para>
/// <para>WithOutputParam: 输出参数在 ExecuteAsync 后通过 GetOutputValue&lt;T&gt;() 读取。</para></summary>
public sealed class StoredProcBuilder
{
    private readonly DbConnection _conn;
    private readonly string _name;
    private readonly TimeSpan _timeout;
    private readonly SessionOperationState _operationState;
    private readonly List<DbParameter> _parameters = [];
    private readonly List<DbParameter> _outputParams = [];

    private readonly Func<string, object?, DbParameter> _paramFactory;

    internal StoredProcBuilder(DbConnection conn, string name, TimeSpan timeout,
        Func<string, object?, DbParameter> paramFactory,
        SessionOperationState operationState)
    {
        _conn = conn;
        _name = name;
        _timeout = timeout;
        _paramFactory = paramFactory;
        _operationState = operationState;
    }

    /// <summary>绑定输入参数。</summary>
    public StoredProcBuilder WithParam(string name, object? value)
    {
        _parameters.Add(_paramFactory(name, value ?? DBNull.Value));
        return this;
    }

    /// <summary>声明输出参数。执行后通过 GetOutputValue&lt;T&gt;(name) 读取。</summary>
    public StoredProcBuilder WithOutputParam<T>(string name)
    {
        DbParameter p = _paramFactory(name, DBNull.Value);
        p.Direction = ParameterDirection.Output;
        // 设置输出参数类型以帮助 ADO.NET provider 正确推断
        if (typeof(T) == typeof(int)) p.DbType = DbType.Int32;
        else if (typeof(T) == typeof(long)) p.DbType = DbType.Int64;
        else if (typeof(T) == typeof(string)) p.DbType = DbType.String;
        else if (typeof(T) == typeof(decimal)) p.DbType = DbType.Decimal;
        else if (typeof(T) == typeof(bool)) p.DbType = DbType.Boolean;
        else if (typeof(T) == typeof(DateTime)) p.DbType = DbType.DateTime;
        else if (typeof(T) == typeof(Guid)) p.DbType = DbType.Guid;
        _outputParams.Add(p);
        _parameters.Add(p);
        return this;
    }

    /// <summary>执行后读取输出参数值。</summary>
    public T? GetOutputValue<T>(string name)
    {
        var p = _outputParams.Find(x => x.ParameterName == name)
            ?? throw new InvalidOperationException($"Output parameter '{name}' not found.");
        return p.Value is DBNull ? default : (T?)p.Value;
    }

    /// <summary>执行并返回结果集。</summary>
    public async ValueTask<List<T>> QueryAsync<T>(CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation =
            _operationState.Enter();
        if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' not registered.");

        await using DbCommand cmd = _conn.CreateCommand();
        cmd.CommandText = _name;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandTimeout = (int)_timeout.TotalSeconds;
        cmd.Transaction = _operationState.GetActiveTransaction();
        foreach (var p in _parameters) cmd.Parameters.Add(p);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<T>();
        IRowFactory<T> tf = (IRowFactory<T>)factory;
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(tf.Read(reader));
        return list;
    }

    /// <summary>执行不返回结果集。</summary>
    public async ValueTask<int> ExecuteAsync(CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation =
            _operationState.Enter();
        await using DbCommand cmd = _conn.CreateCommand();
        cmd.CommandText = _name;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandTimeout = (int)_timeout.TotalSeconds;
        cmd.Transaction = _operationState.GetActiveTransaction();
        foreach (var p in _parameters) cmd.Parameters.Add(p);
        int result = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return result;
    }
}
