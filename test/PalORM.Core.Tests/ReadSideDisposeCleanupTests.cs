using System.Collections;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>A2（2026-10-12 审读修复）：会话释放时读连接释放抛异常，不得跳过并行读池的清理。
/// <para>池内物理连接须在会话释放时被释放，不论读连接释放是否失败。</para></summary>
[SuppressMessage("Maintainability", "S3011:AccessibilityBypass",
    Justification = "测试需注入会话私有的读连接并观测并行读池登记，无公开观测面；只读写测试桩，不触碰生产语义。")]
public sealed class ReadSideDisposeCleanupTests
{
    /// <summary>释放即抛异常的读连接桩（模拟读连接释放失败）。</summary>
    private sealed class DisposeFailingConnection(string connectionString) : SqliteConnection(connectionString)
    {
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            throw new InvalidOperationException("read connection dispose failed");
        }
    }

    private static object? ReadField(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void WriteField(object target, string name, object? value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    [Test]
    [SuppressMessage("Reliability", "CA2000",
        Justification = "注入的读连接桩所有权转移给会话，由会话释放路径负责 Dispose（与 CreateAsync 的所有权转移同口径）。")]
    public async Task SessionDispose_ReadConnectionDisposeFails_StillReleasesParallelPool()
    {
        string cs = $"Data Source=rsdc_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var session = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = cs });
        await session.ExecuteAsync($"CREATE TABLE gkcr_items (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)");
        await session.InsertAsync(new GkcrItem { Id = 1, Name = "row-1" });

        // 并行读作用域保持打开：池连接须在会话释放时仍存活，才能观测清理行为
        _ = session.ForParallelReads();
        _ = await session.GetAsync<GkcrItem>(1L);
        var pool = (IList)ReadField(session, "_parallelReadConnections")!;
        var pooled = (SqliteConnection)pool[0]!;

        WriteField(session, "_readConnection", new DisposeFailingConnection(cs));

        Exception? disposeFailure = null;
        try { await session.DisposeAsync(); }
        catch (Exception exception) { disposeFailure = exception; }

        await Assert.That(disposeFailure).IsNotNull();
        await Assert.That(pooled.State).IsEqualTo(ConnectionState.Closed);
    }
}
