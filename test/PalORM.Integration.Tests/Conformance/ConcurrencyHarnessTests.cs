namespace PalORM.Integration.Tests.Conformance;

/// <summary>M0 基建的「门禁能失败」验证（修订版方案 §4.2）。
/// <para>每个坏输入都必须让对账器报红；只跑正常输入的全绿不构成判别力证据。</para>
/// <para><b>并行纪律</b>：<c>Harness_ResetProcessState_ClearsDefaultQueryCache</c> 读写进程级默认
/// 查询缓存，故本类标 <c>[NotInParallel]</c>。</para></summary>
[NotInParallel]
public sealed class ConcurrencyHarnessTests
{
    private const int Workers = 2;
    private const int RowsPerWorker = 3;
    private const int ExpectedRows = Workers * RowsPerWorker;
    private const string Marker = "harness-selftest";

    private static async ValueTask<SharedSqliteDb> CreateSeededAsync(CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs_harness", ct).ConfigureAwait(false);
        try
        {
            await ConcurrencyRowVerifier.CreateTableAsync(shared, ct).ConfigureAwait(false);
            for (int worker = 0; worker < Workers; worker++)
                for (int seq = 0; seq < RowsPerWorker; seq++)
                    await ConcurrencyRowVerifier.InsertRowAsync(
                        shared, worker * RowsPerWorker + seq + 1, worker, seq, Marker, ct).ConfigureAwait(false);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [Test]
    public async Task VerifyAllRows_CleanSeed_ReportsNoDifference()
    {
        await using SharedSqliteDb shared = await CreateSeededAsync().ConfigureAwait(false);
        IReadOnlyList<string> differences = await ConcurrencyRowVerifier.VerifyAllRowsAsync(
            shared, ExpectedRows, Workers, RowsPerWorker, Marker).ConfigureAwait(false);
        await Assert.That(differences).IsEmpty();
    }

    [Test]
    public async Task VerifyAllRows_MissingRow_ReportsDifference()
    {
        await using SharedSqliteDb shared = await CreateSeededAsync().ConfigureAwait(false);
        await shared.ExecuteRawAsync("DELETE FROM cs_shared_rows WHERE Id = 4").ConfigureAwait(false);
        IReadOnlyList<string> differences = await ConcurrencyRowVerifier.VerifyAllRowsAsync(
            shared, ExpectedRows, Workers, RowsPerWorker, Marker).ConfigureAwait(false);
        await Assert.That(differences.Count).IsGreaterThan(0);
        await Assert.That(string.Join("\n", differences)).Contains("写者 1 缺 Seq=0");
    }

    [Test]
    public async Task VerifyAllRows_ForeignMarker_ReportsDifference()
    {
        await using SharedSqliteDb shared = await CreateSeededAsync().ConfigureAwait(false);
        await shared.ExecuteRawAsync("UPDATE cs_shared_rows SET Marker = 'other-case' WHERE Id = 1").ConfigureAwait(false);
        IReadOnlyList<string> differences = await ConcurrencyRowVerifier.VerifyAllRowsAsync(
            shared, ExpectedRows, Workers, RowsPerWorker, Marker).ConfigureAwait(false);
        await Assert.That(string.Join("\n", differences)).Contains("Marker=other-case");
    }

    [Test]
    public async Task VerifyAllRows_OutOfRangeOwner_ReportsDifference()
    {
        await using SharedSqliteDb shared = await CreateSeededAsync().ConfigureAwait(false);
        await shared.ExecuteRawAsync("UPDATE cs_shared_rows SET Owner = 99 WHERE Id = 1").ConfigureAwait(false);
        IReadOnlyList<string> differences = await ConcurrencyRowVerifier.VerifyAllRowsAsync(
            shared, ExpectedRows, Workers, RowsPerWorker, Marker).ConfigureAwait(false);
        await Assert.That(string.Join("\n", differences)).Contains("Owner=99 越界");
    }

    [Test]
    public async Task VerifyAllRows_TotalCountMismatch_ReportsDifference()
    {
        await using SharedSqliteDb shared = await CreateSeededAsync().ConfigureAwait(false);
        IReadOnlyList<string> differences = await ConcurrencyRowVerifier.VerifyAllRowsAsync(
            shared, ExpectedRows + 1, Workers, RowsPerWorker, Marker).ConfigureAwait(false);
        await Assert.That(string.Join("\n", differences)).Contains($"行数 {ExpectedRows}，期望 {ExpectedRows + 1}");
    }

    [Test]
    public async Task Harness_ResetProcessState_ClearsDefaultQueryCache()
    {
        CacheStore.Set("cs-harness-probe", "value");
        ConcurrencyHarness.ResetProcessState();
        bool found = CacheStore.TryGet<string>("cs-harness-probe", out _);
        await Assert.That(found).IsFalse();
    }
}
