using System.Data.Common;

namespace PalORM;

/// <summary>CRUD 委托与工厂聚合——4 个委托打包为单参数，避免 CrudMetadata ctor 参数过多（S107）。
/// 与 CrudMetadata 同生命周期：注册时一次绑定，运行时按类型查找后调用。</summary>
public readonly struct CrudBindings
{
    /// <summary>Insert 参数绑定委托（支持批量 paramOffset 偏移）。</summary>
    public readonly Action<DbCommand, object, int> BindInsert;
    /// <summary>v4.6：仅设置预分配参数 Value 的委托（跨批参数复用路径）。</summary>
    public readonly Action<DbParameter[], object, int>? BindInsertValues;
    /// <summary>Upsert 参数绑定委托。</summary>
    public readonly Action<DbCommand, object> BindUpsert;
    /// <summary>Update 参数绑定委托。</summary>
    public readonly Action<DbCommand, object> BindUpdate;
    /// <summary>行读取工厂委托（装箱为 object）。</summary>
    public readonly object RowFactory;

    /// <summary>构造 CRUD 委托聚合。</summary>
    public CrudBindings(
        Action<DbCommand, object, int> bindInsert,
        Action<DbParameter[], object, int>? bindInsertValues,
        Action<DbCommand, object> bindUpsert,
        Action<DbCommand, object> bindUpdate,
        object rowFactory)
    {
        BindInsert = bindInsert;
        BindInsertValues = bindInsertValues;
        BindUpsert = bindUpsert;
        BindUpdate = bindUpdate;
        RowFactory = rowFactory;
    }
}

/// <summary>INSERT/UPSERT/UPDATE 涉及的列名聚合——把三个 IReadOnlyList 打包为单参数。</summary>
public readonly struct CrudColumns
{
    /// <summary>INSERT 涉及的列名（排除自增主键与计算列）。</summary>
    public readonly IReadOnlyList<string> Insert;
    /// <summary>UPSERT 涉及的列名。</summary>
    public readonly IReadOnlyList<string> Upsert;
    /// <summary>UPDATE SET 涉及的列名（ITM-642：与生成器 IsUpdatableColumn 谓词同序——
    /// BulkUpdateBatchAsync 直接消费本真源，不再解析生成 SQL 文本）。</summary>
    public readonly IReadOnlyList<string> Update;

    /// <summary>构造列名聚合。三个列表做只读快照，调用方可安全复用生成代码的静态数组。</summary>
    public CrudColumns(IReadOnlyList<string> insert, IReadOnlyList<string> upsert, IReadOnlyList<string> update)
    {
        Insert = Array.AsReadOnly(insert.ToArray());
        Upsert = Array.AsReadOnly(upsert.ToArray());
        Update = Array.AsReadOnly(update.ToArray());
    }
}

/// <summary>CRUD 元数据聚合——单次字典查找替代四次独立查找。</summary>
public readonly struct CrudMetadata
{
    /// <summary>legacy 无方言 CRUD SQL 集（标识符未经引用转义，运行时从不消费）。
    /// 评审 2026-09-02 收敛：方言 SQL（<c>PalORM_Runtime.CommandSqlsByDialect</c>）是唯一真源；
    /// 本字段仅为旧版本生成器模型程序集的注册兼容而保留（经旧 ctor 写入），新代码勿读勿写。</summary>
    public readonly CommandSqlSet Sqls;
    /// <summary>Insert 参数绑定委托（支持批量 paramOffset 偏移）。</summary>
    public readonly Action<DbCommand, object, int> BindInsert;
    /// <summary>v4.6：仅设置预分配参数 Value 的委托（跨批参数复用路径）。</summary>
    public readonly Action<DbParameter[], object, int>? BindInsertValues;
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
    /// <summary>UPDATE SET 涉及的列名（与 BindUpdate 参数序同源同序，ITM-642）。</summary>
    public readonly IReadOnlyList<string> UpdateColumns;
    /// <summary>递增并发令牌委托；实体无 [ConcurrencyCheck] 时为 null。</summary>
    public readonly Action<object>? IncrementVersion;
    /// <summary>判断实体主键是否仍为默认值（用于 Save 区分 Insert/Update）。</summary>
    public readonly Func<object, bool> HasDefaultKey;
    /// <summary>v4.3：源生成器保证 binder 参数数 == 列数，probe 只需验证一次。注册时设 true。</summary>
    public readonly bool InsertBinderValidated;

    /// <summary>推荐构造——接受聚合对象，避免参数列表过长（S107）。
    /// 评审 2026-09-02 收敛后的新形态：不含 legacy 无方言 SQL 载荷。</summary>
    /// <param name="bindings">CRUD 委托聚合（BindInsert/BindUpsert/BindUpdate/RowFactory）。</param>
    /// <param name="columns">列名聚合（Insert/Upsert/Update，注册时做只读快照）。</param>
    /// <param name="incrementVersion">递增并发令牌委托，无并发列时传 null。</param>
    /// <param name="hasDefaultKey">主键默认值判断委托。</param>
    /// <param name="insertBinderValidated">源生成器已验证 binder 参数数 == 列数时为 true，跳过运行时 probe。</param>
    public CrudMetadata(
        CrudBindings bindings,
        CrudColumns columns,
        Action<object>? incrementVersion,
        Func<object, bool> hasDefaultKey,
        bool insertBinderValidated = true)
    {
        BindInsert = bindings.BindInsert;
        BindInsertValues = bindings.BindInsertValues;
        BindUpsert = bindings.BindUpsert;
        BindUpdate = bindings.BindUpdate;
        RowFactory = bindings.RowFactory;
        // CrudColumns ctor 已做只读快照；这里直接复用，避免二次拷贝。
        InsertColumns = columns.Insert;
        UpsertColumns = columns.Upsert;
        UpdateColumns = columns.Update;
        IncrementVersion = incrementVersion;
        HasDefaultKey = hasDefaultKey;
        InsertBinderValidated = insertBinderValidated;
    }

    /// <summary>旧版生成器兼容构造——与新版生成的注册代码保持二进制兼容（旧模型程序集的
    /// ModuleInitializer 经此 ctor 传入 legacy SQL 载荷）。新代码请用不含 <paramref name="sqls"/> 的 ctor；
    /// <paramref name="sqls"/> 仅被存储、从不消费。</summary>
    /// <param name="sqls">legacy CRUD SQL 集（仅兼容载荷，运行时不消费）。</param>
    /// <param name="bindings">CRUD 委托聚合（BindInsert/BindUpsert/BindUpdate/RowFactory）。</param>
    /// <param name="columns">列名聚合（Insert/Upsert/Update，注册时做只读快照）。</param>
    /// <param name="incrementVersion">递增并发令牌委托，无并发列时传 null。</param>
    /// <param name="hasDefaultKey">主键默认值判断委托。</param>
    /// <param name="insertBinderValidated">源生成器已验证 binder 参数数 == 列数时为 true，跳过运行时 probe。</param>
    public CrudMetadata(
        CommandSqlSet sqls,
        CrudBindings bindings,
        CrudColumns columns,
        Action<object>? incrementVersion,
        Func<object, bool> hasDefaultKey,
        bool insertBinderValidated = true)
        : this(bindings, columns, incrementVersion, hasDefaultKey, insertBinderValidated)
    {
        Sqls = sqls;
    }

    internal CrudMetadata Copy()
        => new(Sqls,
            new CrudBindings(BindInsert, BindInsertValues, BindUpsert, BindUpdate, RowFactory),
            new CrudColumns(InsertColumns, UpsertColumns, UpdateColumns),
            IncrementVersion, HasDefaultKey, InsertBinderValidated);
}
