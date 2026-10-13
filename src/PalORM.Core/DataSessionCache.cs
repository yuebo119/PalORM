using System.Collections.Concurrent;

namespace PalORM;

/// <summary>非泛型共享缓存--避免泛型 DataSession&lt;TProvider&gt; 中的 static 字段
/// 不跨 TProvider 共享（Sonar S2743）。v4.1 极致降内存优化。</summary>
internal static class DataSessionCache
{
    /// <summary>R3（2026-10-12）：per-(Type, Dialect) 缓存 Upsert 列清单与冲突子句——
    /// BuildUpsertSqlShape 原每次调用 1 LINQ 迭代器 + 2 string.Join + 插值（与
    /// QuotedColumnsCache 同纪律；值由编译期注册表元数据唯一决定）。空 updateColumns 的
    /// 不支持形态不缓存（异常逐次抛出，语义不变）。</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (Type EntityType, SqlDialect Dialect), (string QuotedColumns, string ConflictClause)> UpsertShapeCache = new();

    /// <summary>R3（2026-10-12）：per-(Type, Dialect) 缓存逐条 UPDATE 池化路径的每行参数数
    ///（原每次调用建 probe 命令数参；哨兵语义随 B9 先例转为每进程一次）。</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (Type EntityType, SqlDialect Dialect), int> UpdateRowParamsCache = new();

    /// <summary>per-(Type, Dialect) 缓存 selectColumns 字符串，消除每次 QuoteIdentifier + string.Join。
    /// <para><b>注意形态</b>：本缓存存的是<b>不带表名限定</b>的裸列清单（<c>"id", "status"</c>），
    /// 供 GetAllAsync/GetAsync 的 FROM 单表查询使用。需要表名限定的调用点（QueryBuilder 的
    /// SELECT 列表，JOIN 下防 ambiguous column）必须用 <see cref="QualifiedSelectColumnsCache"/>，
    /// 两者键相同但值不同，混用会静默丢掉限定名。</para></summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect), string> SelectColumnsCache = new();

    /// <summary>per-(Type, Dialect) 缓存<b>带表名限定</b>的列清单（<c>"t"."id", "t"."status"</c>），
    /// 供 QueryBuilder.BuildSql 的 SELECT 列表使用（v5.6）。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect), string> QualifiedSelectColumnsCache = new();

    /// <summary>per-(Type, Dialect, softDelete, tenant) 缓存默认过滤的三种拼接形态，消除每次
    /// QuoteIdentifier + 插值。<b>v5.6</b>：值由「条件文本」升级为
    /// <see cref="DefaultFilterForms"/>——三个调用点（COUNT 的裸条件、拼在既有 WHERE 之后的
    /// 追加片段、独立 WHERE 子句）原本各自再做一次插值，现在一次查找同时拿到三种形态。
    ///（G4，2026-10-13 五轮微项包：GetAll/Count 组合句/聚合后缀的<b>语句级</b>缓存已迁
    /// DataSession&lt;TProvider&gt;.SqlSlots&lt;T&gt; 闭式泛型静态槽，本字典仍服务过滤形态的
    /// 构建期真源。）</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect, bool, bool), DefaultFilterForms> FilterFormsCache = new();

    /// <summary>per-Dialect 缓存租户过滤追加片段（" AND {quote(tenant_id)} = @__tenant0"，
    /// M1，v5.6.0）——片段只含方言标识符与 const 参数名，方言内恒定；原先四处调用点每次
    /// 2 次 QuoteIdentifier + 插值。供 BulkDeleteAsync（语句随批次占位符变化，仅后缀可缓存）
    /// 及 SqlSlots 的 UPDATE/DELETE 租户全句构建期复用。</summary>
    internal static readonly ConcurrentDictionary<SqlDialect, string> TenantAppendFragmentCache = new();

    /// <summary>租户护栏（2026-10-06）：per-(Type, Dialect) 缓存 UPSERT 冲突子句 + 租户守卫——
    /// PG/SQLite 在 RETURNING 前插 WHERE，MySQL 逐赋值项包 IF。供 UpsertWithReturningAsync/
    /// UpsertWithMySqlAsync 消费（SaveCoreAsync 单行 upsert 面）。</summary>
    internal static readonly ConcurrentDictionary<(Type, SqlDialect), string> UpsertTenantGuardedSqlCache = new();

    /// <summary>B9（2026-10-01 全 API 逐项轮）：批量 UPDATE 上下文（SET 列集 + 引用表名/主键）
    /// 按 (Type, Dialect) 缓存——生成器三处（SQL/Bind/元数据）一致性是编译期事实，probe 验证
    /// 每进程每类型一次即可；原实现每次调用建 1 个 probe 命令 + BindUpdate 约 setColumnCount 个
    /// 参数对象 + LINQ 引用列构建。租户位不缓存（会话态），由 PrepareBatchUpdateContext 每次现算。
    /// 键空间有限（实体类型 × 3 方言）。</summary>
    internal static readonly ConcurrentDictionary<
        (Type EntityType, SqlDialect Dialect),
        (string[] SetColumns, string QuotedTable, string QuotedPk)> BatchUpdateContextCache = new();

    /// <summary>F2（2026-10-13 全量重读）：BulkDeleteAsync 类型化重载的等同性探针结果缓存——
    /// 键 = (实体类型, 键 CLR 类型)，值 = "生成物 BuildDeleteKeyArray 对该键类型的产出与
    /// 类型化直拷逐位等价"（双值探针，以生成物自身为真源）。键空间有限（实体 × 键类型组合），
    /// 每组合每进程探测一次后定态。</summary>
    internal static readonly ConcurrentDictionary<(Type EntityType, Type KeyType), bool> TypedKeyIdentityCache = new();
}

/// <summary>默认过滤（软删/租户）的三种拼接形态。空过滤时为 <see cref="Empty"/>（三项全空串），
/// 而非 default——后者三项为 null，调用点拼接前取 Length 会 NRE。</summary>
internal readonly record struct DefaultFilterForms(string Condition, string AndFragment, string WhereClause)
{
    /// <summary>无默认过滤（实体既非 [SoftDelete] 也无租户会话）。</summary>
    internal static readonly DefaultFilterForms Empty = new("", "", "");

    /// <summary>由条件文本派生三种形态；空条件映射为 <see cref="Empty"/>。</summary>
    internal static DefaultFilterForms FromCondition(string condition)
        => condition.Length == 0
            ? Empty
            : new DefaultFilterForms(condition, " AND " + condition, " WHERE " + condition);
}
