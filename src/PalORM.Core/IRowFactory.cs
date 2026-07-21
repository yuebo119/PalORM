using System.Data.Common;

namespace PalORM;

/// <summary>行物化工厂接口（v3.1 起为兼容遗留路径保留——热路径不再使用）。
/// <para><b>v3.1 性能优化</b>: 源生成器 emit 已迁移到 <c>static readonly Func&lt;DbDataReader, T&gt;</c> 委托字段，
/// 调用方直接委托 invoke 替代接口虚分发（每行从 ~0.3μs vtable+间接跳转降至 ~0.05μs 直接调用）。
/// 本接口保留以兼容潜在的外部实现；运行时核心调用点（QueryBuilder、DataSession、GridReader 等）
/// 全部改为委托。</para>
/// <para><typeparamref name="T"/> 声明为 <c>out</c> 协变——仅作为 Read 返回类型出现，从不作为方法参数；
/// 允许 <c>IRowFactory&lt;DerivedEntity&gt;</c> 隐式转换为 <c>IRowFactory&lt;BaseEntity&gt;</c>。</para></summary>
/// <typeparam name="T">标注 [Table] 的实体类型</typeparam>
public interface IRowFactory<out T> where T : class, new()
{
    /// <summary>从 DbDataReader 当前行读取实体。源生成器实现——零反射、零装箱（值类型列）。</summary>
    T Read(DbDataReader reader);
}
