using System.Data.Common;
using BenchmarkDotNet.Attributes;

namespace PalORM.DapperSuite;

/// <summary>官方 BenchmarkBase 的移植：连接在 GlobalSetup 打开、i 计数器经 Step() 轮转 1..5000。
/// <para>播种挂在 BaseSetup（官方库已预置数据；本套件幂等自建，首个进程付成本）。</para></summary>
[BenchmarkCategory("ORM")]
public abstract class BenchmarkBase
{
    protected DbConnection Connection = null!;
    protected int i;
    protected string Dialect => Database.Dialect;

    protected void BaseSetup()
    {
        i = 0;
        Connection = Database.Open();
        Database.EnsureSeededAsync(Connection).GetAwaiter().GetResult();
    }

    /// <summary>轮转主键 1..5000——每个基准调用查不同行（官方 Step 逐位一致）。</summary>
    protected void Step()
    {
        i++;
        if (i > Database.RowCount) i = 1;
    }

    [GlobalCleanup]
    public virtual void CloseConnection() => Connection?.Dispose();
}
