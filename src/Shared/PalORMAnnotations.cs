namespace PalORM;

/// <summary>PalORM 注解契约（Core 与 SourceGen 共享的单一真源）。
/// <para>SourceGen 是 netstandard2.0，不能引用 Core；本文件通过 Compile Link 同时编译进
/// PalORM.Core 与 PalORM.SourceGen。两部分内容：</para>
/// <list type="bullet">
/// <item>外键删除行为枚举——SourceGen 侧使用不同的内部类型名，避免测试程序集同时引用
/// 两个程序集时出现 CS0433 类型歧义；</item>
/// <item>注解短名常量——SourceGen 只能按名称匹配注解，收敛为常量避免两侧字符串漂移。</item>
/// </list></summary>
#if PALORM_SOURCEGEN
internal enum SourceGenDeleteAction
#else
public enum DeleteAction
#endif
{
    /// <summary>不做处理（NO ACTION）。</summary>
    NoAction = 0,

    /// <summary>级联删除引用行（CASCADE）。</summary>
    Cascade = 1,

    /// <summary>将引用列置为 NULL（SET NULL）。</summary>
    SetNull = 2,

    /// <summary>存在引用时禁止删除（RESTRICT）。</summary>
    Restrict = 3
}

/// <summary>PalORM 注解短名常量（Core 与 SourceGen 共享的单一真源）。
/// <para>SourceGen 在 netstandard2.0 下不能引用 Core 的注解类型，只能按名称匹配；
/// 把这些名称收敛为常量，避免生成器与运行时两侧字符串漂移。Core 侧不直接消费本类，
/// 但通过 Compile Link 编译进来，保证两侧看到同一份名称表。</para></summary>
internal static class PalORMAttributeNames
{
    internal const string Namespace = "PalORM";
    internal const string Table = "Table";
    internal const string Column = "Column";
    internal const string Key = "Key";
    internal const string NotMapped = "NotMapped";
    internal const string ForeignKey = "ForeignKey";
    internal const string ConcurrencyCheck = "ConcurrencyCheck";
    internal const string IgnoreOnInsert = "IgnoreOnInsert";
    internal const string Required = "Required";
    internal const string DefaultValue = "DefaultValue";
    internal const string Timestamp = "Timestamp";
    internal const string SoftDelete = "SoftDelete";
    internal const string SensitiveData = "SensitiveData";
    internal const string Computed = "Computed";
    internal const string Converter = "Converter";
    internal const string TenantAware = "TenantAware";
    internal const string OwnedJson = "OwnedJson";
    internal const string Index = "Index";
    internal const string Unique = "Unique";
    internal const string SqlFile = "SqlFile";
    internal const string Schema = "Schema";
    internal const string Database = "Database";
    internal const string Projection = "Projection";
    internal const string SqlTemplate = "SqlTemplate";
}
