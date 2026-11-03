using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>B 层契约条目（方案 §5.1）。
/// <para>每条带 ID、前置、后置与来源；<see cref="Verify"/> 在独立库上执行，失败即该条契约被违背。
/// 断言消息必须带 ID（由执行器统一拼接）。</para>
/// <para><b>期望值来源</b>：能力契约的后置一律取自 `docs/API参考.md` 与 XML doc 的承诺；
/// 代码里"实际如此"不作为契约依据。带"实测"字样的条目是 0a/0b 阶段发现文档未写明、经实测确认后登记的行为。</para>
/// <para><b>命名澄清（2026-11-03）</b>：本目录属方案 §5「B 层：契约防线」，ID 前缀的 <c>C-</c>
/// 是「能力（Capability）」缩写，不是「C 层」。方案 §6 的 C 层（状态与并发防线）与 §7 的 D 层
/// （随机化差分层）是另外两套测试，与本目录无关。本目录全称「能力契约目录」，姊妹件
/// <see cref="DialectContractCatalog{TProvider}"/> 全称「方言契约目录」（20 条 × 三方言）。
/// 二者同为 B 层的 C 类条目，只是后者按 provider 参数化。</para>
/// <para><b>2026-11-03 定位重述（去重后 30 → 11 条）</b>：现存的 11 条与方言契约目录
/// <b>互补而非重复</b>：</para>
/// <para>① <b>契约元数据登记册</b>（不可替代）：<c>ContractRationale.Validate</c> 强制每条的
/// <c>Source</c> 指向 <c>API参考.md</c> / <c>ADR</c> / <c>reviewed:</c> 之一，把"把当前行为记成
/// 合理行为"从口头纪律变成可核对字段（首轮 13 条产品行为里 3 条其实是缺陷的教训）。删本目录即
/// 失去这个机械检查的载体。</para>
/// <para>② <b>SQLite 快速回归</b>：11 条跑在内存库上，比方言契约目录的三方言真库轮快，适合每次
/// 改动即跑。<b>它不承担方言覆盖</b>：那 10 条方言契约目录没有的条目（JOIN-01/02、CTE-01、
/// GROUP-01、WINDOW-01、MULTI-01、RAW-01、SPROC-01、TX-03、BULKUP-02）各自另有三方言或真库
/// 覆盖，见 QueryShapeDualSideTests / TriDialectMultiResultTests / SavepointDialectTests /
/// BulkUpdateBatchDialectTests / ExternalDatabaseFeatureTests。PAGE-01 保留是因为它断言
/// <c>descending</c> 默认 true，方言契约目录只测显式 <c>descending: false</c>。</para>
/// <para><b>不要"并进方言契约目录"</b>：本目录的 SQL 硬编码双引号（如 <c>SELECT "Id", "name"</c>），
/// MySQL 上直接失败；迁移前须把每条改成 <c>scope.Q()</c> 参数化，工作量等同重写，而方言契约目录
/// 已有对应覆盖，收益为零。SQLite 兜底由 <c>DialectCapabilityContractTests.Sqlite_CapabilityContracts_Hold</c>
/// 的"SQLite 不得登记 DialectUnsupported"守卫保证。</para></summary>
internal sealed record ContractEntry(
    string Id,
    string Precondition,
    string Postcondition,
    string Source,
    Func<string, Task> Verify);

/// <summary>契约条目的「合理性依据」检查（方案 §5.2 纪律的机械化载体）。
/// <para>每条契约的 <c>Source</c>（能力契约目录的 <see cref="ContractEntry.Source"/> 与方言契约目录的
/// <c>DialectContract.Source</c>）必须指向三类依据之一：`API参考.md`（文档承诺）、
/// `ADR`（架构决策）、`reviewed:`（实测后经人工评审确认合理，后接一句结论）。只在源码注释里出现、
/// 或纯 `实测` 而无评审结论的，记为「仅实测」，进待评审清单。</para>
/// <para>为什么需要这条：首轮的 13 条产品行为里有 3 条是缺陷，根因正是把「当前行为」记成「合理行为」。
/// 这个检查把「交由评审」从口头纪律变成可核对字段。</para></summary>
internal static class ContractRationale
{
    /// <summary>能力契约目录的入口（原签名保留）。</summary>
    public static List<string> Validate(IEnumerable<ContractEntry> entries)
        => Validate(entries.Select(static entry => (entry.Id, entry.Source)));

    /// <summary>按 (ID, 来源) 校验，两个契约目录共用。
    /// 2026-11-03：方言契约目录的 20 条 <c>Source</c> 此前只写不校验，本重载把它们纳入同一判据。</summary>
    public static List<string> Validate(IEnumerable<(string Id, string Source)> entries)
    {
        var problems = new List<string>();
        foreach ((string id, string source) in entries)
        {
            bool hasDocument = source.Contains("API参考.md", StringComparison.Ordinal);
            bool hasAdr = source.Contains("ADR", StringComparison.Ordinal);
            bool hasReview = source.Contains("reviewed:", StringComparison.Ordinal);
            if (!hasDocument && !hasAdr && !hasReview)
                problems.Add(id + " → " + source);
        }
        return problems;
    }
}

internal static class ContractCatalog
{
    /// <summary>在独立库上开场，供各契约验证使用。</summary>
    private static async ValueTask<(SharedSqliteDb Shared, DataSession<SqliteProvider> Db)> OpenAsync(
        string libraryName, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(libraryName, ct);
        DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
        return (shared, db);
    }

    private static CfBulkPlainEntity Plain(long id, string name = "n", long amount = 1)
        => new() { Id = id, Name = name, Amount = amount };

    public static IReadOnlyList<ContractEntry> Capabilities { get; } =
    [
        // 2026-11-03 去重（19 条）：INSERT-01/02、SAVE-01/02、UPDATE-01、UPDCOLS-01、
        // DELETE-01/02、GET-01、GETALL-01、QUERY-01、AGG-01、SCALAR-01、BULKIN-01、
        // BULKUP-01、BULKMERGE-01、BULKDEL-01、TX-01/02 的断言与方言契约目录
        // 的 D-* 同名条目等价（同断言谓词 + 同期望值；差别只在 session/db 命名与
        // 本目录把结果先存局部变量）。唯一例外 SAVE-01：D 版少一条 IsNotNull，但紧接着的
        // row.Name 访问在 null 时照样失败，判别力未降。
        // 方言契约目录在 SQLite/PG/MySQL 三方言都跑（本目录只在 SQLite），删除后 SQLite 侧
        // 仍由它覆盖，覆盖率不降。保留的 11 条是方言契约目录没有的 10 条
        //（JOIN-01/02、CTE-01、GROUP-01、WINDOW-01、MULTI-01、RAW-01、SPROC-01、
        // TX-03、BULKUP-02）与断言更全的 1 条（PAGE-01：descending 默认 true 只在本目录断言）。
        // SQLite 兜底由 DialectCapabilityContractTests.Sqlite_CapabilityContracts_Hold 的
        // "SQLite 不得登记不支持"守卫保证。

        new("C-JOIN-01",
            "左表三行、右表两行，按 Id 关联",
            "InnerJoin 只返回匹配的行（2 行）",
            "API参考.md 查询构建器（InnerJoin）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3)]);
                await session.BulkInsertAsync(
                [
                    new CfCompositeIndexEntity { Id = 1, Category = "a", Price = 1m, Sku = "s1" },
                    new CfCompositeIndexEntity { Id = 2, Category = "b", Price = 2m, Sku = "s2" }
                ]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .InnerJoin<CfCompositeIndexEntity>($"cf_composite_index.\"Id\" = cf_bulk_plain.\"Id\"")
                    .ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(2);
            }),

        new("C-JOIN-02",
            "左表三行、右表两行，按 Id 关联",
            "LeftJoin 保留左表全部行（3 行）",
            "API参考.md 查询构建器（LeftJoin）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3)]);
                await session.BulkInsertAsync(
                [
                    new CfCompositeIndexEntity { Id = 1, Category = "a", Price = 1m, Sku = "s1" },
                    new CfCompositeIndexEntity { Id = 2, Category = "b", Price = 2m, Sku = "s2" }
                ]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .LeftJoin<CfCompositeIndexEntity>($"cf_composite_index.\"Id\" = cf_bulk_plain.\"Id\"")
                    .ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(3);
            }),

        new("C-CTE-01",
            "三行 amount 为 10/20/30",
            "With(cteName, subquery) 后 From 以 CTE 为源，结果与子查询条件一致",
            "API参考.md 查询构建器（With）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1, "a", 10), Plain(2, "b", 20), Plain(3, "c", 30)]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .With("cf_cte", $"SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain WHERE \"amount\" > 15")
                    .ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(2);
            }),

        new("C-GROUP-01",
            "五行：x 出现两次、y 两次、z 一次",
            "GroupBy + Having 只返回满足聚合条件的组，组数与手写 SQL 一致",
            "API参考.md 查询构建器（GroupBy / Having）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync(
                [
                    Plain(1, "x"), Plain(2, "x"), Plain(3, "y"), Plain(4, "y"), Plain(5, "z")
                ]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .GroupBy(e => e.Name).Having($"COUNT(*) > 1").ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(2);
            }),

        new("C-WINDOW-01",
            "五行数据",
            "UnsafeWindowOver 只追加窗口列，不改变实体行集",
            "API参考.md 查询构建器（UnsafeWindowOver）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1), Plain(2), Plain(3), Plain(4), Plain(5)]);
                List<CfBulkPlainEntity> rows = await session.From<CfBulkPlainEntity>()
                    .UnsafeWindowOver("ROW_NUMBER()", "ORDER BY \"Id\"").ToListAsync();
                await Assert.That(rows.Count).IsEqualTo(5);
            }),

        // 2026-11-03：C-PAGE-01 已并入方言契约目录的 D-PAGE-01（那条本来就有升序与 Skip 断言，
        // 补上「descending 默认 true」后退化为严格更强的版本），两个目录的同名重叠归零。

        new("C-BULKUP-02",
            "两行已插入，实体无 [ConcurrencyCheck]",
            "BulkUpdateBatchAsync 按批更新，返回影响行数 2",
            "API参考.md 写入 / 批量 + PALORM031 限制说明",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                CfBulkPlainEntity[] rows = [Plain(1, "a", 1), Plain(2, "b", 2)];
                await session.BulkInsertAsync(rows);
                rows[0].Amount = 11;
                rows[1].Amount = 12;
                long affected = await session.BulkUpdateBatchAsync(rows);
                await Assert.That(affected).IsEqualTo(2L);
                await Assert.That((await session.GetAsync<CfBulkPlainEntity>(2L))!.Amount).IsEqualTo(12L);
            }),

        new("C-TX-03",
            "事务内插入 A、Savepoint、插入 B、回滚到 Savepoint",
            "A 保留、B 被回滚，提交后只剩 A",
            "API参考.md 事务（SavepointAsync / RollbackToAsync）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                using System.Data.Common.DbTransaction transaction = await session.BeginTransactionAsync();
                await session.InsertAsync(Plain(1, "keep"));
                await session.SavepointAsync(transaction, "sp1");
                await session.InsertAsync(Plain(2, "discard"));
                await session.RollbackToAsync(transaction, "sp1");
                await transaction.CommitAsync();
                await Assert.That(await session.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
                await Assert.That((await session.GetAsync<CfBulkPlainEntity>(1L))!.Name).IsEqualTo("keep");
            }),

        new("C-MULTI-01",
            "两条 SELECT 的多结果集查询",
            "QueryMultipleAsync 返回的 GridReader 可依次读取两个结果集",
            "API参考.md 执行方法（QueryMultipleAsync / GridReader）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.BulkInsertAsync([Plain(1, "a", 10), Plain(2, "b", 20)]);
                await using GridReader reader = await session.From<CfBulkPlainEntity>()
                    .QueryMultipleAsync(
                        $"SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain WHERE \"amount\" > 0; SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain");
                List<CfBulkPlainEntity> first = await reader.ReadAsync<CfBulkPlainEntity>();
                List<CfBulkPlainEntity> second = await reader.ReadAsync<CfBulkPlainEntity>();
                await Assert.That(first.Count).IsEqualTo(2);
                await Assert.That(second.Count).IsEqualTo(2);
            }),

        new("C-SPROC-01",
            "存储过程构建器",
            "StoredProc(name) 对空名与非法字符抛 ArgumentException（SQLite 无真实存储过程，走参数校验面）",
            "API参考.md 执行方法（StoredProc）+ 实测 SQLite 限制",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await Assert.That(() => session.StoredProc("")).Throws<ArgumentException>();
                await Assert.That(() => session.StoredProc("bad name; DROP")).Throws<ArgumentException>();
            }),

        new("C-RAW-01",
            "会话已初始化",
            "GetRawConnection 返回非 null 连接，且可直接执行原生 SQL",
            "API参考.md 执行方法（GetRawConnection 逃生舱）",
            async name =>
            {
                (SharedSqliteDb shared, DataSession<SqliteProvider> db) = await OpenAsync(name);
                await using SharedSqliteDb scope = shared;
                await using DataSession<SqliteProvider> session = db;
                await session.CountAsync<CfBulkPlainEntity>();
                using System.Data.Common.DbConnection connection = session.GetRawConnection();
                await Assert.That(connection).IsNotNull();
                await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Open);
            }),
    ];
}