using System.Reflection;

namespace PalORM.Core.Tests;

/// <summary>加固 4（2026-10-13 池化全面审计）：池化归还复位清单的机械化守卫。
/// <para><b>防什么</b>：未来给 DataSession/SessionOperationState 新增可变实例字段而忘了
/// <c>DataSession.ReturnToPoolAsync</c> 的瞬态复位（或有意保留却未登记白名单）——租期 A 的
/// 状态串味到租期 B。人工纪律防不住跨月的新增字段，本守卫把"忘登记"变成测试失败。
/// 判定：非 static、非 readonly 的实例字段必须落在两张表之一——
/// <see cref="ResetOnReturn"/>（归还时复位：瞬态语义）或
/// <see cref="KeptAcrossRentals"/>（跨租期保留：槽/池标记/计数器等，含保留理由）。
/// readonly 字段免检（构造后不可变，无从串味）。</para>
/// <para><b>与 Reset 对账</b>：ResetOnReturn 集合与 ReturnToPoolAsync 的复位语句一一对应
///（新增复位字段时两处同步改；若测试红在这张表，说明实现漏写了复位语句）。</para></summary>
public sealed class PoolResetCompletenessTests
{
    /// <summary>归还时复位的瞬态字段（与 ReturnToPoolAsync 的赋值语句逐项对应）。
    /// _interceptors 是 readonly List（内容级复位，Clear+回填），字段本身免检。</summary>
    private static readonly HashSet<string> ResetOnReturn =
    [
        "_ignoreFilters", "_tenantId", "_tenantScopeEntry", "_isolationLevel",
        "_options", "_resilience",
    ];

    /// <summary>跨租期保留的字段（含理由）：
    /// _reusableInsert/Update/GetByKey/Delete 与 _querySlot（复用槽：池化的核心收益）；
    /// 四个 *_WriteOps/_getByKeyReadOps 计数器（晋升判据，保留使已晋升状态跨租期持续）；
    /// _parallelReadConnections/_idleParallelReadConnections（并行池，作用域退出即空，
    /// Park 门禁保证归还时无作用域）；_materializedCapacity（容量启发，租期无关近似）；
    /// _readConnection（会话级读连接，AcquireReadConnectionAsync 自带死连接重建）；
    /// _readConnProvider/_readConnReturner（W1 惰性读路由委托：绑定 this 的方法组，
    /// 幂等无状态，保留使第二租期起 From&lt;T&gt; 免重建）；
    /// _lastMigrationSkippedIndexes（DDL 幂等跳过缓存：类型级事实，与租期无关——守卫测试
    /// 首跑即抓到该遗漏，登记实证）；_pool/_poolBaseOptions/_poolBaseResilience（池标记与
    /// 复位基准，永不复位）。</summary>
    private static readonly HashSet<string> KeptAcrossRentals =
    [
        "_reusableInsert", "_reusableUpdate", "_reusableGetByKey", "_reusableDelete",
        "_querySlot",
        "_insertWriteOps", "_updateWriteOps", "_getByKeyReadOps", "_deleteWriteOps",
        "_parallelReadConnections", "_idleParallelReadConnections",
        "_materializedCapacity", "_readConnection",
        "_readConnProvider", "_readConnReturner",
        "_lastMigrationSkippedIndexes",
        "_pool", "_poolBaseOptions", "_poolBaseResilience",
    ];

    /// <summary>SessionOperationState 可变字段的跨租期归属全集（按复位机制分组，全部显式登记）：
    /// Park 收口复位：_transaction/_transactionOperationOwner/_externalTransaction/_state（驻留置 3）；
    /// Exit/事务流退出路径归零（Park 前置校验保证这些能量在驻留时点恒为零——非零即抛，不可能带残留进池）：
    /// _isActive/_activeOperation/_activeOperationOwner/_activeTransaction/_transactionOwner/
    /// _transactionResources/_transactionCompleting/_parallelReadScopes/_activeReadLeases/_readLeasesDrained；
    /// 归还复位（DataSession.ReturnToPoolAsync）：DefersPostCommitActions/_postCommitBackfills（Discard）；
    /// 生命周期终态：_disposeTask（仅真实释放路径产生，驻留会话恒 null）；
    /// 跨租期保留：CompletedOps（原 OwnerPrewarmed，深挖轮改计数——AsyncLocal 槽值随异步流
    /// 不跨租期，保留计数使第二租期起免 EC 拷贝且立即达预热门槛）、
    /// DisposeWaitTimeout（测试注入的实例配置，与租期无关）。</summary>
    private static readonly HashSet<string> OperationStateClassified =
    [
        // Park 收口复位
        "_transaction", "_transactionOperationOwner", "_externalTransaction", "_state",
        // Exit/事务流退出归零（Park 前置校验兜底）
        "_isActive", "_activeOperation", "_activeOperationOwner", "_activeTransaction",
        "_transactionOwner", "_transactionResources", "_transactionCompleting",
        "_parallelReadScopes", "_activeReadLeases", "_readLeasesDrained",
        // 归还复位（ReturnToPoolAsync）
        "DefersPostCommitActions", "_postCommitBackfills",
        // 生命周期终态
        "_disposeTask",
        // 跨租期保留
        "CompletedOps", "DisposeWaitTimeout",
    ];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S3011",
        Justification = "守卫测试只读取私有字段名做白名单对账（防池化复位遗漏），不读写字段值、不绕过任何不变式。")]
    [Test]
    public async Task DataSession_MutableFields_AreAllClassifiedForPoolReturn()
    {
        string[] unclassified = [.. typeof(DataSession<PalORM.Sqlite.SqliteProvider>)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static f => !f.IsInitOnly && !f.IsLiteral)
            .Select(static f => f.Name)
            .Where(name => !ResetOnReturn.Contains(name) && !KeptAcrossRentals.Contains(name))];

        await Assert.That(string.Join(", ", unclassified))
            .IsEmpty()
            .Because("DataSession 新增可变字段必须归入池化归还两表之一（并同步 ReturnToPoolAsync 或登记保留理由）："
                + "归 ResetOnReturn 的必须写复位语句；归 KeptAcrossRentals 的必须在注释里给保留理由。"
                + "未分类字段 = 租期串味漏洞。");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S3011",
        Justification = "守卫测试只读取私有字段名做白名单对账（防池化复位遗漏），不读写字段值、不绕过任何不变式。")]
    [Test]
    public async Task SessionOperationState_MutableFields_AreAllClassifiedForPoolReturn()
    {
        // 属性 backing field（<X>k__BackingField）归一为属性名后对账
        string[] unclassified = [.. typeof(SessionOperationState)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static f => !f.IsInitOnly && !f.IsLiteral)
            .Select(static f => f.Name.EndsWith(">k__BackingField", StringComparison.Ordinal)
                ? f.Name[1..^">k__BackingField".Length]
                : f.Name)
            .Where(name => !OperationStateClassified.Contains(name))];

        await Assert.That(string.Join(", ", unclassified))
            .IsEmpty()
            .Because("SessionOperationState 新增可变字段必须显式登记 OperationStateClassified"
                + "（注释里写清复位机制：Park 收口/退出路径归零/归还复位/终态/保留）——"
                + "否则驻留态残留会进入下一租期。");
    }

    [Test]
    public async Task ResetOnReturn_MatchesReturnToPoolImplementation()
    {
        // 对账第二面：ResetOnReturn 表里的每个字段，ReturnToPoolAsync 源码里必须有对应复位语句
        // （防"表里登记了、实现忘写"的假绿）。读源码文件做文本级核对——比反射拿方法体可靠。
        string sourcePath = FindSourceFile("DataSession.cs");
        string source = await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false);

        string[] missing = [.. ResetOnReturn
            .Where(name => !source.Contains($"Volatile.Write(ref {name}", StringComparison.Ordinal)
                && !source.Contains($"{name} = ", StringComparison.Ordinal))];
        await Assert.That(string.Join(", ", missing))
            .IsEmpty()
            .Because("ResetOnReturn 表中的字段在 ReturnToPoolAsync 里没有复位语句——表与实现漂移。");
    }

    private static string FindSourceFile(string fileName)
    {
        // 测试程序集工作目录向上回溯找 src 源码（仓库布局固定 src/PalORM.Core/*.cs）
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "src", "PalORM.Core", fileName)))
            directory = Path.GetDirectoryName(directory);
        return directory is null
            ? throw new InvalidOperationException($"未找到 src/PalORM.Core/{fileName}（从 {AppContext.BaseDirectory} 回溯）")
            : Path.Combine(directory, "src", "PalORM.Core", fileName);
    }
}
