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
    /// <summary>v5.6：仅设置预分配 UPDATE 参数 Value 的委托（批量 UPDATE 参数池路径，
    /// 零 CreateParameter 分配）。旧版生成器模型程序集为 null，消费方应回退逐行绑定。</summary>
    public readonly Action<DbParameter[], object, int>? BindUpdateValues;
    /// <summary>行读取工厂委托（装箱为 object）。</summary>
    public readonly object RowFactory;
    /// <summary>v5.6.0：INSERT ... RETURNING 只回主键（生成器静态判定：唯一自增主键之外
    /// 全部列可直接插入且无转换器/OwnedJson/IgnoreOnInsert/Computed/Timestamp——
    /// RETURNING 的整行与插入值恒等，物化等价于返回调用方实体+回填 ID）。
    /// 消费方（InsertCoreAsync）据此走标量读取路径；旧生成器缺省 false 走整行物化。</summary>
    public readonly bool InsertReturningKeyOnly;
    /// <summary>PL-3：INSERT 不读返回（生成器静态判定 SupportsInsertWithoutReturning：
    /// 唯一非自增主键 + 全部列 IsInsertable 且无转换器/OwnedJson——插入值即行值，
    /// 纯 INSERT 即完成语义）。消费方（InsertCoreAsync）走 ExecuteNonQuery 路径，
    /// 省 RETURNING/LAST_INSERT_ID 的读返回与物化；旧生成器缺省 false 走读返回路径。</summary>
    public readonly bool InsertNoReturning;
    /// <summary>PL-3.2：仅设置预分配 UPSERT 参数 Value 的委托（批量 UPSERT 参数池路径，
    /// 与 BindUpsert 同列序）。旧版生成器缺省 false 走读返回路径。</summary>
    public readonly Action<DbParameter[], object, int>? BindUpsertValues;
    /// <summary>O1（2026-10-01）：二进制 COPY 定型行写入委托（PG Binary COPY 路径，
    /// 与 BindInsertValues 同列序同谓词）。仅当实体全部可插入列的 provider 类型都在
    /// IBinaryRowSink 支持面内时由生成器发射；旧版生成器或含未支持类型时为 null，
    /// 消费方回退参数池路径（零行为变化）。</summary>
    public readonly Action<IBinaryRowSink, object>? CopyWriteRow;
    /// <summary>B21（2026-10-01）：INSERT 池 DbType 一次性初始化委托（消费方 = MultiValueBulkInsert
    /// 自建池路径，建池后调用一次）。新旧形态互斥：非 null 时 BindInsertValues 只写 Value
    /// （DbType 已由本委托建立，池存续期内恒定）；null（旧生成器程序集）时 BindInsertValues
    /// 保持"每行写 DbType+Value"的旧形态。</summary>
    public readonly Action<DbParameter[], int>? InitInsertParameters;

    /// <summary>UNNEST-1（2026-10-02）：主键数组构造器——把键对象序列转成类型化数组
    /// （<c>BuildDeleteKeyArray(keys, start, count)</c>），供 PG 的 <c>pk = ANY(@ids)</c> 形态使用。
    /// <para>元素类型与元素值均由生成物静态确定（零反射，AOT 安全）；元素值与
    /// <c>BindDelete</c> 同一归一化真源，两条路径绑定语义逐位一致。</para>
    /// <para>返回 null 表示**该实体不支持数组形态**（复合主键——UNNEST 需行构造器数组，
    /// 收益与复杂度不成比例）；旧版生成器程序集亦缺此委托。两种情况调用方均回退 IN 占位符形态。</para></summary>
    public readonly Func<IReadOnlyList<object>, int, int, Array?>? BuildDeleteKeyArray;

    /// <summary>UNNEST 阶段 B（2026-10-02）：逐列类型化数组填充器——把实体区间按 UPDATE 列序
    /// （SET 列 → 主键 → 并发令牌）填入调用方预建的数组。
    /// <para><c>FillUpdateColumnArrays(entities, start, count, arrays, arrayOffset)</c>：元素类型
    /// 与元素值均与 <c>BindUpdateValues</c> 同一真源（共享 <c>GetParameterValueExpressionCore</c>
    /// 与 <c>GetUpdateColumnOrder</c>），两条路径绑定语义逐位一致。</para>
    /// <para>返回 null 表示旧版生成器程序集；调用方回退 VALUES 形态。</para></summary>
    public readonly Action<IReadOnlyList<object>, int, int, Array[], int>? FillUpdateColumnArrays;

    /// <summary>UNNEST 阶段 B：逐列数组的元素类型（与 <see cref="FillUpdateColumnArrays"/> 同序同长度）。
    /// <para><b>为什么必须带可空标注</b>：数组元素类型决定能否承载 null——值类型必须建成
    /// <c>T?[]</c>（探针实测 PG 正确写 SQL NULL），非可空列建成裸 <c>T[]</c>（可省一次 Nullable
    /// 包装）。生成期已知，故直接发自真源而非运行时推断。</para>
    /// <para>注册时做只读快照（与 ColumnNames 同纪律）：片段传入的是生成代码的静态数组裸引用。</para></summary>
    public readonly IReadOnlyList<Type>? UpdateColumnArrayElementTypes;

    /// <summary>UNNEST 阶段 B：逐列类型化数组分配器——<c>CreateUpdateColumnArrays(count)</c> 返回
    /// 长度 <c>count</c> 的各列数组，<b>静态类型 <c>new T[count]</c> 分配</b>。
    /// <para><b>为什么分配必须在生成物内</b>：Core 侧按 <c>Type</c> 建数组只能走
    /// <c>Array.CreateInstance(Type, …)</c>，该方法带 <c>RequiresDynamicCode</c>（IL3050），
    /// AOT 下不可用——生成物内写静态类型 <c>new T[count]</c> 才是 AOT 安全的（G4/G6 门禁）。</para></summary>
    public readonly Func<int, Array[]>? CreateUpdateColumnArrays;

    /// <summary>UNNEST 阶段 B：UPSERT 的逐列数组填充器——列序 = <c>IsUpsertable</c> 声明序
    /// （与 <c>BindUpsertValues</c> / <c>UpsertColumns</c> 同源同序）。语义与
    /// <see cref="FillUpdateColumnArrays"/> 同构，只是列集不同（UPSERT 含非更新列，不含并发令牌）。</summary>
    public readonly Action<IReadOnlyList<object>, int, int, Array[], int>? FillUpsertColumnArrays;

    /// <summary>UNNEST 阶段 B：UPSERT 逐列数组的元素类型（与 <see cref="FillUpsertColumnArrays"/>
    /// 同序同长度；注册时做只读快照）。</summary>
    public readonly IReadOnlyList<Type>? UpsertColumnArrayElementTypes;

    /// <summary>UNNEST 阶段 B：UPSERT 逐列数组分配器（AOT 安全，同 <see cref="CreateUpdateColumnArrays"/>）。</summary>
    public readonly Func<int, Array[]>? CreateUpsertColumnArrays;

    /// <summary>N4（2026-10-04 全量复读）：UPSERT 池 DbType 一次性初始化委托（消费方 =
    /// BatchUpsertAsync 自建池路径，建池后调用一次）。新旧形态互斥：非 null 时
    /// <see cref="BindUpsertValues"/> 只写 Value（DbType 已由本委托建立，池存续期内恒定）；
    /// null（旧生成器程序集）时 BindUpsertValues 保持"每行写 DbType+Value"的旧形态
    ///（B21 对 INSERT 池的契约同构）。</summary>
    public readonly Action<DbParameter[], int>? InitUpsertParameters;

    /// <summary>N4（2026-10-04 全量复读）：UPDATE 池 DbType 一次性初始化委托（消费方 =
    /// ExecuteBulkUpdatePooledAsync / ExecuteBulkUpdateBatchesAsync 的参数池，建池后调用一次；
    /// PL-2 单行复用槽不经此——其池经 BindUpdate 建参时已带 DbTypeHint）。新旧形态互斥同
    /// <see cref="InitUpsertParameters"/>。</summary>
    public readonly Action<DbParameter[], int>? InitUpdateParameters;

    /// <summary>构造 CRUD 委托聚合。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S107:Constructor should not have more than 7 parameters",
        Justification = "本聚合存在的目的就是把 CRUD 绑定打包为单参数（避免 CrudMetadata 的 20+ 参列表）；"
            + "成员随版本演进只会增加，拆分聚合会把参数列表问题转移回消费方。末位三参均有默认值，"
            + "位置参数调用点不受影响。")]
    public CrudBindings(
        Action<DbCommand, object, int> bindInsert,
        Action<DbParameter[], object, int>? bindInsertValues,
        Action<DbCommand, object> bindUpsert,
        Action<DbCommand, object> bindUpdate,
        object rowFactory,
        Action<DbParameter[], object, int>? bindUpdateValues = null,
        bool insertReturningKeyOnly = false,
        bool insertNoReturning = false,
        Action<DbParameter[], object, int>? bindUpsertValues = null,
        Action<IBinaryRowSink, object>? copyWriteRow = null,
        Action<DbParameter[], int>? initInsertParameters = null,
        Func<IReadOnlyList<object>, int, int, Array?>? buildDeleteKeyArray = null,
        Action<IReadOnlyList<object>, int, int, Array[], int>? fillUpdateColumnArrays = null,
        IReadOnlyList<Type>? updateColumnArrayElementTypes = null,
        Func<int, Array[]>? createUpdateColumnArrays = null,
        Action<IReadOnlyList<object>, int, int, Array[], int>? fillUpsertColumnArrays = null,
        IReadOnlyList<Type>? upsertColumnArrayElementTypes = null,
        Func<int, Array[]>? createUpsertColumnArrays = null,
        Action<DbParameter[], int>? initUpsertParameters = null,
        Action<DbParameter[], int>? initUpdateParameters = null)
    {
        BindInsert = bindInsert;
        BindInsertValues = bindInsertValues;
        BindUpsert = bindUpsert;
        BindUpsertValues = bindUpsertValues;
        BindUpdate = bindUpdate;
        RowFactory = rowFactory;
        BindUpdateValues = bindUpdateValues;
        InsertReturningKeyOnly = insertReturningKeyOnly;
        InsertNoReturning = insertNoReturning;
        CopyWriteRow = copyWriteRow;
        InitInsertParameters = initInsertParameters;
        BuildDeleteKeyArray = buildDeleteKeyArray;
        FillUpdateColumnArrays = fillUpdateColumnArrays;
        UpdateColumnArrayElementTypes = updateColumnArrayElementTypes;
        CreateUpdateColumnArrays = createUpdateColumnArrays;
        FillUpsertColumnArrays = fillUpsertColumnArrays;
        UpsertColumnArrayElementTypes = upsertColumnArrayElementTypes;
        CreateUpsertColumnArrays = createUpsertColumnArrays;
        InitUpsertParameters = initUpsertParameters;
        InitUpdateParameters = initUpdateParameters;
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

    /// <summary>构造列名聚合。三个列表做只读快照，调用方可安全复用生成代码的静态数组。
    /// 快照幂等（2026-10-06，10-05 审计 P3）：已是 ReadOnlyCollection 的输入直接复用——
    /// CrudMetadata.Copy() 经本 ctor 重建时不再对注册期已快照的列表二次拷贝。</summary>
    public CrudColumns(IReadOnlyList<string> insert, IReadOnlyList<string> upsert, IReadOnlyList<string> update)
    {
        Insert = Snapshot(insert);
        Upsert = Snapshot(upsert);
        Update = Snapshot(update);
    }

    /// <summary>只读快照幂等化：裸数组/可变列表包装为 ReadOnlyCollection；已是只读快照
    /// （注册期产物，Copy 路径的输入恒为此形态）直接复用原引用。</summary>
    private static System.Collections.ObjectModel.ReadOnlyCollection<string> Snapshot(IReadOnlyList<string> source)
        => source is System.Collections.ObjectModel.ReadOnlyCollection<string> snapshot
            ? snapshot
            : Array.AsReadOnly(source.ToArray());
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
    /// <summary>v5.6：仅设置预分配 UPDATE 参数 Value 的委托（批量 UPDATE 参数池路径，
    /// 零 CreateParameter 分配）。旧版生成器模型程序集为 null，消费方应回退逐行绑定。</summary>
    public readonly Action<DbParameter[], object, int>? BindUpdateValues;
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
    /// <summary>v5.6.0：INSERT ... RETURNING 只回主键（判定条件与消费路径见 CrudBindings.InsertReturningKeyOnly）。</summary>
    public readonly bool InsertReturningKeyOnly;
    /// <summary>PL-3：INSERT 不读返回（判定条件与消费路径见 CrudBindings.InsertNoReturning）。</summary>
    public readonly bool InsertNoReturning;
    /// <summary>PL-3.2：仅设置预分配 UPSERT 参数 Value 的委托（批量 UPSERT 参数池路径，
    /// 与 BindUpsert 同列序）。旧版生成器缺省 false 走读返回路径。</summary>
    public readonly Action<DbParameter[], object, int>? BindUpsertValues;
    /// <summary>O1（2026-10-01）：二进制 COPY 定型行写入委托（判定条件与消费路径见 CrudBindings.CopyWriteRow）。</summary>
    public readonly Action<IBinaryRowSink, object>? CopyWriteRow;
    /// <summary>B21（2026-10-01）：INSERT 池 DbType 一次性初始化委托（判定条件与消费路径见 CrudBindings.InitInsertParameters）。</summary>
    public readonly Action<DbParameter[], int>? InitInsertParameters;
    /// <summary>UNNEST-1（2026-10-02）：主键数组构造器（判定条件与消费路径见 CrudBindings.BuildDeleteKeyArray）。</summary>
    public readonly Func<IReadOnlyList<object>, int, int, Array?>? BuildDeleteKeyArray;
    /// <summary>UNNEST 阶段 B：逐列类型化数组填充器（判定条件与消费路径见 CrudBindings.FillUpdateColumnArrays）。</summary>
    public readonly Action<IReadOnlyList<object>, int, int, Array[], int>? FillUpdateColumnArrays;
    /// <summary>UNNEST 阶段 B：逐列数组元素类型（判定条件与消费路径见 CrudBindings.UpdateColumnArrayElementTypes）。</summary>
    public readonly IReadOnlyList<Type>? UpdateColumnArrayElementTypes;
    /// <summary>UNNEST 阶段 B：逐列类型化数组分配器（判定条件与消费路径见 CrudBindings.CreateUpdateColumnArrays）。</summary>
    public readonly Func<int, Array[]>? CreateUpdateColumnArrays;
    /// <summary>UNNEST 阶段 B：UPSERT 逐列数组填充器（判定条件与消费路径见 CrudBindings.FillUpsertColumnArrays）。</summary>
    public readonly Action<IReadOnlyList<object>, int, int, Array[], int>? FillUpsertColumnArrays;
    /// <summary>UNNEST 阶段 B：UPSERT 逐列数组元素类型（判定条件与消费路径见 CrudBindings.UpsertColumnArrayElementTypes）。</summary>
    public readonly IReadOnlyList<Type>? UpsertColumnArrayElementTypes;
    /// <summary>UNNEST 阶段 B：UPSERT 逐列数组分配器（判定条件与消费路径见 CrudBindings.CreateUpsertColumnArrays）。</summary>
    public readonly Func<int, Array[]>? CreateUpsertColumnArrays;
    /// <summary>N4（2026-10-04 全量复读）：UPSERT 池 DbType 一次性初始化委托（判定条件与消费路径见 CrudBindings.InitUpsertParameters）。</summary>
    public readonly Action<DbParameter[], int>? InitUpsertParameters;
    /// <summary>N4（2026-10-04 全量复读）：UPDATE 池 DbType 一次性初始化委托（判定条件与消费路径见 CrudBindings.InitUpdateParameters）。</summary>
    public readonly Action<DbParameter[], int>? InitUpdateParameters;

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
        BindUpsertValues = bindings.BindUpsertValues;
        BindUpdate = bindings.BindUpdate;
        BindUpdateValues = bindings.BindUpdateValues;
        RowFactory = bindings.RowFactory;
        // CrudColumns ctor 已做只读快照；这里直接复用，避免二次拷贝。
        InsertColumns = columns.Insert;
        UpsertColumns = columns.Upsert;
        UpdateColumns = columns.Update;
        IncrementVersion = incrementVersion;
        HasDefaultKey = hasDefaultKey;
        InsertBinderValidated = insertBinderValidated;
        InsertReturningKeyOnly = bindings.InsertReturningKeyOnly;
        InsertNoReturning = bindings.InsertNoReturning;
        CopyWriteRow = bindings.CopyWriteRow;
        InitInsertParameters = bindings.InitInsertParameters;
        BuildDeleteKeyArray = bindings.BuildDeleteKeyArray;
        FillUpdateColumnArrays = bindings.FillUpdateColumnArrays;
        // 只读快照（与 CrudColumns / ColumnNames 同纪律）：片段传入的是生成代码的静态数组裸引用。
        // 快照幂等（2026-10-06）：Copy() 路径的输入已是注册期 ReadOnlyCollection，直接复用不二次拷贝。
        UpdateColumnArrayElementTypes = SnapshotElementTypes(bindings.UpdateColumnArrayElementTypes);
        CreateUpdateColumnArrays = bindings.CreateUpdateColumnArrays;
        FillUpsertColumnArrays = bindings.FillUpsertColumnArrays;
        UpsertColumnArrayElementTypes = SnapshotElementTypes(bindings.UpsertColumnArrayElementTypes);
        CreateUpsertColumnArrays = bindings.CreateUpsertColumnArrays;
        InitUpsertParameters = bindings.InitUpsertParameters;
        InitUpdateParameters = bindings.InitUpdateParameters;
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

    /// <summary>深拷贝——<b>新增字段必须同步本方法</b>：漏传在快照层不可见（拷贝后的实例字段为
    /// default），会让优化静默失效或行为改变（PERF_MANAGED_DISCIPLINE 第九节的 Copy 路径检查单）。
    /// </summary>
    internal CrudMetadata Copy()
        => new(Sqls,
            new CrudBindings(BindInsert, BindInsertValues, BindUpsert, BindUpdate, RowFactory, BindUpdateValues,
                InsertReturningKeyOnly, InsertNoReturning, BindUpsertValues, CopyWriteRow, InitInsertParameters,
                BuildDeleteKeyArray, FillUpdateColumnArrays, UpdateColumnArrayElementTypes,
                CreateUpdateColumnArrays, FillUpsertColumnArrays, UpsertColumnArrayElementTypes,
                CreateUpsertColumnArrays, InitUpsertParameters, InitUpdateParameters),
            new CrudColumns(InsertColumns, UpsertColumns, UpdateColumns),
            IncrementVersion, HasDefaultKey, InsertBinderValidated);

    /// <summary>元素类型表只读快照幂等化（同 CrudColumns.Snapshot——注册期已快照的输入
    /// 在 Copy() 路径直接复用，不二次拷贝；2026-10-06，10-05 审计 P3）。</summary>
    private static System.Collections.ObjectModel.ReadOnlyCollection<Type>? SnapshotElementTypes(IReadOnlyList<Type>? source)
    {
        if (source is null)
            return null;
        if (source is System.Collections.ObjectModel.ReadOnlyCollection<Type> snapshot)
            return snapshot;
        return Array.AsReadOnly(source.ToArray());
    }
}
