using System.Data.Common;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>FLT 系列共用夹具：内存 SQLite + 建表 + 可注入故障的会话。
/// <para>建表在**包装之前**用真实连接执行：故障序号从「会话的第一条命令」开始计，
/// 否则 DDL 会白占一个序号，注入计划与测试读起来错位。</para></summary>
internal static class FaultInjectionFixtures
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "夹具：FaultyConnection 由返回的会话持有并在其 DisposeAsync 时释放；会话构造是本方法最后一步")]
    internal static async Task<(DataSession<SqliteProvider> Session, FaultyConnection Wrapper, SqliteConnection Keeper)>
        CreateAsync(Func<SqliteConnection, FaultyConnection>? wrapperFactory = null, string? name = null)
    {
        name ??= $"fault_{Guid.NewGuid():N}";
        string connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using (DbCommand setup = keeper.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE fault_row (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Qty INTEGER NOT NULL)";
            await setup.ExecuteNonQueryAsync();
        }

        FaultyConnection wrapper = wrapperFactory is null
            ? new FaultyConnection(keeper)
            : wrapperFactory(keeper);
        var session = new DataSession<SqliteProvider>(
            wrapper, new DbOptions { ConnectionString = connectionString }, [], null);
        return (session, wrapper, keeper);
    }

    internal static FaultRow Row(string name, int qty) => new() { Name = name, Qty = qty };

    internal static List<FaultRow> Rows(int count)
    {
        var rows = new List<FaultRow>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new FaultRow { Name = $"r{i}", Qty = i });
        }
        return rows;
    }
}

/// <summary>FLT 系列实体：自增主键用于回填与批量返回路径。</summary>
[Table("fault_row")]
internal sealed partial class FaultRow
{
    [Key] public long Id { get; set; }

    [Column("Name")] public string Name { get; set; } = "";

    [Column("Qty")] public int Qty { get; set; }
}
