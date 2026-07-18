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

/// <summary>编译期确定的实体能力标志，运行时据此启用软删除、多租户等行为。</summary>
[Flags]
public enum EntityFeatures
{
    /// <summary>无附加能力。</summary>
    None = 0,
    /// <summary>软删除（实体标注 <see cref="SoftDeleteAttribute"/>）。</summary>
    SoftDelete = 1,
    /// <summary>多租户感知（实体标注 <see cref="TenantAwareAttribute"/>）。</summary>
    TenantAware = 2
}

/// <summary>编译时注册表。各模型程序集的 ModuleInitializer 通过 <see cref="Register"/> 合并片段。</summary>
/// <remarks>注册在锁内构造完整不可变状态，并通过一次引用交换发布；外部调用方只能读取快照。</remarks>
public static class PalORM_Runtime
{
    private static readonly Lock _registrationLock = new();
    private static RuntimeRegistryState _state = RuntimeRegistryState.Empty;

    /// <summary>类型 → 行读取工厂委托（装箱为 object，调用方按实体类型还原泛型）。</summary>
    public static FrozenDictionary<Type, object> RowFactories => Volatile.Read(ref _state).RowFactories;

    /// <summary>类型 → 数据库表名。</summary>
    public static FrozenDictionary<Type, string> TableNames => Volatile.Read(ref _state).TableNames;

    /// <summary>类型 → CRUD SQL 集。</summary>
    public static FrozenDictionary<Type, CommandSqlSet> CommandSqls => Volatile.Read(ref _state).CommandSqls;

    /// <summary>类型 → 按数据库方言生成的 CRUD SQL。</summary>
    public static FrozenDictionary<Type, CommandSqlByDialect> CommandSqlsByDialect
        => Volatile.Read(ref _state).CommandSqlsByDialect;

    /// <summary>类型到 Insert 绑定委托。</summary>
    public static FrozenDictionary<Type, Action<DbCommand, object>> BindInsert => Volatile.Read(ref _state).BindInsert;

    /// <summary>类型 → Update 绑定委托。</summary>
    public static FrozenDictionary<Type, Action<DbCommand, object>> BindUpdate => Volatile.Read(ref _state).BindUpdate;

    /// <summary>类型 → Delete 绑定委托（接收主键值 object）。</summary>
    public static FrozenDictionary<Type, Action<DbCommand, object>> BindDelete => Volatile.Read(ref _state).BindDelete;

    /// <summary>类型 → 主键列名。</summary>
    public static FrozenDictionary<Type, string> PkColumns => Volatile.Read(ref _state).PkColumns;

    /// <summary>类型 → 只读列名列表（编译时确定，零反射）。</summary>
    public static FrozenDictionary<Type, IReadOnlyList<string>> ColumnNames => Volatile.Read(ref _state).ColumnNames;

    /// <summary>类型 → (属性名→列名) 映射 (用于 Include JOIN ON 子句翻译)。</summary>
    public static FrozenDictionary<Type, FrozenDictionary<string, string>> PropertyToColumn => Volatile.Read(ref _state).PropertyToColumn;

    /// <summary>类型 → CREATE TABLE DDL（编译时生成，零反射）。</summary>
    public static FrozenDictionary<Type, string> CreateTableSql => Volatile.Read(ref _state).CreateTableSql;

    /// <summary>类型 → 按数据库方言生成的 CREATE TABLE DDL。</summary>
    public static FrozenDictionary<Type, CreateTableSqlSet> CreateTableSqlByDialect
        => Volatile.Read(ref _state).CreateTableSqlByDialect;

    /// <summary>类型 → 三方言索引 DDL（ADR-B）。</summary>
    public static FrozenDictionary<Type, CreateIndexSqlSet> CreateIndexSqlByDialect
        => Volatile.Read(ref _state).CreateIndexSqlByDialect;

    /// <summary>类型 → 设置自增主键委托（MySQL LAST_INSERT_ID 用，零反射）。</summary>
    public static FrozenDictionary<Type, Action<object, long>> SetIdDelegates => Volatile.Read(ref _state).SetIdDelegates;

    /// <summary>类型 → 聚合 CRUD 元数据——单次查找替代四次独立查找。</summary>
    public static FrozenDictionary<Type, CrudMetadata> CrudMetadatas => Volatile.Read(ref _state).CrudMetadatas;

    /// <summary>类型 → 编译期实体能力标志。</summary>
    public static FrozenDictionary<Type, EntityFeatures> EntityFeatures => Volatile.Read(ref _state).EntityFeatures;

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
                .Where(current.TableNames.ContainsKey)
                .OrderBy(static type => type.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (duplicate is not null)
                throw new InvalidOperationException($"Entity type '{duplicate.FullName}' is already registered in PalORM runtime metadata.");

            var propertyMappings = new Dictionary<Type, FrozenDictionary<string, string>>(current.PropertyToColumn);
            foreach (var pair in fragment.PropertyToColumn)
                propertyMappings.Add(pair.Key, pair.Value.ToFrozenDictionary(StringComparer.Ordinal));

            var columnNames = new Dictionary<Type, IReadOnlyList<string>>(current.ColumnNames);
            foreach (var pair in fragment.ColumnNames)
                columnNames.Add(pair.Key, Array.AsReadOnly((string[])pair.Value.Clone()));

            var crudMetadatas = new Dictionary<Type, CrudMetadata>(current.CrudMetadatas);
            foreach (var pair in fragment.CrudMetadatas)
                crudMetadatas.Add(pair.Key, pair.Value.Copy());

            // 防御性拷贝（ITM-204，与 ColumnNames 纪律对齐）：片段传入的是生成代码
            // static readonly string[] 的裸引用，Get() 返回值可被向下转型修改——
            // 注册时包装为只读快照，保证注册表"完整不可变"声明成立。
            var createIndexSql = new Dictionary<Type, CreateIndexSqlSet>(current.CreateIndexSqlByDialect);
            foreach (var pair in fragment.CreateIndexSqlByDialect)
            {
                createIndexSql.Add(pair.Key, new CreateIndexSqlSet(
                    Array.AsReadOnly(pair.Value.Sqlite.ToArray()),
                    Array.AsReadOnly(pair.Value.PostgreSql.ToArray()),
                    Array.AsReadOnly(pair.Value.MySql.ToArray())));
            }

            var next = new RuntimeRegistryState
            {
                RowFactories = Merge(current.RowFactories, fragment.RowFactories),
                TableNames = Merge(current.TableNames, fragment.TableNames),
                CommandSqls = Merge(current.CommandSqls, fragment.CommandSqls),
                CommandSqlsByDialect = Merge(
                    current.CommandSqlsByDialect, fragment.CommandSqlsByDialect),
                BindInsert = Merge(current.BindInsert, fragment.BindInsert),
                BindUpdate = Merge(current.BindUpdate, fragment.BindUpdate),
                BindDelete = Merge(current.BindDelete, fragment.BindDelete),
                PkColumns = Merge(current.PkColumns, fragment.PkColumns),
                ColumnNames = columnNames.ToFrozenDictionary(),
                PropertyToColumn = propertyMappings.ToFrozenDictionary(),
                CreateTableSql = Merge(current.CreateTableSql, fragment.CreateTableSql),
                CreateTableSqlByDialect = Merge(
                    current.CreateTableSqlByDialect, fragment.CreateTableSqlByDialect),
                CreateIndexSqlByDialect = createIndexSql.ToFrozenDictionary(),
                SetIdDelegates = Merge(current.SetIdDelegates, fragment.SetIdDelegates),
                CrudMetadatas = crudMetadatas.ToFrozenDictionary(),
                EntityFeatures = Merge(current.EntityFeatures, fragment.EntityFeatures)
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

        internal FrozenDictionary<Type, object> RowFactories { get; init; } = FrozenDictionary<Type, object>.Empty;
        internal FrozenDictionary<Type, string> TableNames { get; init; } = FrozenDictionary<Type, string>.Empty;
        internal FrozenDictionary<Type, CommandSqlSet> CommandSqls { get; init; } = FrozenDictionary<Type, CommandSqlSet>.Empty;
        internal FrozenDictionary<Type, CommandSqlByDialect> CommandSqlsByDialect { get; init; } = FrozenDictionary<Type, CommandSqlByDialect>.Empty;
        internal FrozenDictionary<Type, Action<DbCommand, object>> BindInsert { get; init; } = FrozenDictionary<Type, Action<DbCommand, object>>.Empty;
        internal FrozenDictionary<Type, Action<DbCommand, object>> BindUpdate { get; init; } = FrozenDictionary<Type, Action<DbCommand, object>>.Empty;
        internal FrozenDictionary<Type, Action<DbCommand, object>> BindDelete { get; init; } = FrozenDictionary<Type, Action<DbCommand, object>>.Empty;
        internal FrozenDictionary<Type, string> PkColumns { get; init; } = FrozenDictionary<Type, string>.Empty;
        internal FrozenDictionary<Type, IReadOnlyList<string>> ColumnNames { get; init; } = FrozenDictionary<Type, IReadOnlyList<string>>.Empty;
        internal FrozenDictionary<Type, FrozenDictionary<string, string>> PropertyToColumn { get; init; } = FrozenDictionary<Type, FrozenDictionary<string, string>>.Empty;
        internal FrozenDictionary<Type, string> CreateTableSql { get; init; } = FrozenDictionary<Type, string>.Empty;
        internal FrozenDictionary<Type, CreateTableSqlSet> CreateTableSqlByDialect { get; init; } = FrozenDictionary<Type, CreateTableSqlSet>.Empty;
        internal FrozenDictionary<Type, CreateIndexSqlSet> CreateIndexSqlByDialect { get; init; } = FrozenDictionary<Type, CreateIndexSqlSet>.Empty;
        internal FrozenDictionary<Type, Action<object, long>> SetIdDelegates { get; init; } = FrozenDictionary<Type, Action<object, long>>.Empty;
        internal FrozenDictionary<Type, CrudMetadata> CrudMetadatas { get; init; } = FrozenDictionary<Type, CrudMetadata>.Empty;
        internal FrozenDictionary<Type, EntityFeatures> EntityFeatures { get; init; } = FrozenDictionary<Type, EntityFeatures>.Empty;
    }
}

/// <summary>编译期生成的单方言 CRUD SQL 集。</summary>
/// <param name="Insert">INSERT 语句（不含主键回填）。</param>
/// <param name="Update">按主键 UPDATE 语句。</param>
/// <param name="Delete">按主键 DELETE 语句。</param>
/// <param name="InsertReturning">带主键回填的 INSERT 语句（如 RETURNING/LAST_INSERT_ID）。</param>
public readonly record struct CommandSqlSet(string Insert, string Update, string Delete, string InsertReturning);

/// <summary>编译期生成的三数据库方言 CRUD SQL。</summary>
/// <param name="Sqlite">SQLite 方言 SQL 集。</param>
/// <param name="PostgreSql">PostgreSQL 方言 SQL 集。</param>
/// <param name="MySql">MySQL 方言 SQL 集。</param>
public readonly record struct CommandSqlByDialect(
    CommandSqlSet Sqlite,
    CommandSqlSet PostgreSql,
    CommandSqlSet MySql)
{
    /// <summary>按 Provider 方言选择对应 SQL。</summary>
    public CommandSqlSet Get(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.Sqlite => Sqlite,
            SqlDialect.PostgreSql => PostgreSql,
            SqlDialect.MySql => MySql,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
        };
}

/// <summary>编译期生成的三数据库方言建表 DDL。</summary>
/// <param name="Sqlite">SQLite 方言 CREATE TABLE。</param>
/// <param name="PostgreSql">PostgreSQL 方言 CREATE TABLE。</param>
/// <param name="MySql">MySQL 方言 CREATE TABLE。</param>
public readonly record struct CreateTableSqlSet(
    string Sqlite,
    string PostgreSql,
    string MySql)
{
    /// <summary>按 Provider 方言选择对应 DDL。</summary>
    public string Get(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.Sqlite => Sqlite,
            SqlDialect.PostgreSql => PostgreSql,
            SqlDialect.MySql => MySql,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
        };
}

/// <summary>编译期生成的三数据库方言索引 DDL（ADR-B：[Index]/[Unique] 注解产物）。</summary>
/// <param name="Sqlite">SQLite 方言 CREATE INDEX 语句组。</param>
/// <param name="PostgreSql">PostgreSQL 方言 CREATE INDEX 语句组。</param>
/// <param name="MySql">MySQL 方言 CREATE INDEX 语句组。</param>
public readonly record struct CreateIndexSqlSet(
    IReadOnlyList<string> Sqlite,
    IReadOnlyList<string> PostgreSql,
    IReadOnlyList<string> MySql)
{
    /// <summary>按 Provider 方言选择对应索引 DDL 组。</summary>
    public IReadOnlyList<string> Get(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.Sqlite => Sqlite,
            SqlDialect.PostgreSql => PostgreSql,
            SqlDialect.MySql => MySql,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
        };
}

/// <summary>CRUD 元数据聚合——单次字典查找替代四次独立查找。</summary>
public readonly struct CrudMetadata
{
    /// <summary>CRUD SQL 集（当前方言）。</summary>
    public readonly CommandSqlSet Sqls;
    /// <summary>Insert 参数绑定委托。</summary>
    public readonly Action<DbCommand, object> BindInsert;
    /// <summary>Upsert 参数绑定委托。</summary>
    public readonly Action<DbCommand, object> BindUpsert;
    /// <summary>Update 参数绑定委托。</summary>
    public readonly Action<DbCommand, object> BindUpdate;
    /// <summary>行读取工厂委托（装箱为 object）。</summary>
    public readonly object RowFactory;
    /// <summary>INSERT 涉及的列名（排除自增主键与计算列）。</summary>
    public readonly IReadOnlyList<string> InsertColumns;
    /// <summary>UPSERT 涉及的列名。</summary>
    public readonly IReadOnlyList<string> UpsertColumns;
    /// <summary>递增并发令牌委托；实体无 [ConcurrencyCheck] 时为 null。</summary>
    public readonly Action<object>? IncrementVersion;
    /// <summary>判断实体主键是否仍为默认值（用于 Save 区分 Insert/Update）。</summary>
    public readonly Func<object, bool> HasDefaultKey;

    /// <summary>构造聚合元数据。列名列表做只读快照，调用方可安全复用生成代码的静态数组。</summary>
    /// <param name="sqls">CRUD SQL 集。</param>
    /// <param name="bindInsert">Insert 参数绑定委托。</param>
    /// <param name="bindUpsert">Upsert 参数绑定委托。</param>
    /// <param name="bindUpdate">Update 参数绑定委托。</param>
    /// <param name="rowFactory">行读取工厂委托（装箱为 object）。</param>
    /// <param name="insertColumns">INSERT 涉及的列名。</param>
    /// <param name="upsertColumns">UPSERT 涉及的列名。</param>
    /// <param name="incrementVersion">递增并发令牌委托，无并发列时传 null。</param>
    /// <param name="hasDefaultKey">主键默认值判断委托。</param>
    public CrudMetadata(
        CommandSqlSet sqls,
        Action<DbCommand, object> bindInsert,
        Action<DbCommand, object> bindUpsert,
        Action<DbCommand, object> bindUpdate,
        object rowFactory,
        IReadOnlyList<string> insertColumns,
        IReadOnlyList<string> upsertColumns,
        Action<object>? incrementVersion,
        Func<object, bool> hasDefaultKey)
    {
        Sqls = sqls;
        BindInsert = bindInsert;
        BindUpsert = bindUpsert;
        BindUpdate = bindUpdate;
        RowFactory = rowFactory;
        InsertColumns = Array.AsReadOnly(insertColumns.ToArray());
        UpsertColumns = Array.AsReadOnly(upsertColumns.ToArray());
        IncrementVersion = incrementVersion;
        HasDefaultKey = hasDefaultKey;
    }

    internal CrudMetadata Copy()
        => new(Sqls, BindInsert, BindUpsert, BindUpdate, RowFactory,
            InsertColumns, UpsertColumns, IncrementVersion, HasDefaultKey);
}
