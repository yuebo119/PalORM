using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>B 层能力契约执行（方案 §5.6）。
/// <para>10 条能力契约每条一个独立用例（2026-11-03 参数化改造，见《测试机制重设计方案》P0）：
/// 失败时报告里直接标出契约 ID，不再把多条失败拼成一个字符串；删掉一条契约即在报告里少一个
/// 用例，稳定性判据可见。</para>
/// <para>元数据自检单列一条：两个契约目录（能力 10 条 + 方言 20 条）合起来检查 ID 全局唯一、
/// 前缀合规、前置/后置/来源非空、来源可追溯。它防的是"新加契约忘了填来源"或"复制粘贴后 ID 重复"
/// 这类元数据退化。</para></summary>
public sealed class CapabilityContractTests
{
    private static readonly Dictionary<string, ContractEntry> ById =
        ContractCatalog.Capabilities.ToDictionary(static contract => contract.Id, StringComparer.Ordinal);

    /// <summary>数据源：每条契约一个用例。ID 作参数，报告里的 displayName 即含契约 ID。</summary>
    public static IEnumerable<string> ContractIds() => ById.Keys;

    [Test]
    [MethodDataSource(nameof(ContractIds))]
    public async Task CapabilityContract_Holds(string contractId)
    {
        await ById[contractId].Verify($"capability_{contractId}");
    }

    [Test]
    public async Task CapabilityCatalog_MetadataIsWellFormed()
    {
        List<string> problems = [];
        var ids = new HashSet<string>(StringComparer.Ordinal);

        CheckEntries(
            ContractCatalog.Capabilities.Select(static c => (c.Id, c.Precondition, c.Postcondition, c.Source)),
            "C-", ids, problems);
        CheckRationale(
            ContractCatalog.Capabilities.Select(static c => (c.Id, c.Source)), "能力契约", problems);

        // 2026-11-03：C-PAGE-01 已并入方言契约目录的 D-PAGE-01，剩余 10 条都是方言契约目录没有的
        //（JOIN-01/02、CTE-01、GROUP-01、WINDOW-01、MULTI-01、RAW-01、SPROC-01、TX-03、BULKUP-02）。
        if (ContractCatalog.Capabilities.Count != 10)
            problems.Add($"能力契约条数为 {ContractCatalog.Capabilities.Count}，方案要求 10");

        // 方言契约目录（20 条 × 三方言）过同一套元数据判据。ID 与能力契约目录共用同一个
        // 集合，两目录出现同名条目即报；Source 在此之前只写不验（2026-11-03 补）。
        // 泛型目录三份实例的条目表由同一份源码生成，校验 SQLite 一份即可代表全部。
        List<DialectContract> dialectContracts = [.. DialectContractCatalog<SqliteProvider>.Capabilities];
        CheckEntries(
            dialectContracts.Select(static c => (c.Id, c.Precondition, c.Postcondition, c.Source)),
            "D-", ids, problems);
        CheckRationale(
            dialectContracts.Select(static c => (c.Id, c.Source)), "方言契约", problems);

        if (dialectContracts.Count != 20)
            problems.Add($"方言契约条数为 {dialectContracts.Count}，方案要求 20");

        await Assert.That(string.Join(" | ", problems)).IsEmpty();
    }

    /// <summary>逐条检查 ID 唯一、前缀、三段文本非空。两个契约目录共用。</summary>
    private static void CheckEntries(
        IEnumerable<(string Id, string Precondition, string Postcondition, string Source)> entries,
        string expectedPrefix,
        HashSet<string> ids,
        List<string> problems)
    {
        foreach ((string id, string precondition, string postcondition, string source) in entries)
        {
            if (!ids.Add(id)) problems.Add($"{id}: ID 重复");
            if (!id.StartsWith(expectedPrefix, StringComparison.Ordinal))
                problems.Add($"{id}: 前缀不是 {expectedPrefix}");
            if (string.IsNullOrWhiteSpace(precondition)) problems.Add($"{id}: 前置为空");
            if (string.IsNullOrWhiteSpace(postcondition)) problems.Add($"{id}: 后置为空");
            if (string.IsNullOrWhiteSpace(source)) problems.Add($"{id}: 来源为空");
        }
    }

    /// <summary>来源字段必须可追溯到 API参考.md / ADR / reviewed:（判据在 ContractRationale）。</summary>
    private static void CheckRationale(
        IEnumerable<(string Id, string Source)> entries,
        string label,
        List<string> problems)
    {
        List<string> missing = ContractRationale.Validate(entries);
        if (missing.Count > 0)
            problems.Add($"{label}缺合理性依据 {missing.Count} 条：{string.Join(" | ", missing)}");
    }
}
