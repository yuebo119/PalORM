using System.Globalization;
using Microsoft.Data.Sqlite;

if (args.Length < 1)
{
    Console.WriteLine("PalORM Scaffold — SQLite schema → C# entity generator");
    Console.WriteLine("Usage: dotnet run -- <connection-string> [namespace]");
    Console.WriteLine("  connection-string  SQLite 连接串（如 Data Source=my.db）");
    Console.WriteLine("  namespace          生成的命名空间（默认 Models，或读 PALORM_SCAFFOLD_NAMESPACE 环境变量）");
    return 1;
}

string connectionString = args[0];
// 命令行参数优先；其次 PALORM_SCAFFOLD_NAMESPACE 环境变量；最后内置默认值。
string targetNamespace = args.Length > 1
    ? args[1]
    : Environment.GetEnvironmentVariable("PALORM_SCAFFOLD_NAMESPACE") ?? "Models";

using var connection = new SqliteConnection(connectionString);
connection.Open();

using var tablesCommand = connection.CreateCommand();
tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
using var tableReader = tablesCommand.ExecuteReader();

while (tableReader.Read())
{
    string tableName = tableReader.GetString(0);
    string className = ToPascalCase(tableName);

    Console.WriteLine($"using PalORM;\n");
    Console.WriteLine($"namespace {targetNamespace};\n");
    Console.WriteLine($"[Table(\"{tableName}\")]");
    Console.WriteLine($"public partial class {className}");
    Console.WriteLine("{");

    using var columnCommand = connection.CreateCommand();
    columnCommand.CommandText = $"PRAGMA table_info({tableName})";
    using var columnReader = columnCommand.ExecuteReader();

    bool isFirstColumn = true;
    while (columnReader.Read())
    {
        string columnName = columnReader.GetString(1);
        string dbType = columnReader.GetString(2).ToUpper(CultureInfo.InvariantCulture);
        int isPrimaryKey = Convert.ToInt32(columnReader.GetInt64(5));

        string csharpType = MapDbType(dbType);
        string propertyName = ToPascalCase(columnName);

        string columnAttribute = "";
        if (!string.Equals(columnName, propertyName, StringComparison.OrdinalIgnoreCase))
        {
            columnAttribute = $"[Column(\"{columnName}\")] ";
        }

        if (isPrimaryKey > 0 && isFirstColumn)
        {
            Console.WriteLine($"    [Key] public {csharpType} {propertyName} {{ get; set; }}");
            isFirstColumn = false;
        }
        else
        {
            Console.WriteLine($"    {columnAttribute}public {csharpType} {propertyName} {{ get; set; }} = default!;");
        }
    }

    Console.WriteLine("}");
    Console.WriteLine();
}

return 0;

static string MapDbType(string dbType)
{
    if (dbType.StartsWith("INT", StringComparison.Ordinal))
    {
        return "long";
    }
    if (dbType.StartsWith("REAL", StringComparison.Ordinal)
        || dbType.StartsWith("FLOAT", StringComparison.Ordinal)
        || dbType.StartsWith("DOUB", StringComparison.Ordinal))
    {
        return "decimal";
    }
    if (dbType == "BLOB")
    {
        return "byte[]";
    }
    return "string";
}

static string ToPascalCase(string name)
{
    string[] parts = name.Split('_');
    char[] result = new char[name.Length];
    int index = 0;
    for (int i = 0; i < parts.Length; i++)
    {
        string part = parts[i];
        if (part.Length == 0)
        {
            continue;
        }
        result[index++] = char.ToUpper(part[0], CultureInfo.InvariantCulture);
        for (int j = 1; j < part.Length; j++)
        {
            result[index++] = char.ToLower(part[j], CultureInfo.InvariantCulture);
        }
    }
    return new string(result, 0, index);
}
