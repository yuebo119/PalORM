using System.Data.Common;

namespace PalORM;

/// <summary>CRUD 委托与工厂聚合——4 个委托打包为单参数，避免 CrudMetadata ctor 参数过多（S107）。
/// 与 CrudMetadata 同生命周期：注册时一次绑定，运行时按类型查找后调用。</summary>
public readonly struct CrudBindings
{
    /// <summary>Insert 参数绑定委托（支持批量 paramOffset 偏移）。</summary>
    public readonly Action<DbCommand, object, int> BindInsert;
    /// <summary>Upsert 参数绑定委托。</summary>
    public readonly Action<DbCommand, object> BindUpsert;
    /// <summary>Update 参数绑定委托。</summary>
    public readonly Action<DbCommand, object> BindUpdate;
    /// <summary>行读取工厂委托（装箱为 object）。</summary>
    public readonly object RowFactory;

    /// <summary>构造 CRUD 委托聚合。</summary>
    public CrudBindings(
        Action<DbCommand, object, int> bindInsert,
        Action<DbCommand, object> bindUpsert,
        Action<DbCommand, object> bindUpdate,
        object rowFactory)
    {
        BindInsert = bindInsert;
        BindUpsert = bindUpsert;
        BindUpdate = bindUpdate;
        RowFactory = rowFactory;
    }
}

/// <summary>INSERT/UPSERT 涉及的列名聚合——把两个 IReadOnlyList 打包为单参数。</summary>
public readonly struct CrudColumns
{
    /// <summary>INSERT 涉及的列名（排除自增主键与计算列）。</summary>
    public readonly IReadOnlyList<string> Insert;
    /// <summary>UPSERT 涉及的列名。</summary>
    public readonly IReadOnlyList<string> Upsert;

    /// <summary>构造列名聚合。两个列表做只读快照，调用方可安全复用生成代码的静态数组。</summary>
    public CrudColumns(IReadOnlyList<string> insert, IReadOnlyList<string> upsert)
    {
        Insert = Array.AsReadOnly(insert.ToArray());
        Upsert = Array.AsReadOnly(upsert.ToArray());
    }
}

/// <summary>CRUD 元数据聚合——单次字典查找替代四次独立查找。</summary>
public readonly struct CrudMetadata
{
    /// <summary>legacy 无方言 CRUD SQL 集（标识符未经引用转义）。ITM-580: 仅作
    /// GetCommandSqls 的 fallback 形参传递并被其拒绝——不要直接消费；
    /// 按方言 SQL 走 PalORM_Runtime.CommandSqlsByDialect。</summary>
    public readonly CommandSqlSet Sqls;
    /// <summary>Insert 参数绑定委托（支持批量 paramOffset 偏移）。</summary>
    public readonly Action<DbCommand, object, int> BindInsert;
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

    /// <summary>推荐构造——接受聚合对象，避免参数列表过长（S107）。
    /// 二进制布局与遗留 9 参 ctor 等价，所有 public readonly 字段保持位置。</summary>
    /// <param name="sqls">CRUD SQL 集。</param>
    /// <param name="bindings">CRUD 委托聚合（BindInsert/BindUpsert/BindUpdate/RowFactory）。</param>
    /// <param name="columns">列名聚合（Insert/Upsert，注册时做只读快照）。</param>
    /// <param name="incrementVersion">递增并发令牌委托，无并发列时传 null。</param>
    /// <param name="hasDefaultKey">主键默认值判断委托。</param>
    public CrudMetadata(
        CommandSqlSet sqls,
        CrudBindings bindings,
        CrudColumns columns,
        Action<object>? incrementVersion,
        Func<object, bool> hasDefaultKey)
    {
        Sqls = sqls;
        BindInsert = bindings.BindInsert;
        BindUpsert = bindings.BindUpsert;
        BindUpdate = bindings.BindUpdate;
        RowFactory = bindings.RowFactory;
        // CrudColumns ctor 已做只读快照；这里直接复用，避免二次拷贝。
        InsertColumns = columns.Insert;
        UpsertColumns = columns.Upsert;
        IncrementVersion = incrementVersion;
        HasDefaultKey = hasDefaultKey;
    }

    internal CrudMetadata Copy()
        => new(Sqls,
            new CrudBindings(BindInsert, BindUpsert, BindUpdate, RowFactory),
            new CrudColumns(InsertColumns, UpsertColumns),
            IncrementVersion, HasDefaultKey);
}
