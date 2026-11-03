namespace PalORM.Integration.Tests.Conformance;

/// <summary>B 层能力契约执行器（方案 §5.6）。
/// <para>30 条契约在独立库上逐条验证；失败收集后一次性报出，每条消息带契约 ID，
/// 失败时能一步跳到 <see cref="ContractCatalog"/> 的条目。</para>
/// <para>契约元数据自检单列一条：ID 唯一、前缀合规、前置/后置/来源非空。它防的是
/// "新加契约忘了填来源"或"复制粘贴后 ID 重复"这类元数据退化。</para></summary>
public sealed class CapabilityContractTests
{
    private static readonly string[] AllowedPrefixes = ["C-"];

    [Test]
    public async Task AllCapabilityContracts_Hold()
    {
        List<string> failures = [];
        int index = 0;
        foreach (ContractEntry contract in ContractCatalog.Capabilities)
        {
            index++;
            try
            {
                await contract.Verify($"capability_{index:D2}");
            }
            catch (Exception ex)
            {
                failures.Add($"{contract.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        await Assert.That(string.Join("\n", failures)).IsEmpty();
    }

    [Test]
    public async Task CapabilityCatalog_MetadataIsWellFormed()
    {
        List<string> problems = [];
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (ContractEntry contract in ContractCatalog.Capabilities)
        {
            if (!ids.Add(contract.Id)) problems.Add($"{contract.Id}: ID 重复");
            if (!AllowedPrefixes.Any(prefix => contract.Id.StartsWith(prefix, StringComparison.Ordinal)))
                problems.Add($"{contract.Id}: 前缀不是 C-");
            if (string.IsNullOrWhiteSpace(contract.Precondition)) problems.Add($"{contract.Id}: 前置为空");
            if (string.IsNullOrWhiteSpace(contract.Postcondition)) problems.Add($"{contract.Id}: 后置为空");
            if (string.IsNullOrWhiteSpace(contract.Source)) problems.Add($"{contract.Id}: 来源为空");
        }

        // 2026-11-03 去重后 30 → 11：19 条的断言与 DialectContractCatalog 的 D-* 等价
        //（SAVE-01 少一条 IsNotNull，但 null 时 row.Name 访问照样失败），D 层三方言都跑故
        // SQLite 侧覆盖不降；保留 D 层没有的 10 条
        //（JOIN-01/02、CTE-01、GROUP-01、WINDOW-01、MULTI-01、RAW-01、SPROC-01、TX-03、
        // BULKUP-02）+ 断言更全的 PAGE-01（descending 默认 true 只在 C 层断言）。
        if (ContractCatalog.Capabilities.Count != 11)
            problems.Add($"契约条数为 {ContractCatalog.Capabilities.Count}，方案要求 11");

        // 合理性依据：Source 必须指向文档锚点、ADR 或 reviewed 结论（见 ContractRationale）
        List<string> noRationale = ContractRationale.Validate(ContractCatalog.Capabilities);
        if (noRationale.Count > 0)
            problems.Add($"缺合理性依据 {noRationale.Count} 条：{string.Join(" | ", noRationale)}");

        await Assert.That(string.Join(" | ", problems)).IsEmpty();
    }
}