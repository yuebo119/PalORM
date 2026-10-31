namespace PalORM;

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
