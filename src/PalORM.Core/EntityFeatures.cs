namespace PalORM;

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
