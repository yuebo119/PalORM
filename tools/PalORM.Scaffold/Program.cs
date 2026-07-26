using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using PalORM.Scaffold;

// === 参数解析 ===
// 用法：
//   dotnet run -- <connection-string> [--dialect sqlite|pg|mysql] [--namespace NS] [--output DIR]
//   或位置参数：dotnet run -- <connection-string> [namespace]
if (args.Length < 1)
{
    Console.Error.WriteLine("PalORM Scaffold — 三 Provider schema → C# entity generator");
    Console.Error.WriteLine("Usage: dotnet run -- <connection-string> [--dialect sqlite|pg|mysql] [--namespace NS] [--output DIR]");
    Console.Error.WriteLine("  --dialect     sqlite (默认) | pg | mysql");
    Console.Error.WriteLine("  --namespace   生成的命名空间（默认 Models，或读 PALORM_SCAFFOLD_NAMESPACE）");
    Console.Error.WriteLine("  --output      输出目录（默认 stdout，每张表一个 class）");
    return 1;
}

string connectionString = args[0];
string dialectArg = ParseOption(args, "--dialect") ?? "sqlite";
string targetNamespace = ParseOption(args, "--namespace")
    ?? Environment.GetEnvironmentVariable("PALORM_SCAFFOLD_NAMESPACE")
    ?? "Models";
string? outputDir = ParseOption(args, "--output");  // null = stdout

// 兼容旧位置参数：args[1] 不以 -- 开头则当 namespace
if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) && !args.Contains("--namespace"))
    targetNamespace = args[1];

// 支持方言别名：pg/postgres/postgresql → PostgreSql；mysql/my → MySql；sqlite/sql → Sqlite
SchemaDialect dialect = dialectArg.ToLowerInvariant() switch
{
    "sqlite" or "sql" => SchemaDialect.Sqlite,
    "pg" or "postgres" or "postgresql" => SchemaDialect.PostgreSql,
    "mysql" or "my" => SchemaDialect.MySql,
    _ => throw new ArgumentException($"无效的 dialect: '{dialectArg}'。支持: sqlite | pg | mysql")
};

// === 选择 ISchemaProvider + 打开连接 ===
ISchemaProvider provider = dialect switch
{
    SchemaDialect.Sqlite => new SqliteSchemaProvider(),
    SchemaDialect.PostgreSql => new PostgreSqlSchemaProvider(),
    SchemaDialect.MySql => new MySqlSchemaProvider(),
    _ => throw new InvalidOperationException($"Unsupported dialect: {dialect}")
};

if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
    Directory.CreateDirectory(outputDir);

using DbConnection connection = CreateConnection(dialect, connectionString);
await connection.OpenAsync().ConfigureAwait(false);

IReadOnlyList<SchemaTable> tables = await provider.GetTablesAsync(connection).ConfigureAwait(false);
if (tables.Count == 0)
{
    Console.Error.WriteLine($"警告：未在 {dialect} 数据库中发现任何用户表。");
    return 0;
}

foreach (SchemaTable table in tables)
{
    string code = EntityGenerator.Generate(table, dialect, targetNamespace);
    if (string.IsNullOrEmpty(outputDir))
    {
        Console.WriteLine(code);
    }
    else
    {
        string className = EntityGenerator.ToPascalCase(table.Name);
        string path = Path.Combine(outputDir, $"{className}.cs");
        await File.WriteAllTextAsync(path, code).ConfigureAwait(false);
        Console.WriteLine($"生成: {path}");
    }
}

return 0;

// === 辅助：--key value 参数解析（默认 null）===
static string? ParseOption(string[] args, string key)
{
    int idx = Array.IndexOf(args, key);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

// === 辅助：按方言创建连接 ===
static DbConnection CreateConnection(SchemaDialect dialect, string connectionString)
    => dialect switch
    {
        SchemaDialect.Sqlite => new SqliteConnection(connectionString),
        SchemaDialect.PostgreSql => new NpgsqlConnection(connectionString),
        SchemaDialect.MySql => new MySqlConnection(connectionString),
        _ => throw new InvalidOperationException($"Unsupported dialect: {dialect}")
    };
