using System.Data;
using System.Data.Common;

namespace PalORM;

/// <summary>存储过程构建器——链式绑定参数+执行。
/// <para><b>参数工厂</b>: 构造时注入 TProvider.CreateParameter——不再为每个参数创建临时 DbCommand。</para>
/// <para>WithOutputParam: 输出参数在 ExecuteAsync 后通过 GetOutputValue&lt;T&gt;() 读取。</para>
/// <para>ITM-582: 存储过程执行<b>不经过</b> IQueryInterceptor 与 WithTracing/WithMetrics
/// （与 QueryMultipleAsync 的 ITM-548 同类边界）——完整审计请用数据库层审计。</para>
/// <para><b>线程安全</b>（ITM-610）：<c>StoredProcBuilder</c> 实例<b>非线程安全</b>——
/// <c>_executed</c> 标志是裸 bool 无锁保护，同实例并发调用 <c>QueryAsync</c> 有竞态。
/// 使用契约是"一个 builder 一次异步链"：构造→绑定→执行→读取→丢弃。跨异步流复用请构造新实例。</para></summary>
public sealed class StoredProcBuilder
{
    private readonly DbConnection _conn;
    private readonly string _name;
    private readonly TimeSpan _timeout;
    private readonly SessionOperationState _operationState;
    private readonly List<DbParameter> _parameters = [];
    private readonly List<DbParameter> _outputParams = [];
    private bool _executed;
    private readonly bool _validateColumnOrder;

    private readonly Func<string, object?, DbParameter> _paramFactory;

    internal StoredProcBuilder(DbConnection conn, string name, TimeSpan timeout,
        Func<string, object?, DbParameter> paramFactory,
        SessionOperationState operationState,
        bool validateColumnOrder = false)
    {
        _validateColumnOrder = validateColumnOrder;
        _conn = conn;
        _name = ValidateProcedureName(name);
        _timeout = timeout;
        _paramFactory = paramFactory;
        _operationState = operationState;
    }

    /// <summary>过程名白名单：字母/下划线开头，仅含字母数字、下划线和点分限定符。
    /// 过程名直接进入 CommandText（CommandType.StoredProcedure），纵深防御拒绝特殊字符。
    /// <para>D4（2026-10-01 全 API 逐项轮）：span 扫描替代 <c>Split('.')</c> + LINQ Any——
    /// 原实现每次 <c>StoredProc()</c> 构造 1 个分段数组 + 每段一个 LINQ 委托/迭代器。
    /// 分段与首字符/余下字符判定语义与原实现逐位一致（首字符已由前判定保证合法，
    /// 全段 Any 等价于索引 1 起扫描）。</para></summary>
    private static string ValidateProcedureName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ReadOnlySpan<char> span = name.AsSpan();
        int segmentStart = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i < span.Length && span[i] != '.') continue;
            ReadOnlySpan<char> segment = span[segmentStart..i];
            if (segment.Length == 0 || (!char.IsLetter(segment[0]) && segment[0] != '_'))
                throw new ArgumentException($"Invalid stored procedure name '{name}'.", nameof(name));
            for (int c = 1; c < segment.Length; c++)
            {
                if (!char.IsLetterOrDigit(segment[c]) && segment[c] != '_')
                    throw new ArgumentException($"Invalid stored procedure name '{name}'.", nameof(name));
            }
            segmentStart = i + 1;
        }
        return name;
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
        // 设置输出参数类型以帮助 ADO.NET provider 正确推断。
        // ITM-787(r21)：可空泛型解包（.NET 11 起用 Type.GetNullableUnderlyingType 实例方法）——
        // GetOutputValue<T> 支持可空泛型，
        // 声明侧 typeof(T)== 链不识别 int?/long? 等会静默不设 DbType（两侧能力不对称）。
        Type effectiveType = typeof(T).GetNullableUnderlyingType() ?? typeof(T);
        if (effectiveType == typeof(int)) p.DbType = DbType.Int32;
        else if (effectiveType == typeof(long)) p.DbType = DbType.Int64;
        else if (effectiveType == typeof(string)) p.DbType = DbType.String;
        else if (effectiveType == typeof(decimal)) p.DbType = DbType.Decimal;
        else if (effectiveType == typeof(bool)) p.DbType = DbType.Boolean;
        else if (effectiveType == typeof(DateTime)) p.DbType = DbType.DateTime;
        else if (effectiveType == typeof(Guid)) p.DbType = DbType.Guid;
        _outputParams.Add(p);
        _parameters.Add(p);
        return this;
    }

    /// <summary>执行后读取输出参数值。
    /// <para>ITM-592: 必须在 <see cref="QueryAsync{T}"/> / <see cref="ExecuteAsync"/> 之后调用——
    /// 执行前调用会读到 <see cref="DBNull"/> 初始值并静默返回 default(T)，掩盖调用方 bug。</para></summary>
    public T? GetOutputValue<T>(string name)
    {
        // ITM-592: 未执行时 _outputParams 内 Value 恒为 DBNull.Value（WithOutputParam 初始化），
        // 走到下面 default 分支会返回 default(T) 无异常——调用方无法察觉。明确拒绝。
        if (!_executed)
            throw new InvalidOperationException(
                $"Cannot read output parameter '{name}' before executing the stored procedure. " +
                "Call QueryAsync<T>() or ExecuteAsync() first.");
        // D4（2026-10-01 全 API 逐项轮）：手写循环替代 List.Find——原每次读取分配 1 个闭包
        // （捕获 name 的显示类 + 委托）；输出参数数量级小、线性扫描等价。
        DbParameter? p = null;
        for (int i = 0; i < _outputParams.Count; i++)
        {
            if (_outputParams[i].ParameterName == name)
            {
                p = _outputParams[i];
                break;
            }
        }
        if (p is null)
            throw new InvalidOperationException($"Output parameter '{name}' not found.");
        // ITM-540: 宽容拆箱，参照 ScalarAsync——provider 返回的装箱类型可能与 T 不完全一致
        // （如 int 输出参数回填 long/decimal），直接 (T?) 强转会抛 InvalidCastException。
        if (p.Value is null or DBNull) return default;
        if (p.Value is T t) return t;
        Type target = typeof(T).GetNullableUnderlyingType() ?? typeof(T);
        // ITM-729(r20)：Convert.ChangeType 不支持 Guid（string 回填 Guid 输出参数会抛
        // InvalidCastException）——显式补 Guid 分支，与"宽容拆箱"文档承诺一致。
        if (target == typeof(Guid))
        {
            object guid = p.Value is Guid g ? g : Guid.Parse(p.Value.ToString()!);
            return (T)guid;
        }
        return (T?)Convert.ChangeType(p.Value, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>执行并返回结果集。</summary>
    public async ValueTask<List<T>> QueryAsync<T>(CancellationToken ct = default) where T : class, new()
    {
        // 先取门禁租约再校验元数据、最后置"已执行"标志（ITM-424/692）：门禁拒绝或
        // 未注册类型失败都不得永久消耗 single-use builder。
        using SessionOperationState.SessionOperationLease operation =
            _operationState.Enter();
        if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' not registered.");
        MarkExecuted();

        await using DbCommand cmd = _conn.CreateCommand();
        cmd.CommandText = _name;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(_timeout);
        cmd.Transaction = _operationState.GetActiveTransaction();
        foreach (var p in _parameters) cmd.Parameters.Add(p);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        ColumnOrderValidator.Validate<T>(reader, _validateColumnOrder);
        return await ResultListReader.ReadAllAsync(
            reader, (Func<DbDataReader, T>)factory, 16, ct).ConfigureAwait(false);
    }

    /// <summary>执行不返回结果集。</summary>
    public async ValueTask<int> ExecuteAsync(CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation =
            _operationState.Enter();
        MarkExecuted();
        await using DbCommand cmd = _conn.CreateCommand();
        cmd.CommandText = _name;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(_timeout);
        cmd.Transaction = _operationState.GetActiveTransaction();
        foreach (var p in _parameters) cmd.Parameters.Add(p);
        int result = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>builder 一次性契约：DbParameter 实例归属首个执行命令（provider 跟踪归属），
    /// 二次执行会以 provider 特定异常失败——这里改为提前明确失败。</summary>
    private void MarkExecuted()
    {
        if (_executed)
        {
            throw new InvalidOperationException(
                "StoredProcBuilder is single-use: its parameters belong to the first executed command. " +
                "Create a new builder via StoredProc(name) for another execution.");
        }
        _executed = true;
    }
}
