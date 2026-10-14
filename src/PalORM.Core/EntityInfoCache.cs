namespace PalORM;

/// <summary>刀1（2026-10-13 二轮全量重读）：实体合并元数据（<see cref="EntityRuntimeInfo"/>）的
/// 每类型单例缓存载体——把热路径（From&lt;T&gt;/GetByKey/GetAll/Count/聚合/Query）每次操作的
/// FrozenDictionary 查找收敛为零（注册后命中静态字段）。
/// <para><b>为什么可以缓存</b>：类型级注册表条目单调不可变——Register 对重复类型抛异常，
/// 已注册实体的元数据在进程内恒定（ITM-758 对 live 读安全性的同一论证）；缓存只在注册表
/// 查找<b>成功后</b>写入，未注册类型的失败路径不缓存（注册后首查入缓）。</para>
/// <para><b>为什么独立文件/非泛型宿主</b>：缓存键是实体类型 T 本身（与 TProvider 无关——
/// 注册表全局共享），载体若嵌在 DataSession&lt;TProvider&gt; 内会按 (TProvider, T) 分片出
/// N 份冗余副本（S2743 的本意场景）；写入经静态 <see cref="Store{T}"/>（S2696：静态字段
/// 不由实例方法直写）。</para></summary>
internal static class EntityInfoCache
{
    /// <summary>每实体类型 T 的缓存槽。可变静态字段是缓存语义本体（S2223 抑制）；
    /// 按 T 分片正是缓存的目的而非事故（S2743 抑制）；T 仅经类型参数参与分片、不出现在
    /// 字段类型中（S2326 抑制——分片键就是 T 的闭合类型本身）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S2326",
        Justification = "T 不出现在字段类型中是刻意的：分片键是 T 的闭合类型本身，字段值是全局共享的注册表元数据。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S2743",
        Justification = "静态字段按 T 分片正是本缓存的目的（每实体类型一份），非跨泛型实例共享事故。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S2223",
        Justification = "可变静态字段是惰性缓存的语义本体；写入只在注册表查找成功后发生一次（单调不可变前提）。")]
    internal static class Per<T> where T : class, new()
    {
        /// <summary>缓存槽：null = 未查过（下次走注册表并回填）。</summary>
        public static EntityRuntimeInfo? Info;
    }

    /// <summary>静态写入入口（S2696：实例方法不直写静态字段）——仅在注册表查找成功后调用，
    /// 每类型至多生效一次（竞态下后写覆盖，值同源等价，无害）。</summary>
    internal static void Store<T>(EntityRuntimeInfo info) where T : class, new()
        => Per<T>.Info = info;

    /// <summary>每类型行工厂缓存槽（直查族专用）。与 <see cref="Per{T}"/> 分离：直查族
    ///（QueryAsync/QueryAsyncEnumerable/QueryFirst/QuerySingle）的 T 允许是 [Projection]
    /// 类型——只进 RowFactories 不进 TableNames（实体集），不能走 EntityRuntimeInfo 视图
    ///（投影无表名/CRUD，视图键集不含它们）。同样满足单调不可变前提（RowFactories 注册后
    /// 条目恒定）。抑制理由同 <see cref="Per{T}"/>。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S2326",
        Justification = "T 不出现在字段类型中是刻意的：分片键是 T 的闭合类型本身。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S2743",
        Justification = "静态字段按 T 分片正是本缓存的目的，非跨泛型实例共享事故。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S2223",
        Justification = "可变静态字段是惰性缓存的语义本体；写入只在注册表查找成功后发生一次。")]
    internal static class FactoryPer<T> where T : class, new()
    {
        /// <summary>缓存槽：null = 未查过（下次走 RowFactories 并回填）。</summary>
        public static object? Factory;
    }

    /// <summary>行工厂缓存写入（同 <see cref="Store{T}"/> 口径）。</summary>
    internal static void StoreFactory<T>(object factory) where T : class, new()
        => FactoryPer<T>.Factory = factory;
}
