using System.Data.Common;

namespace PalORM;

/// <summary>编译查询计划（M3，2026-10-31 极致性能轮）——把 SQL 文本与参数模板固定为会话级对象，
/// 执行时只写参数值、复用命令、物化结果。适用于同形状、稳定参数类型的重复查询。
/// <para><b>与 Fluent API 的分工</b>：Fluent 路径每次调用重建子句链、形状哈希与参数对象；
/// 编译计划把这三步从每调用一次降为每计划一次。计划是<b>会话级</b>的（命令绑定连接），
/// 租约内串行执行；池化用法下每次租入会话后重新编译（首次创建命令与参数，后续租期复用
/// 同一计划对象时可继续命中）。</para>
/// <para><b>契约</b>：① SQL 逐字执行，默认过滤（软删/租户）不适用（与 QueryAsync 同口径）；
/// ② 参数按位置绑定，名称必须与 SQL 中的占位符一致；③ 参数运行时类型必须稳定——类型变化
/// 会明确失败而非静默换绑（PG auto-prepare 下更安全）；④ 计划不被会话自动释放，
/// 调用方须在会话释放前 Dispose（或随会话作用域结束一并释放）；⑤ 不支持并行读作用域
/// 与读路由，计划始终使用主连接。</para></summary>
/// <typeparam name="T">实体或投影类型（需经源生成器注册行工厂）。</typeparam>
/// <typeparam name="TProvider">数据库 Provider 类型。</typeparam>
public sealed class CompiledQuery<T, TProvider> : IDisposable
    where T : class, new()
    where TProvider : IDbProvider
{
    private readonly DataSession<TProvider> _session;
    private readonly string _sql;
    private readonly string[] _parameterNames;
    private DbCommand? _command;
    private DbParameter[]? _parameters;
    private DbTransaction? _appliedTransaction;
    private int _appliedTimeoutSeconds = int.MinValue;

    internal CompiledQuery(DataSession<TProvider> session, string sql, string[] parameterNames)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(parameterNames);

        for (int i = 0; i < parameterNames.Length; i++)
        {
            string name = parameterNames[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name[0] != '@')
                throw new ArgumentException(
                    $"Compiled query parameter name '{name}' must start with '@'.", nameof(parameterNames));
            for (int j = 0; j < i; j++)
            {
                if (string.Equals(parameterNames[j], name, StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"Compiled query parameter name '{name}' is declared more than once.", nameof(parameterNames));
            }
        }

        _session = session;
        _sql = sql;
        _parameterNames = (string[])parameterNames.Clone();
    }

    /// <summary>计划执行的 SQL 文本（逐字）。</summary>
    public string Sql => _sql;

    /// <summary>按位置绑定参数并执行，返回物化列表。参数个数必须等于声明个数；
    /// 同一参数位在多次执行间的运行时类型必须一致（首次执行建立参数对象，之后只写 Value）。</summary>
    /// <param name="values">参数值（按声明顺序；null 映射为 DBNull）。</param>
    /// <param name="ct">取消令牌。</param>
    public async ValueTask<List<T>> ExecuteAsync(object?[] values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != _parameterNames.Length)
            throw new ArgumentException(
                $"Compiled query expects {_parameterNames.Length} parameter value(s) but received {values.Length}.",
                nameof(values));

        using SessionOperationState.SessionOperationLease operation = _session.EnterReadOnly();
        DbCommand command = EnsureCommand();
        DbTransaction? transaction = _session.CompiledTransaction;
        if (!ReferenceEquals(_appliedTransaction, transaction))
        {
            command.Transaction = transaction;
            _appliedTransaction = transaction;
        }
        int timeoutSeconds = _session.CompiledTimeoutSeconds;
        if (_appliedTimeoutSeconds != timeoutSeconds)
        {
            command.CommandTimeout = timeoutSeconds;
            _appliedTimeoutSeconds = timeoutSeconds;
        }

        DbParameter[] parameters = _parameters!;
        for (int i = 0; i < values.Length; i++)
        {
            object value = values[i] ?? DBNull.Value;
            object? existing = parameters[i].Value;
            if (existing is not null and not DBNull && value is not DBNull
                && existing.GetType() != value.GetType())
            {
                throw new InvalidOperationException(
                    $"Compiled query parameter '{_parameterNames[i]}' was first bound as " +
                    $"'{existing.GetType().Name}' and cannot be rebound as '{value.GetType().Name}'. " +
                    "Compile a separate plan for each runtime parameter type.");
            }
            parameters[i].Value = value;
        }

        await using DbDataReader reader =
            await _session.ExecuteReaderWithPoisonAsync(command, ct).ConfigureAwait(false);
        List<T> list = await ResultListReader.ReadAllAsync(
            reader, DataSession<TProvider>.GetRowFactory<T>(), _session.CompiledCapacity<T>(), ct,
            syncRead: _session.CompiledSyncRowRead).ConfigureAwait(false);
        _session.CompiledRecordCount<T>(list.Count);
        return list;
    }

    private DbCommand EnsureCommand()
    {
        if (_command is not null)
        {
            // C1：连接重建后旧命令绑定在已释放的连接上，必须失效重建
            if (ReferenceEquals(_command.Connection, _session.BatchConnection))
                return _command;
            _command.Dispose();
            _command = null;
            _parameters = null;
        }

        DbCommand command = _session.CreateCompiledCommand();
        command.CommandText = _sql;
        var parameters = new DbParameter[_parameterNames.Length];
        for (int i = 0; i < _parameterNames.Length; i++)
        {
            DbParameter parameter = TProvider.CreateParameter(_parameterNames[i], DBNull.Value);
            command.Parameters.Add(parameter);
            parameters[i] = parameter;
        }
        _command = command;
        _parameters = parameters;
        return command;
    }

    /// <summary>释放计划持有的命令。计划不被会话自动释放，须在会话释放前调用。</summary>
    public void Dispose()
    {
        _command?.Dispose();
        _command = null;
        _parameters = null;
        GC.SuppressFinalize(this);
    }
}
