using System.Data.Common;

namespace PalORM;

/// <summary>行物化工厂接口。源生成器为每个 [Table] 类自动实现，编译时生成 Read() 方法，零反射。
/// <para><typeparamref name="T"/> 声明为 <c>out</c> 协变——仅作为 Read 返回类型出现，从不作为方法参数；
/// 允许 <c>IRowFactory&lt;DerivedEntity&gt;</c> 隐式转换为 <c>IRowFactory&lt;BaseEntity&gt;</c>。</para></summary>
/// <typeparam name="T">标注 [Table] 的实体类型</typeparam>
public interface IRowFactory<out T> where T : class, new()
{
    /// <summary>从 DbDataReader 当前行读取实体。源生成器实现——零反射、零装箱（值类型列）。</summary>
    T Read(DbDataReader reader);
}
