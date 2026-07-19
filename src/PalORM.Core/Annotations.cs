namespace PalORM;

/// <summary>数据库实体映射注解。所有注解均为编译时元数据，源生成器在编译期读取，零运行时开销。</summary>

/// <summary>标记实体类对应的数据库表名。</summary>
/// <param name="name">数据库表名。方言 Emitter 生成 SQL 时做标识符引用转义（ITM-580：
/// 仅已被运行时拒绝的 legacy 路径为原样）。</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TableAttribute(string name) : Attribute
{
    /// <summary>数据库表名。</summary>
    public string Name { get; } = name;
    /// <summary>保留的 Schema 配置。当前源生成器通过 PALORM011 拒绝使用。</summary>
    public string? Schema { get; init; }
    /// <summary>保留的 Database 配置。当前源生成器通过 PALORM011 拒绝使用。</summary>
    public string? Database { get; init; }
}

/// <summary>标记属性对应的数据库列名。</summary>
/// <param name="name">数据库列名。</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ColumnAttribute(string name) : Attribute
{
    /// <summary>数据库列名。</summary>
    public string Name { get; } = name;
    /// <summary>字符串列最大长度。<b>ITM-549：当前迁移 DDL 未实现</b>——设置会触发 PALORM017 告警，
    /// string 列恒生成 TEXT/VARCHAR(255)。需定长时用原生 DDL 或等待实现。</summary>
    public int? Length { get; init; }
    /// <summary>数值列精度（总位数）。<b>ITM-549：当前未实现</b>（PALORM017 告警）——decimal 恒 DECIMAL(18,6)。</summary>
    public int? Precision { get; init; }
    /// <summary>数值列小数位数。<b>ITM-549：当前未实现</b>（PALORM017 告警），随 <see cref="Precision"/>。</summary>
    public int? Scale { get; init; }
    /// <summary>显式指定数据库列类型。<b>ITM-549：当前未实现</b>（PALORM017 告警），默认类型映射不被覆盖。</summary>
    public string? TypeName { get; init; }
    /// <summary>列存储策略（枚举按整数/字符串存储）。<b>ITM-553：当前未实现</b>（PALORM017 告警）——
    /// 枚举恒按默认映射存储（TEXT）。</summary>
    public StoreAs StoreAs { get; init; }
}

/// <summary>列存储策略。</summary>
public enum StoreAs
{
    /// <summary>按类型默认映射存储。</summary>
    Default,
    /// <summary>按 32 位整数存储。</summary>
    AsInt32,
    /// <summary>按 64 位整数存储。</summary>
    AsInt64,
    /// <summary>按字符串存储。</summary>
    AsString
}

/// <summary>标记主键属性。支持 long/int/short/byte、Guid、string；Ulid 必须配置编译期值转换器。
/// 数值主键（long/int/short/byte）默认视为数据库自增（排除出 INSERT 列）；
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
/// <param name="referencedTable">被引用的表名。</param>
/// <param name="referencedColumn">被引用表中的列名。</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ForeignKeyAttribute(string referencedTable, string referencedColumn) : Attribute
{
    /// <summary>被引用的表名。</summary>
    public string ReferencedTable { get; } = referencedTable;
    /// <summary>被引用表中的列名。</summary>
    public string ReferencedColumn { get; } = referencedColumn;
    /// <summary>被引用行删除时的行为，默认 <see cref="DeleteAction.NoAction"/>。</summary>
    public DeleteAction OnDelete { get; init; }
}

/// <summary>外键删除行为。</summary>
public enum DeleteAction
{
    /// <summary>不做处理（NO ACTION）。</summary>
    NoAction,
    /// <summary>级联删除引用行（CASCADE）。</summary>
    Cascade,
    /// <summary>将引用列置为 NULL（SET NULL）。</summary>
    SetNull,
    /// <summary>存在引用时禁止删除（RESTRICT）。</summary>
    Restrict
}

/// <summary>乐观锁并发令牌。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ConcurrencyCheckAttribute : Attribute { }

/// <summary>INSERT 时跳过该列。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class IgnoreOnInsertAttribute : Attribute { }

/// <summary>NOT NULL 约束。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class RequiredAttribute : Attribute { }

/// <summary>DB 端默认值表达式。
/// <para><b>当前未实现</b>（ITM-580）: 表达式不参与 DDL 生成（MigrationEmitter 不读取本注解），
/// 标注后 PALORM017 编译期告警。列默认值请直接写入 [SqlFile] 迁移脚本或建表后 ALTER。</para></summary>
/// <param name="expression">SQL 默认值表达式（如 <c>CURRENT_TIMESTAMP</c>）。当前仅作元数据保留。</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class DefaultValueAttribute(string expression) : Attribute
{
    /// <summary>SQL 默认值表达式。</summary>
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
    /// <summary>日志中替换真实值的掩码文本。</summary>
    public string Mask { get; init; } = "***MASKED***";
}

/// <summary>标记由数据库维护的计算列；生成写入命令会排除该列，完整读取仍会回填。</summary>
/// <param name="expression">列的 SQL 计算表达式，<b>原样进入三方言 DDL（ITM-541）</b>——不经
/// 引用转义、不做方言翻译。表达式内的列名/函数须在目标数据库合法；跨方言部署时注意
/// 同一表达式可能仅在部分方言有效（如字符串拼接 SQLite/PG 用 <c>||</c>、MySQL 用 <c>CONCAT</c>）。</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ComputedAttribute(string expression) : Attribute
{
    /// <summary>列的 SQL 计算表达式。</summary>
    public string Expression { get; } = expression;
}

/// <summary>配置编译期值转换器；不得与 <see cref="OwnedJsonAttribute"/> 同用。转换器须为顶级非泛型类型，具有无参构造，并实现 Provider 类型非 nullable 的匹配 <see cref="IValueConverter{TModel, TProvider}"/>；跨程序集使用时类型和构造函数必须为 public。</summary>
/// <param name="converterType">实现 <see cref="IValueConverter{TModel, TProvider}"/> 的转换器类型。</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ConverterAttribute(Type converterType) : Attribute
{
    /// <summary>值转换器类型。</summary>
    public Type ConverterType { get; } = converterType;
}

/// <summary>自定义类型与 DB 类型间的编译时安全转换。</summary>
public interface IValueConverter<TModel, TProvider>
{
    /// <summary>将模型值转换为数据库存储值。</summary>
    TProvider ToProvider(TModel value);
    /// <summary>将数据库存储值还原为模型值。</summary>
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
/// <param name="name">索引名。</param>
/// <param name="columns">按顺序参与索引的列名。</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class IndexAttribute(string name, params string[] columns) : Attribute
{
    /// <summary>索引名。</summary>
    public string Name { get; } = name;
    /// <summary>按顺序参与索引的列名。</summary>
    public string[] Columns { get; } = columns;
    /// <summary>是否为唯一索引。</summary>
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
    /// <summary>.sql 文件路径（相对项目根，需以 AdditionalFiles 加入编译）。</summary>
    public string Path { get; }
    /// <summary>可选：指定 Provider 名称（PostgreSql/MySql/Sqlite）。省略则使用 -- @all 段。</summary>
    public string? Provider { get; init; }
    /// <summary>初始化外置 SQL 文件标记。</summary>
    /// <param name="path">.sql 文件路径。</param>
    public SqlFileAttribute(string path) => Path = path;
}

/// <summary>保留的数据库 Schema 注解。当前源生成器通过 PALORM011 拒绝使用。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SchemaAttribute : Attribute
{
    /// <summary>Schema 名。</summary>
    public string Name { get; }
    /// <summary>初始化 Schema 注解。</summary>
    /// <param name="name">Schema 名。</param>
    public SchemaAttribute(string name) => Name = name;
}

/// <summary>保留的数据库名称注解。当前源生成器通过 PALORM011 拒绝使用。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DatabaseAttribute : Attribute
{
    /// <summary>数据库名。</summary>
    public string Name { get; }
    /// <summary>初始化数据库名称注解。</summary>
    /// <param name="name">数据库名。</param>
    public DatabaseAttribute(string name) => Name = name;
}

/// <summary>SQL 模板预编译标记。执行时在参数绑定后调用 DbCommand.PrepareAsync。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SqlTemplateAttribute : Attribute
{
    /// <summary>模板名。</summary>
    public string Name { get; }
    /// <summary>初始化 SQL 模板预编译标记。</summary>
    /// <param name="name">模板名。</param>
    public SqlTemplateAttribute(string name) => Name = name;
}
