using Microsoft.Extensions.Logging;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>ITM-763(r21)：[SensitiveData] 脱敏端到端——**真实执行路径**验证（非手工构造参数）。
/// <para>背景：r20 的实现把掩码写进生成器 binder 的参数 SourceColumn，但 binder 只服务
/// CRUD/Bulk 路径（不经过拦截器），拦截器只看 QueryBuilder 参数（无掩码）——信道断路，
/// 单测用手工 SourceColumn 掩盖了断路。本轮载体迁移为注册表 SensitiveColumnMasks →
/// QueryBuilder.Set 登记 → QueryContext.SensitiveParameterMasks → AuditInterceptor。</para></summary>
public sealed class SensitiveDataMaskingE2ETests
{
    internal sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync(
        ILogger auditLogger, FormattableString tableDdl)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });
        // 注意传 FormattableString 原样执行——经 string 中转再 $"{...}" 会把整条 DDL
        // 变成单个插值洞（SQL 退化为 "@p0"，即本轮调试中 "near @p0" 的真实来源）
        await session.ExecuteAsync(tableDdl);
        session.AddInterceptor(new AuditInterceptor(auditLogger, logParameters: true));
        return session;
    }

    [Test]
    public async Task UpdateViaSet_SensitiveColumn_MaskedInAuditLog()
    {
        // 端到端：From<T>().Set(敏感列, 值).Where(...).ExecuteNonQueryAsync()
        // → 拦截器日志中该参数值必须为掩码、不得出现明文
        var logger = new RecordingLogger();
        await using DataSession<SqliteProvider> session = await CreateSessionAsync(logger,
            $"CREATE TABLE masking_e2e (id INTEGER PRIMARY KEY, name TEXT NOT NULL, secret_value TEXT NOT NULL)");
        await session.InsertAsync(new MaskingE2EEntity { Name = "row1", SensitiveValue = "hunter2-real-secret" });

        await session.From<MaskingE2EEntity>()
            .Set(e => e.SensitiveValue, "new-secret-value")
            .Where($"id = {1L}")
            .ExecuteNonQueryAsync();

        string audit = string.Join("\n", logger.Messages);
        // 敏感参数被掩码替代
        await Assert.That(audit.Contains("***MASKED***", StringComparison.Ordinal)).IsTrue();
        // 明文绝不进日志参数面
        await Assert.That(audit.Contains("new-secret-value", StringComparison.Ordinal)).IsFalse();

        // 值本身已正确写入数据库（脱敏只影响日志，不影响执行）
        MaskingE2EEntity? reloaded = await session.GetAsync<MaskingE2EEntity>(1L);
        await Assert.That(reloaded is not null).IsTrue();
        await Assert.That(reloaded!.SensitiveValue).IsEqualTo("new-secret-value");
    }

    [Test]
    public async Task UpdateViaSet_NormalColumn_NotMasked()
    {
        // 对照：非敏感列的参数值照常出现在审计日志
        var logger = new RecordingLogger();
        await using DataSession<SqliteProvider> session = await CreateSessionAsync(logger,
            $"CREATE TABLE masking_e2e (id INTEGER PRIMARY KEY, name TEXT NOT NULL, secret_value TEXT NOT NULL)");
        await session.InsertAsync(new MaskingE2EEntity { Name = "row1", SensitiveValue = "s" });

        await session.From<MaskingE2EEntity>()
            .Set(e => e.Name, "renamed-plain-value")
            .Where($"id = {1L}")
            .ExecuteNonQueryAsync();

        string audit = string.Join("\n", logger.Messages);
        await Assert.That(audit.Contains("renamed-plain-value", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task CustomMask_IsUsedFromRegistry()
    {
        // [SensitiveData(Mask = "###")] 的自定义掩码经注册表传递到日志
        var logger = new RecordingLogger();
        await using DataSession<SqliteProvider> session = await CreateSessionAsync(logger,
            $"CREATE TABLE masking_custom (id INTEGER PRIMARY KEY, token TEXT NOT NULL)");
        await session.InsertAsync(new CustomMaskEntity { Token = "seed" });

        await session.From<CustomMaskEntity>()
            .Set(e => e.Token, "plain-token")
            .Where($"id = {1L}")
            .ExecuteNonQueryAsync();

        string audit = string.Join("\n", logger.Messages);
        await Assert.That(audit.Contains("###", StringComparison.Ordinal)).IsTrue();
        await Assert.That(audit.Contains("plain-token", StringComparison.Ordinal)).IsFalse();
    }
}

#region Test Entities
[Table("masking_e2e")]
internal sealed partial class MaskingE2EEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = "";
    [Column("secret_value")]
    [SensitiveData]
    public string SensitiveValue { get; set; } = "";
}

[Table("masking_custom")]
internal sealed partial class CustomMaskEntity
{
    [Key]
    public long Id { get; set; }
    [Column("token")]
    [SensitiveData(Mask = "###")]
    public string Token { get; set; } = "";
}
#endregion
