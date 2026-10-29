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

        if (ContractCatalog.Capabilities.Count != 30)
            problems.Add($"契约条数为 {ContractCatalog.Capabilities.Count}，方案要求 30");

        await Assert.That(string.Join(" | ", problems)).IsEmpty();
    }
}