namespace PalORM;

/// <summary>数据库实体映射注解。所有注解均为编译时元数据，源生成器在编译期读取，零运行时开销。</summary>

/// <summary>标记实体类对应的数据库表名。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TableAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    /// <summary>保留的 Schema 配置。当前源生成器通过 PALORM011 拒绝使用。</summary>
    public string? Schema { get; init; }
    /// <summary>保留的 Database 配置。当前源生成器通过 PALORM011 拒绝使用。</summary>
    public string? Database { get; init; }
}

/// <summary>标记属性对应的数据库列名。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ColumnAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public string? TypeName { get; init; }
    public StoreAs StoreAs { get; init; }
}

/// <summary>列存储策略。</summary>
public enum StoreAs { Default, AsInt32, AsInt64, AsString }

/// <summary>标记主键属性。支持 long、Guid、string；Ulid 必须配置编译期值转换器。
/// int/long 主键默认视为数据库自增（排除出 INSERT 列）；
/// 雪花 ID 等应用侧赋值的数值主键设置 <c>AutoIncrement = false</c> 关闭。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class KeyAttribute : Attribute
{
    /// <summary>数值主键是否由数据库自增生成（默认 true）。false = 应用侧赋值，进入 INSERT 列。</summary>
    public bool AutoIncrement { get; init; } = true;
}

/// <summary>排除非 DB 属性。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class NotMappedAttribute : Attribute { }

/// <summary>外键约束定义。OnDelete 默认 NO ACTION。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ForeignKeyAttribute(string referencedTable, string referencedColumn) : Attribute
{
    public string ReferencedTable { get; } = referencedTable;
    public string ReferencedColumn { get; } = referencedColumn;
    public DeleteAction OnDelete { get; init; }
}

/// <summary>外键删除行为。</summary>
public enum DeleteAction { NoAction, Cascade, SetNull, Restrict }

/// <summary>乐观锁并发令牌。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ConcurrencyCheckAttribute : Attribute { }

/// <summary>INSERT 时跳过该列。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class IgnoreOnInsertAttribute : Attribute { }

/// <summary>NOT NULL 约束。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class RequiredAttribute : Attribute { }

/// <summary>DB 端默认值表达式。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class DefaultValueAttribute(string expression) : Attribute
{
    public string Expression { get; } = expression;
}

/// <summary>DB 端自动更新的时间戳/行版本列。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class TimestampAttribute : Attribute { }

/// <summary>标记软删除实体。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SoftDeleteAttribute : Attribute { }

/// <summary>日志脱敏标记。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class SensitiveDataAttribute : Attribute
{
    public string Mask { get; init; } = "***MASKED***";
}

/// <summary>标记由数据库维护的计算列；生成写入命令会排除该列，完整读取仍会回填。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ComputedAttribute(string expression) : Attribute
{
    public string Expression { get; } = expression;
}

/// <summary>配置编译期值转换器；不得与 <see cref="OwnedJsonAttribute"/> 同用。转换器须为顶级非泛型类型，具有无参构造，并实现 Provider 类型非 nullable 的匹配 <see cref="IValueConverter{TModel, TProvider}"/>；跨程序集使用时类型和构造函数必须为 public。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ConverterAttribute(Type converterType) : Attribute
{
    public Type ConverterType { get; } = converterType;
}

/// <summary>自定义类型与 DB 类型间的编译时安全转换。</summary>
public interface IValueConverter<TModel, TProvider>
{
    TProvider ToProvider(TModel value);
    TModel FromProvider(TProvider value);
}

/// <summary>多租户感知标记。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TenantAwareAttribute : Attribute { }

/// <summary>JSON 列标记。字符串属性按原始 JSON 存储；对象属性必须指定 STJ 源生成上下文。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class OwnedJsonAttribute : Attribute
{
    /// <summary>初始化字符串形式的原始 JSON 列。</summary>
    public OwnedJsonAttribute() { }

    /// <summary>初始化使用指定 STJ 源生成上下文的对象 JSON 列。</summary>
    /// <param name="contextType">派生自 <c>JsonSerializerContext</c> 的源生成上下文类型。</param>
    public OwnedJsonAttribute(Type contextType) => ContextType = contextType;

    /// <summary>获取对象 JSON 列使用的 STJ 源生成上下文类型。</summary>
    public Type? ContextType { get; }
}

/// <summary>复合索引定义（可标注多次）。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class IndexAttribute(string name, params string[] columns) : Attribute
{
    public string Name { get; } = name;
    public string[] Columns { get; } = columns;
    public bool Unique { get; init; }
}

/// <summary>唯一索引定义。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class UniqueAttribute : Attribute { }

/// <summary>外置 SQL 文件标记。源生成器读取 .sql 文件生成静态 SQL 常量。
/// 文件内可使用 -- @pg / -- @mysql / -- @sqlite / -- @all 条件分支。
/// <para><b>增量编译限制（ITM-313）</b>: .sql 文件内容不参与增量比较——只改 .sql 不改 .cs 时
/// 生成物复用缓存（陈旧 SQL），需 Rebuild 或触碰标记特性的 .cs 文件强制重新生成。</para></summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SqlFileAttribute : Attribute
{
    public string Path { get; }
    /// <summary>可选：指定 Provider 名称（PostgreSql/MySql/Sqlite）。省略则使用 -- @all 段。</summary>
    public string? Provider { get; init; }
    public SqlFileAttribute(string path) => Path = path;
}

/// <summary>保留的数据库 Schema 注解。当前源生成器通过 PALORM011 拒绝使用。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SchemaAttribute : Attribute
{
    public string Name { get; }
    public SchemaAttribute(string name) => Name = name;
}

/// <summary>保留的数据库名称注解。当前源生成器通过 PALORM011 拒绝使用。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DatabaseAttribute : Attribute
{
    public string Name { get; }
    public DatabaseAttribute(string name) => Name = name;
}

/// <summary>SQL 模板预编译标记。执行时在参数绑定后调用 DbCommand.PrepareAsync。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SqlTemplateAttribute : Attribute
{
    public string Name { get; }
    public SqlTemplateAttribute(string name) => Name = name;
}
