using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>表达式构建器的端到端冒烟——<b>目的是 AOT 覆盖而非功能回归</b>。
/// <para>背景（审计 TEST-012 更正）：历史上三个 AOT 验收程序只走 CRUD/Bulk/OwnedJson/软删路径，
/// <c>OrderBy</c>/<c>ThenBy</c>/<c>Select</c>/<c>GroupBy</c>/<c>Having</c>/<c>WhereIn</c>/<c>WhereNotIn</c>/
/// <c>Set</c>/<c>Include</c>/<c>ThenInclude</c>/<c>With(CTE)</c>/<c>UnsafeWindowOver</c>/
/// <c>ForUpdate</c>/<c>WithCache</c>/<c>AsPrepared</c>/<c>Tag</c> 曾全部未被 AOT 程序调用；
/// 该缺口已由 <c>PalORM.AotTest/Program.cs</c> 的 VerifyExpressionBuildersAsync 补上
/// （JIT/AOT 镜像对）。本用例保留 JIT 侧同源验证：迭代快、失败定位准，
/// 与 AOT 侧共同构成"构建器原生运行验证"的双面防线。</para>
/// <para>断言口径：能执行出正确结果的必须断言结果；只影响 SQL 的断言 SQL 片段。
/// 两者都必要——纯 SQL 断言会让"能生成但执行炸"漏网，纯结果断言会让窗口函数/锁子句没进过语句。</para></summary>
internal sealed class ExpressionBuilderSmokeTests
{
    [Test]
    public async Task ExpressionBuilders_ExecuteCorrectly_OnSqlite()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        var parent = await db.InsertAsync(new SmokeParent { Name = "p0" });
        // 逐条而非循环：循环内 DB 调用会被 PALORM005（N+1 检测）拦下——那是分析器的正确行为，
        // 冒烟用例只需 3 行数据，展开展开即可，不必为此改走 bulk 路径（那会混淆本用例的覆盖点）。
        await db.InsertAsync(new SmokeChild { ParentId = parent.Id, Label = "c0", Weight = 0 });
        await db.InsertAsync(new SmokeChild { ParentId = parent.Id, Label = "c1", Weight = 1 });
        await db.InsertAsync(new SmokeChild { ParentId = parent.Id, Label = "c2", Weight = 2 });

        // ── OrderBy / ThenBy / Skip / Take ──
        var page = await db.From<SmokeChild>()
            .Where($"parent_id = {parent.Id}")
            .OrderByDescending(x => x.Label)
            .ThenBy(x => x.Id)
            .Skip(1)
            .Take(1)
            .ToListAsync();
        await Assert.That(page.Count).IsEqualTo(1);
        await Assert.That(page[0].Label).IsEqualTo("c1");

        // ── WhereIn / WhereNotIn ──
        var inList = await db.From<SmokeChild>().WhereIn(x => x.Label, ["c0", "c2"]).ToListAsync();
        var notInList = await db.From<SmokeChild>().WhereNotIn(x => x.Label, ["c0"]).ToListAsync();
        await Assert.That(inList.Count).IsEqualTo(2);
        await Assert.That(notInList.Count).IsEqualTo(2);

        // ── Set(expr, value) + ExecuteNonQueryAsync ──
        int renamed = await db.From<SmokeChild>()
            .Set(x => x.Label, "renamed")
            .Where($"label = {"c2"}")
            .ExecuteNonQueryAsync();
        await Assert.That(renamed).IsEqualTo(1);
        var renamedRow = (await db.From<SmokeChild>().Where($"label = {"renamed"}").ToListAsync()).Single();
        await Assert.That(renamedRow.Id).IsGreaterThan(0);

        // ── Select 投影（文档口径：仅 DryRun/ToSql）──
        var projection = db.From<SmokeChild>().Select(x => x.Id, x => x.Label).AsDryRun();
        await Assert.That(projection.Sql).Contains("label");
        // 投影只含列出的列——未列出的 weight 不得出现（原断言写成了反向，被本用例当场抓出）
        await Assert.That(projection.Sql).DoesNotContain("weight");

        // ── GroupBy / Having（SQL 面）──
        var grouped = db.From<SmokeChild>()
            .GroupBy(x => x.ParentId)
            .Having($"COUNT(*) > {0}")
            .AsDryRun();
        await Assert.That(grouped.Sql).Contains("GROUP BY");
        await Assert.That(grouped.Sql).Contains("HAVING");

        // ── Include / ThenInclude（只发 JOIN，不装配导航对象）──
        var withInclude = db.From<SmokeChild>()
            .Include<SmokeParent>(c => c.ParentId, p => p.Id)
            .Where($"label = {"c0"}");
        await Assert.That(withInclude.AsDryRun().Sql).Contains("smoke_parent");
        await Assert.That((await withInclude.ToListAsync()).Count).IsEqualTo(1);

        var withThenInclude = db.From<SmokeChild>()
            .ThenInclude<SmokeParent, SmokeChild>(p => p.Id, c => c.ParentId);
        await Assert.That(withThenInclude.AsDryRun().Sql).Contains("INNER JOIN");
        await Assert.That((await withThenInclude.ToListAsync()).Count).IsGreaterThan(0);

        // ── CTE：子查询参数化 + 主查询 FROM CTE ──
        var cteRows = await db.From<SmokeChild>()
            .With("smoke_cte", $"SELECT * FROM smoke_child WHERE weight >= {1}")
            .ToListAsync();
        // 3 行里 weight>=1 的只有 2 行（0/1/2）——CTE 的筛选必须真的生效，
        // 故同时断言行数与"返回行都满足 CTE 条件"（原断言写成 3，被本用例当场抓出）
        await Assert.That(cteRows.Count).IsEqualTo(2);
        await Assert.That(cteRows.All(static row => row.Weight >= 1)).IsTrue();

        // ── 窗口函数 ──
        var window = await db.From<SmokeChild>()
            .UnsafeWindowOver("ROW_NUMBER()", "PARTITION BY parent_id ORDER BY id")
            .Take(1)
            .ToListAsync();
        await Assert.That(window.Count).IsEqualTo(1);

        // ── 悲观锁：只断言 SQL 形态，**不执行** ──
        // ITM-639（5c47b69 登记）：SQLite 不支持 FOR UPDATE/SHARE，且**故意不在构建期拒绝**——
        // 既定契约是"SQLite 上可用 DryRun/ToSql 预览锁语句形态（面向 PG/MySQL 的生产 SQL），
        // 执行期由 SQLite 报语法错误"。故这里断言形态而非执行；执行会抛
        // SqliteException: near "FOR": syntax error（我第一版就是这么写的，撞上后核到该登记）。
        await Assert.That(db.From<SmokeChild>().ForUpdate().AsDryRun().Sql).Contains("FOR UPDATE");
        await Assert.That(db.From<SmokeChild>().ForUpdate(skipLocked: true).AsDryRun().Sql).Contains("SKIP LOCKED");
        await Assert.That(db.From<SmokeChild>().ForShare().AsDryRun().Sql).Contains("FOR SHARE");

        // ── 缓存 / 预编译 / 标签 ──
        var cached = db.From<SmokeChild>().WithCache("aot-smoke-cache", TimeSpan.FromSeconds(5)).Where($"label = {"c0"}");
        await Assert.That((await cached.ToListAsync()).Count).IsEqualTo(1);
        await Assert.That((await cached.ToListAsync()).Count).IsEqualTo(1); // 第二次走缓存
        var prepared = await db.From<SmokeChild>().AsPrepared().Where($"label = {"c0"}").ToListAsync();
        await Assert.That(prepared.Count).IsEqualTo(1);
        await Assert.That(db.From<SmokeChild>().Tag("aot-smoke").AsDryRun().Sql).Contains("aot-smoke");

        // ── AsSplitQuery 去掉 JOIN 但保留 WHERE ──
        var split = db.From<SmokeChild>()
            .Include<SmokeParent>(c => c.ParentId, p => p.Id)
            .Where($"label = {"c0"}")
            .AsSplitQuery();
        await Assert.That(split.AsDryRun().Sql).DoesNotContain("JOIN");
        await Assert.That((await split.ToListAsync()).Count).IsEqualTo(1);

    }
    [Test]
    public async Task ForEachAsync_StreamsAllRows_WithoutMaterializingList()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var parent = await db.InsertAsync(new SmokeParent { Name = "fp" });
        await db.InsertAsync(new SmokeChild { ParentId = parent.Id, Label = "c0", Weight = 1 });
        await db.InsertAsync(new SmokeChild { ParentId = parent.Id, Label = "c1", Weight = 2 });
        await db.InsertAsync(new SmokeChild { ParentId = parent.Id, Label = "c2", Weight = 4 });

        long count = 0;
        long weightSum = 0;
        await db.From<SmokeChild>()
            .Where($"parent_id = {parent.Id}")
            .ForEachAsync((child, _) =>
            {
                count++;
                weightSum += child.Weight;
                return default;
            });

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(weightSum).IsEqualTo(7);

        // 与 Where 组合的过滤必须生效（回调只见到匹配行）
        long filtered = 0;
        await db.From<SmokeChild>()
            .Where($"parent_id = {parent.Id}").Where($"weight >= {2}")
            .ForEachAsync((_, _) =>
            {
                filtered++;
                return default;
            });
        await Assert.That(filtered).IsEqualTo(2);
    }
}

[Table("smoke_parent")]
internal sealed partial class SmokeParent
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

[Table("smoke_child")]
internal sealed partial class SmokeChild
{
    [Key] public long Id { get; set; }
    [Column("parent_id")] public long ParentId { get; set; }
    [Column("label")] public string Label { get; set; } = "";
    [Column("weight")] public long Weight { get; set; }
}
