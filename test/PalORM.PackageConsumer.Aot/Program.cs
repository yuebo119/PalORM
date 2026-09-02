using PalORM.Sqlite;

namespace PalORM.PackageConsumer.Aot;

[Table("package_consumer")]
internal sealed partial class PackageEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

// 评审 2026-09-02 第二批：SqlFile 包契约用例——包内 buildTransitive targets 必须自动把
// **/*.sql 注入为 AdditionalFiles，编译期嵌入内容（Native AOT 下 const 化零运行时 IO）。
internal static partial class SqlFileQueries
{
    [SqlFile("Queries/Ping.sql")]
    public static partial string GetPingSql();
}

internal static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303",
        Justification = "固定英文文本是 Native AOT smoke test 的机器可读成功标记。")]
    internal static async Task Main()
    {
        string pingSql = SqlFileQueries.GetPingSql();
        if (!pingSql.Contains("SELECT 1 AS ping", System.StringComparison.Ordinal))
            throw new InvalidOperationException(
                "SqlFile content was not embedded: packaged targets failed to supply AdditionalFiles");

        var options = new DbOptions { ConnectionString = "Data Source=:memory:" };
        DataSession<SqliteProvider> db = await DataSession<SqliteProvider>.CreateAsync(options).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.MigrateAsync().ConfigureAwait(false);
            PackageEntity inserted = await db.InsertAsync(new PackageEntity { Name = "package" }).ConfigureAwait(false);
            PackageEntity found = await db.GetAsync<PackageEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Packaged analyzer did not generate runtime metadata");
            if (found.Name != "package")
                throw new InvalidOperationException("Package consumer round trip failed");
        }

        Console.WriteLine("PalORM package consumer AOT verification PASSED");
    }
}
