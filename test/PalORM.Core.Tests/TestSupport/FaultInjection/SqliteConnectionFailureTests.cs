using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>SQLite 连接判死判据锁定（FLT4-1，2026-11-09）。
/// <para>背景：原判据精确匹配 "Connection is not open"，而驱动真实消息是
/// "ExecuteNonQuery can only be called when the connection is open."（2026-11-09 实测）——
/// 判据从不命中，连接判死自愈在生产环境失效。本用例用真实消息锁定修复：撤掉 Contains 修复即红。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class SqliteConnectionFailureTests
{
    [Test]
    public async Task RealDriverMessage_IsJudgedAsConnectionFailure()
    {
        // 实测消息（Microsoft.Data.Sqlite 11.0.0-rc.2，连接关闭后 ExecuteNonQuery）
        var real = new InvalidOperationException("ExecuteNonQuery can only be called when the connection is open.");
        await Assert.That(SqliteProvider.IsConnectionFailure(real)).IsTrue();
    }

    [Test]
    public async Task UnrelatedInvalidOperationIsNotConnectionFailure()
    {
        var other = new InvalidOperationException("Something else went wrong");
        await Assert.That(SqliteProvider.IsConnectionFailure(other)).IsFalse();
    }
}
