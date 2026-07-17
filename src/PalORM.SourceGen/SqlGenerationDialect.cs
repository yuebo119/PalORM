namespace PalORM.SourceGen;

internal enum SqlGenerationDialect
{
    Sqlite,
    PostgreSql,
    MySql
}

internal static class SqlGeneration
{
    internal static string QuoteIdentifier(string identifier, SqlGenerationDialect dialect)
    {
        char quote = dialect == SqlGenerationDialect.MySql ? '`' : '"';
        string escaped = identifier.Replace(
            quote.ToString(),
            new string(quote, 2));
        return $"{quote}{escaped}{quote}";
    }
}
