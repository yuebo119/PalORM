using System.Globalization;
using System.Text;

namespace PalORM.Scaffold;

/// <summary>从 <see cref="SchemaTable"/> 生成 PalORM 实体类 C# 代码。
/// <para>与方言解耦——只接收 SchemaTable DTO，不知道来源是 SQLite/PG/MySQL。
/// 类型映射委托给 <see cref="TypeMapper"/>。</para>
/// <para><b>生成规则</b>：</para>
/// <list type="bullet">
/// <item>表名 → 类名（snake_case → PascalCase）</item>
/// <item>列名 → 属性名（snake_case → PascalCase）</item>
/// <item>列名与属性名不同时加 [Column("原名")]</item>
/// <item>PK 列加 [Key]，自增 PK 加 [Key(AutoIncrement = true)]</item>
/// <item>生成物带 #nullable enable；可空列（含引用类型 string/byte[]）加 ? 后缀——驱动 IsDBNull 守卫</item>
/// <item>引用类型默认值 = default!（非 null 模式）</item>
/// <item>值类型可空列加 ? 后缀</item>
/// </list></summary>
internal static class EntityGenerator
{
    /// <summary>生成单个实体类的 C# 代码。</summary>
    public static string Generate(SchemaTable table, SchemaDialect dialect, string targetNamespace)
    {
        string className = ToPascalCase(table.Name);
        var sb = new StringBuilder();
        // #nullable enable：可空注解驱动 PalORM 生成物的 IsDBNull 守卫（ITM-312）——
        // 缺省上下文里可空列读 NULL 会绕过守卫直接抛 SqlNullValueException
        sb.Append("#nullable enable\nusing PalORM;\n\nnamespace ").Append(targetNamespace).Append(";\n\n");
        sb.Append("[Table(\"").Append(table.Name).Append("\")]\n");
        sb.Append("public partial class ").Append(className).Append("\n{\n");

        bool hasKey = false;
        for (int i = 0; i < table.Columns.Count; i++)
        {
            SchemaColumn col = table.Columns[i];
            string propertyName = ToPascalCase(col.Name);
            (string csharpType, bool isReferenceType) = TypeMapper.Map(col.DbType, dialect);

            // 列名与属性名不同时加 [Column("原名")]
            bool needsColumnAttr = !string.Equals(col.Name, propertyName, StringComparison.Ordinal);
            // 可空列加 ? 后缀——引用（string/byte[]）与值类型一致：#nullable enable 下
            // 可空注解是 RowFactory 生成 IsDBNull 守卫的判据，非空注解读 NULL 会炸
            bool needsNullableSuffix = col.IsNullable && !col.IsPrimaryKey;

            string fullType = needsNullableSuffix ? csharpType + "?" : csharpType;

            sb.Append("    ");
            if (col.IsPrimaryKey && !hasKey)
            {
                // 自增 PK 标记 AutoIncrement=true
                sb.Append("[Key").Append(col.IsAutoIncrement ? "(AutoIncrement = true)" : "").Append("] ");
                hasKey = true;
            }
            else if (needsColumnAttr)
            {
                sb.Append("[Column(\"").Append(col.Name).Append("\")] ");
            }
            sb.Append("public ").Append(fullType).Append(' ').Append(propertyName).Append(" { get; set; }");
            if (isReferenceType)
                sb.Append(" = default!;");
            sb.Append('\n');
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>snake_case → PascalCase（如 user_name → UserName）。
    /// 也处理已 PascalCase 输入（不改大小写）。</summary>
    internal static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        // 不含下划线且首字母大写——已是 PascalCase，直接返回
        if (!name.Contains('_', StringComparison.Ordinal) && char.IsUpper(name[0]))
            return name;

        string[] parts = name.Split('_');
        var result = new char[name.Length];
        int index = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0) continue;
            result[index++] = char.ToUpper(part[0], CultureInfo.InvariantCulture);
            for (int j = 1; j < part.Length; j++)
                result[index++] = char.ToLower(part[j], CultureInfo.InvariantCulture);
        }
        return new string(result, 0, index);
    }
}
