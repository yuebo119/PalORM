using System.Collections.Frozen;
using System.Data.Common;

namespace PalORM;

/// <summary>一个模型程序集在编译期生成的实体元数据片段。</summary>
/// <remarks>片段仅包含显式构造的类型键和委托，不执行反射或程序集扫描。</remarks>
public sealed class RegistryFragment
{
    /// <summary>类型 → 行读取工厂委托（装箱为 object，调用方按实体类型还原泛型）。</summary>
    public required IReadOnlyDictionary<Type, object> RowFactories { get; init; }

    /// <summary>类型 → 数据库表名。其键集是片段的实体全集，其余字典的键以此校验。</summary>
    public required IReadOnlyDictionary<Type, string> TableNames { get; init; }

    /// <summary>类型 → CRUD SQL 集。</summary>
    public required IReadOnlyDictionary<Type, CommandSqlSet> CommandSqls { get; init; }

    /// <summary>类型 → 按数据库方言生成的 CRUD SQL。可选，旧片段可缺省。</summary>
    public IReadOnlyDictionary<Type, CommandSqlByDialect> CommandSqlsByDialect { get; init; }
        = FrozenDictionary<Type, CommandSqlByDialect>.Empty;

    /// <summary>类型 → Insert 参数绑定委托。</summary>
    public required IReadOnlyDictionary<Type, Action<DbCommand, object>> BindInsert { get; init; }

    /// <summary>类型 → Update 参数绑定委托。</summary>
    public required IReadOnlyDictionary<Type, Action<DbCommand, object>> BindUpdate { get; init; }

    /// <summary>类型 → Delete 参数绑定委托（接收主键值 object）。</summary>
    public required IReadOnlyDictionary<Type, Action<DbCommand, object>> BindDelete { get; init; }

    /// <summary>类型 → 主键列名。</summary>
    public required IReadOnlyDictionary<Type, string> PkColumns { get; init; }

    /// <summary>类型 → 列名数组。注册时做只读快照，片段可安全复用生成代码的静态数组。</summary>
    public required IReadOnlyDictionary<Type, string[]> ColumnNames { get; init; }

    /// <summary>类型 → (属性名→列名) 映射（用于 Include JOIN ON 子句翻译）。</summary>
    public required IReadOnlyDictionary<Type, IReadOnlyDictionary<string, string>> PropertyToColumn { get; init; }

    /// <summary>类型 → CREATE TABLE DDL。</summary>
    public required IReadOnlyDictionary<Type, string> CreateTableSql { get; init; }

    /// <summary>类型 → 按数据库方言生成的 CREATE TABLE DDL。可选，旧片段可缺省。</summary>
    public IReadOnlyDictionary<Type, CreateTableSqlSet> CreateTableSqlByDialect { get; init; }
        = FrozenDictionary<Type, CreateTableSqlSet>.Empty;

    /// <summary>类型 → 三方言索引 DDL（ADR-B）。可选，无索引注解的片段可缺省。</summary>
    public IReadOnlyDictionary<Type, CreateIndexSqlSet> CreateIndexSqlByDialect { get; init; }
        = FrozenDictionary<Type, CreateIndexSqlSet>.Empty;

    /// <summary>类型 → 设置自增主键委托（MySQL LAST_INSERT_ID 回填用）。键集允许为实体子集。</summary>
    public required IReadOnlyDictionary<Type, Action<object, long>> SetIdDelegates { get; init; }

    /// <summary>类型 → 聚合 CRUD 元数据。</summary>
    public required IReadOnlyDictionary<Type, CrudMetadata> CrudMetadatas { get; init; }

    /// <summary>类型 → 编译期实体能力标志。</summary>
    public required IReadOnlyDictionary<Type, EntityFeatures> EntityFeatures { get; init; }
}

/// <summary>编译时注册表。各模型程序集的 ModuleInitializer 通过 <see cref="Register"/> 合并片段。</summary>
/// <remarks>注册在锁内构造完整不可变状态，并通过一次引用交换发布；外部调用方只能读取快照。</remarks>
public static class PalORM_Runtime
{
    private static readonly Lock _registrationLock = new();
    private static RuntimeRegistryState _state = RuntimeRegistryState.Empty;

    /// <summary>类型 → 行读取工厂委托（装箱为 object，调用方按实体类型还原泛型）。</summary>
    public static FrozenDictionary<Type, object> RowFactories => Volatile.Read(ref _state)._rowFactories;

    /// <summary>类型 → 数据库表名。</summary>
    public static FrozenDictionary<Type, string> TableNames => Volatile.Read(ref _state)._tableNames;

    /// <summary>类型 → CRUD SQL 集。</summary>
    public static FrozenDictionary<Type, CommandSqlSet> CommandSqls => Volatile.Read(ref _state)._commandSqls;

    /// <summary>类型 → 按数据库方言生成的 CRUD SQL。</summary>
    public static FrozenDictionary<Type, CommandSqlByDialect> CommandSqlsByDialect
        => Volatile.Read(ref _state)._commandSqlsByDialect;

    /// <summary>类型到 Insert 绑定委托。</summary>
    public static FrozenDictionary<Type, Action<DbCommand, object>> BindInsert => Volatile.Read(ref _state)._bindInsert;

    /// <summary>类型 → Update 绑定委托。</summary>
    public static FrozenDictionary<Type, Action<DbCommand, object>> BindUpdate => Volatile.Read(ref _state)._bindUpdate;

    /// <summary>类型 → Delete 绑定委托（接收主键值 object）。</summary>
    public static FrozenDictionary<Type, Action<DbCommand, object>> BindDelete => Volatile.Read(ref _state)._bindDelete;

    /// <summary>类型 → 主键列名。</summary>
    public static FrozenDictionary<Type, string> PkColumns => Volatile.Read(ref _state)._pkColumns;

    /// <summary>类型 → 只读列名列表（编译时确定，零反射）。</summary>
    public static FrozenDictionary<Type, IReadOnlyList<string>> ColumnNames => Volatile.Read(ref _state)._columnNames;

    /// <summary>类型 → (属性名→列名) 映射 (用于 Include JOIN ON 子句翻译)。</summary>
    public static FrozenDictionary<Type, FrozenDictionary<string, string>> PropertyToColumn => Volatile.Read(ref _state)._propertyToColumn;

    /// <summary>类型 → CREATE TABLE DDL（编译时生成，零反射）。</summary>
    public static FrozenDictionary<Type, string> CreateTableSql => Volatile.Read(ref _state)._createTableSql;

    /// <summary>类型 → 按数据库方言生成的 CREATE TABLE DDL。</summary>
    public static FrozenDictionary<Type, CreateTableSqlSet> CreateTableSqlByDialect
        => Volatile.Read(ref _state)._createTableSqlByDialect;

    /// <summary>类型 → 三方言索引 DDL（ADR-B）。</summary>
    public static FrozenDictionary<Type, CreateIndexSqlSet> CreateIndexSqlByDialect
        => Volatile.Read(ref _state)._createIndexSqlByDialect;

    /// <summary>类型 → 设置自增主键委托（MySQL LAST_INSERT_ID 用，零反射）。</summary>
    public static FrozenDictionary<Type, Action<object, long>> SetIdDelegates => Volatile.Read(ref _state)._setIdDelegates;

    /// <summary>类型 → 聚合 CRUD 元数据——单次查找替代四次独立查找。</summary>
    public static FrozenDictionary<Type, CrudMetadata> CrudMetadatas => Volatile.Read(ref _state)._crudMetadatas;

    /// <summary>类型 → 编译期实体能力标志。</summary>
    public static FrozenDictionary<Type, EntityFeatures> EntityFeatures => Volatile.Read(ref _state)._entityFeatures;

    /// <summary>原子验证并合并一个模型程序集生成的实体元数据片段。</summary>
    /// <exception cref="InvalidOperationException">同一实体类型已由另一个片段注册。</exception>
    public static void Register(RegistryFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        lock (_registrationLock)
        {
            RuntimeRegistryState current = Volatile.Read(ref _state);
            var entityTypes = fragment.TableNames.Keys.ToHashSet();
            ValidateRequiredKeys(entityTypes, fragment.RowFactories.Keys, nameof(fragment.RowFactories));
            ValidateRequiredKeys(entityTypes, fragment.CommandSqls.Keys, nameof(fragment.CommandSqls));
            ValidateOptionalKeys(entityTypes, fragment.CommandSqlsByDialect.Keys,
                nameof(fragment.CommandSqlsByDialect));
            ValidateRequiredKeys(entityTypes, fragment.BindInsert.Keys, nameof(fragment.BindInsert));
            ValidateRequiredKeys(entityTypes, fragment.BindUpdate.Keys, nameof(fragment.BindUpdate));
            ValidateRequiredKeys(entityTypes, fragment.BindDelete.Keys, nameof(fragment.BindDelete));
            ValidateRequiredKeys(entityTypes, fragment.PkColumns.Keys, nameof(fragment.PkColumns));
            ValidateRequiredKeys(entityTypes, fragment.ColumnNames.Keys, nameof(fragment.ColumnNames));
            ValidateRequiredKeys(entityTypes, fragment.PropertyToColumn.Keys, nameof(fragment.PropertyToColumn));
            ValidateRequiredKeys(entityTypes, fragment.CreateTableSql.Keys, nameof(fragment.CreateTableSql));
            ValidateOptionalKeys(entityTypes, fragment.CreateTableSqlByDialect.Keys,
                nameof(fragment.CreateTableSqlByDialect));
            ValidateOptionalKeys(entityTypes, fragment.CreateIndexSqlByDialect.Keys,
                nameof(fragment.CreateIndexSqlByDialect));
            ValidateRequiredKeys(entityTypes, fragment.CrudMetadatas.Keys, nameof(fragment.CrudMetadatas));
            ValidateRequiredKeys(entityTypes, fragment.EntityFeatures.Keys, nameof(fragment.EntityFeatures));
            ValidateOptionalKeys(entityTypes, fragment.SetIdDelegates.Keys, nameof(fragment.SetIdDelegates));

            Type? duplicate = entityTypes
                .Where(current._tableNames.ContainsKey)
                .OrderBy(static type => type.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (duplicate is not null)
                throw new InvalidOperationException($"Entity type '{duplicate.FullName}' is already registered in PalORM runtime metadata.");

            var propertyMappings = new Dictionary<Type, FrozenDictionary<string, string>>(current._propertyToColumn);
            foreach (var pair in fragment.PropertyToColumn)
                propertyMappings.Add(pair.Key, pair.Value.ToFrozenDictionary(StringComparer.Ordinal));

            var columnNames = new Dictionary<Type, IReadOnlyList<string>>(current._columnNames);
            foreach (var pair in fragment.ColumnNames)
                columnNames.Add(pair.Key, Array.AsReadOnly((string[])pair.Value.Clone()));

            var crudMetadatas = new Dictionary<Type, CrudMetadata>(current._crudMetadatas);
            foreach (var pair in fragment.CrudMetadatas)
                crudMetadatas.Add(pair.Key, pair.Value.Copy());

            // 防御性拷贝（ITM-204，与 ColumnNames 纪律对齐）：片段传入的是生成代码
            // static readonly string[] 的裸引用，Get() 返回值可被向下转型修改——
            // 注册时包装为只读快照，保证注册表"完整不可变"声明成立。
            var createIndexSql = new Dictionary<Type, CreateIndexSqlSet>(current._createIndexSqlByDialect);
            foreach (var pair in fragment.CreateIndexSqlByDialect)
            {
                createIndexSql.Add(pair.Key, new CreateIndexSqlSet(
                    Array.AsReadOnly(pair.Value.Sqlite.ToArray()),
                    Array.AsReadOnly(pair.Value.PostgreSql.ToArray()),
                    Array.AsReadOnly(pair.Value.MySql.ToArray())));
            }

            var next = new RuntimeRegistryState
            {
                _rowFactories = Merge(current._rowFactories, fragment.RowFactories),
                _tableNames = Merge(current._tableNames, fragment.TableNames),
                _commandSqls = Merge(current._commandSqls, fragment.CommandSqls),
                _commandSqlsByDialect = Merge(
                    current._commandSqlsByDialect, fragment.CommandSqlsByDialect),
                _bindInsert = Merge(current._bindInsert, fragment.BindInsert),
                _bindUpdate = Merge(current._bindUpdate, fragment.BindUpdate),
                _bindDelete = Merge(current._bindDelete, fragment.BindDelete),
                _pkColumns = Merge(current._pkColumns, fragment.PkColumns),
                _columnNames = columnNames.ToFrozenDictionary(),
                _propertyToColumn = propertyMappings.ToFrozenDictionary(),
                _createTableSql = Merge(current._createTableSql, fragment.CreateTableSql),
                _createTableSqlByDialect = Merge(
                    current._createTableSqlByDialect, fragment.CreateTableSqlByDialect),
                _createIndexSqlByDialect = createIndexSql.ToFrozenDictionary(),
                _setIdDelegates = Merge(current._setIdDelegates, fragment.SetIdDelegates),
                _crudMetadatas = crudMetadatas.ToFrozenDictionary(),
                _entityFeatures = Merge(current._entityFeatures, fragment.EntityFeatures)
            };

            Volatile.Write(ref _state, next);
        }
    }

    private static FrozenDictionary<Type, TValue> Merge<TValue>(
        FrozenDictionary<Type, TValue> current,
        IReadOnlyDictionary<Type, TValue> fragment)
    {
        var merged = new Dictionary<Type, TValue>(current);
        foreach (var pair in fragment)
            merged.Add(pair.Key, pair.Value);
        return merged.ToFrozenDictionary();
    }

    private static void ValidateRequiredKeys(
        HashSet<Type> entityTypes,
        IEnumerable<Type> metadataTypes,
        string metadataName)
    {
        if (!entityTypes.SetEquals(metadataTypes))
            throw new InvalidOperationException($"Registry fragment '{metadataName}' keys must match TableNames keys.");
    }

    private static void ValidateOptionalKeys(
        HashSet<Type> entityTypes,
        IEnumerable<Type> metadataTypes,
        string metadataName)
    {
        Type? unexpected = metadataTypes.FirstOrDefault(type => !entityTypes.Contains(type));
        if (unexpected is not null)
            throw new InvalidOperationException($"Registry fragment '{metadataName}' contains unknown entity type '{unexpected.FullName}'.");
    }

    private sealed class RuntimeRegistryState
    {
        internal static readonly RuntimeRegistryState Empty = new();

        // 字段名加 _ 前缀：避免与外部类 PalORM_Runtime 的同名 public 属性构成 Sonar S3218 遮蔽。
        internal FrozenDictionary<Type, object> _rowFactories { get; init; } = FrozenDictionary<Type, object>.Empty;
        internal FrozenDictionary<Type, string> _tableNames { get; init; } = FrozenDictionary<Type, string>.Empty;
        internal FrozenDictionary<Type, CommandSqlSet> _commandSqls { get; init; } = FrozenDictionary<Type, CommandSqlSet>.Empty;
        internal FrozenDictionary<Type, CommandSqlByDialect> _commandSqlsByDialect { get; init; } = FrozenDictionary<Type, CommandSqlByDialect>.Empty;
        internal FrozenDictionary<Type, Action<DbCommand, object>> _bindInsert { get; init; } = FrozenDictionary<Type, Action<DbCommand, object>>.Empty;
        internal FrozenDictionary<Type, Action<DbCommand, object>> _bindUpdate { get; init; } = FrozenDictionary<Type, Action<DbCommand, object>>.Empty;
        internal FrozenDictionary<Type, Action<DbCommand, object>> _bindDelete { get; init; } = FrozenDictionary<Type, Action<DbCommand, object>>.Empty;
        internal FrozenDictionary<Type, string> _pkColumns { get; init; } = FrozenDictionary<Type, string>.Empty;
        internal FrozenDictionary<Type, IReadOnlyList<string>> _columnNames { get; init; } = FrozenDictionary<Type, IReadOnlyList<string>>.Empty;
        internal FrozenDictionary<Type, FrozenDictionary<string, string>> _propertyToColumn { get; init; } = FrozenDictionary<Type, FrozenDictionary<string, string>>.Empty;
        internal FrozenDictionary<Type, string> _createTableSql { get; init; } = FrozenDictionary<Type, string>.Empty;
        internal FrozenDictionary<Type, CreateTableSqlSet> _createTableSqlByDialect { get; init; } = FrozenDictionary<Type, CreateTableSqlSet>.Empty;
        internal FrozenDictionary<Type, CreateIndexSqlSet> _createIndexSqlByDialect { get; init; } = FrozenDictionary<Type, CreateIndexSqlSet>.Empty;
        internal FrozenDictionary<Type, Action<object, long>> _setIdDelegates { get; init; } = FrozenDictionary<Type, Action<object, long>>.Empty;
        internal FrozenDictionary<Type, CrudMetadata> _crudMetadatas { get; init; } = FrozenDictionary<Type, CrudMetadata>.Empty;
        internal FrozenDictionary<Type, EntityFeatures> _entityFeatures { get; init; } = FrozenDictionary<Type, EntityFeatures>.Empty;
    }
}
