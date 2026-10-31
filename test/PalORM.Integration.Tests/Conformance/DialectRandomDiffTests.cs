using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

// DIALECT-4（方案 §6.3）：随机差分与 pairwise 的夜间 lane。
// 默认不跑（PR 只跑固定种子 SQLite 回归）；夜间 workflow 设 PALORM_NIGHTLY_DIALECT=1 后执行。
// 失败时打印种子与收缩结果（ReplayKey 可直接粘回 DGenerator 复现）。
// 有效负载用 marker 行自证：workflow 断言 marker 出现，防止"门禁未启用却全绿"。

internal static class NightlyGate
{
    public const string EnvVar = "PALORM_NIGHTLY_DIALECT";

    public const string RanMarker = "NIGHTLY-DIALECT-RAN";

    public static bool Enabled
        => string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);

    public static int Seed
    {
        get
        {
            string? text = Environment.GetEnvironmentVariable("PALORM_NIGHTLY_SEED");
            return int.TryParse(text, out int seed) ? seed : 20261031;
        }
    }
}

[NotInParallel("ExtBulkTable")]
public sealed class DialectRandomDiffTests
{
    private const int CasesPerSeed = 8;

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_RandomDiff_OneSeed_NoDifferences()
        => await RunRandomDiffAsync(
            "postgresql", SqlDialect.PostgreSql, static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_RandomDiff_OneSeed_NoDifferences()
        => await RunRandomDiffAsync("mysql", SqlDialect.MySql, static () => TestDb.MySqlAsync());

    private static async Task RunRandomDiffAsync<TProvider>(
        string name, SqlDialect dialect, Func<System.Threading.Tasks.Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        if (!NightlyGate.Enabled)
        {
            Console.WriteLine($"NIGHTLY-DIALECT-SKIPPED random-diff/{name}（{NightlyGate.EnvVar}≠1）");
            return;
        }

        await using DataSession<TProvider> db = await factory();
        await db.MigrateAsync();
        System.Data.Common.DbConnection raw = db.GetRawConnection();
        await MirrorTable.EnsureAsync(db, dialect);

        try
        {
            int seed = NightlyGate.Seed;
            List<string> differences = [];
            int executed = 0;
            foreach (DScenario scenario in DGenerator.Generate(seed, CasesPerSeed))
            {
                executed++;
                if (await RandomDiffRunner.HasDifferenceAsync(scenario, db, raw, dialect))
                    differences.Add(scenario.ReplayKey);
            }

            await Assert.That(executed).IsEqualTo(CasesPerSeed);

            if (differences.Count > 0)
            {
                // 失败打印种子与收缩结果：收缩谓词 = 同场景重跑仍不一致
                List<string> shrunk = [];
                foreach (string replayKey in differences.Take(3))
                {
                    DScenario original = DGenerator.Generate(seed, CasesPerSeed)
                        .First(s => s.ReplayKey == replayKey);
                    DScenario min = await ScenarioShrinker.ShrinkAsync(
                        original, candidate => RandomDiffRunner.HasDifferenceAsync(candidate, db, raw, dialect));
                    IReadOnlyList<RowComparer.Difference> detail =
                        await RandomDiffRunner.FindDifferencesAsync(min, db, raw, dialect);
                    string detailText = string.Join(
                        "; ", detail.Take(3).Select(d => $"{d.Kind}/{d.Column}: expected={d.Expected} actual={d.Actual}"));
                    shrunk.Add($"seed={seed} original={replayKey} shrunk={min.ReplayKey} detail=[{detailText}]");
                }
                await Assert.That(string.Join(" | ", shrunk)).IsEmpty()
                    .Because($"{name} 随机差分 {differences.Count}/{CasesPerSeed} 例差异（种子 {seed}）");
            }

            Console.WriteLine($"{NightlyGate.RanMarker} random-diff {name} seed={seed} cases={executed}");
        }
        finally
        {
            await MirrorTable.CleanAsync(raw, dialect);
        }
    }

    // ── pairwise 子集：10 操作 × 5 形状 = 50 对 ─────────────────

    private static readonly MatrixOperation[] Operations =
    [
        MatrixOperation.InsertAutoKey,
        MatrixOperation.SaveInsertBranch,
        MatrixOperation.SaveUpdateBranch,
        MatrixOperation.UpdateAllColumns,
        MatrixOperation.UpdateColumnsPartial,
        MatrixOperation.DeleteHard,
        MatrixOperation.GetByKey,
        MatrixOperation.GetAll,
        MatrixOperation.QueryConditional,
        MatrixOperation.ToPage,
        MatrixOperation.TransactionCommit,
        MatrixOperation.TransactionRollback
    ];

    /// <summary>显式不适用清单（PairwiseMatrixTests.NotApplicable 同源口径）：
    /// S04 带 [ConcurrencyCheck]，SaveAsync 的 UPSERT 无法遵守乐观锁，运行时抛 NotSupportedException。</summary>
    private static readonly HashSet<(MatrixOperation Op, string Shape)> NotApplicable =
    [
        (MatrixOperation.SaveInsertBranch, "S04"),
        (MatrixOperation.SaveUpdateBranch, "S04")
    ];

    private static readonly string[] Shapes = ["S01", "S02", "S04", "S12", "S14"];

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_Pairwise_50Pairs_Hold()
        => await RunPairwiseAsync(
            "postgresql", SqlDialect.PostgreSql, static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_Pairwise_50Pairs_Hold()
        => await RunPairwiseAsync("mysql", SqlDialect.MySql, static () => TestDb.MySqlAsync());

    private static async Task RunPairwiseAsync<TProvider>(
        string name, SqlDialect dialect, Func<System.Threading.Tasks.Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        if (!NightlyGate.Enabled)
        {
            Console.WriteLine($"NIGHTLY-DIALECT-SKIPPED pairwise/{name}（{NightlyGate.EnvVar}≠1）");
            return;
        }

        await using DataSession<TProvider> db = await factory();
        await db.MigrateAsync();

        List<string> failures = [];
        int executed = 0;
        try
        {
            foreach (MatrixOperation op in Operations)
            {
                foreach (string shape in Shapes)
                {
                    if (NotApplicable.Contains((op, shape))) continue;
                    try
                    {
                        await CleanShapeAsync(db, shape, dialect);
                        await ExecuteAsync(op, shape, db, dialect);
                        executed++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{shape}/{op}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            await Assert.That(string.Join("\n", failures.Take(10))).IsEmpty();
            int expected = (Operations.Length * Shapes.Length) - NotApplicable.Count;
            await Assert.That(executed).IsEqualTo(expected);
            Console.WriteLine($"{NightlyGate.RanMarker} pairwise {name} pairs={executed} skipped={NotApplicable.Count}");
        }
        finally
        {
            foreach (string shape in Shapes)
            {
                try
                {
                    await CleanShapeAsync(db, shape, dialect);
                }
                catch (System.Data.Common.DbException)
                {
                    // 清理尽力而为
                }
            }
        }
    }

    /// <summary>按方言引用清空形状表（PairwiseShapes.CleanAsync 是 SQLite 固定签名，不能直接用于远程）。
    /// 表名来自编译期常量，无外部输入。</summary>
    private static async Task CleanShapeAsync<TProvider>(
        DataSession<TProvider> db, string shapeId, SqlDialect dialect)
        where TProvider : IDbProvider
    {
        string? table = shapeId switch
        {
            "S01" => PairwiseShapes.S01.TableName,
            "S02" => PairwiseShapes.S02.TableName,
            "S04" => PairwiseShapes.S04.TableName,
            "S12" => PairwiseShapes.S12.TableName,
            "S14" => PairwiseShapes.S14.TableName,
            _ => null
        };
        if (table is null) return;
        await db.ExecuteAsync(
            System.Runtime.CompilerServices.FormattableStringFactory.Create(
                $"DELETE FROM {DialectSql.Quote(dialect, table)}"));
    }

    private static Task ExecuteAsync<TProvider>(
        MatrixOperation op, string shapeId, DataSession<TProvider> db, SqlDialect dialect)
        where TProvider : IDbProvider
        => shapeId switch
        {
            "S01" => ExecuteAsync(op, db, PairwiseShapes.S01, dialect),
            "S02" => ExecuteAsync(op, db, PairwiseShapes.S02, dialect),
            "S04" => ExecuteAsync(op, db, PairwiseShapes.S04, dialect),
            "S12" => ExecuteAsync(op, db, PairwiseShapes.S12, dialect),
            "S14" => ExecuteAsync(op, db, PairwiseShapes.S14, dialect),
            _ => throw new ArgumentOutOfRangeException(nameof(shapeId), shapeId, "夜间子集外的形状")
        };

    /// <summary>10 个操作在三方言可执行的子集（与 PairwiseMatrixTests 同判据，方言化标识符引用）。</summary>
    private static async Task ExecuteAsync<TProvider, TEntity>(
        MatrixOperation op, DataSession<TProvider> db, ShapeSpec<TEntity> spec, SqlDialect dialect)
        where TProvider : IDbProvider
        where TEntity : class, new()
    {
        CancellationToken ct = CancellationToken.None;
        string qId = DialectSql.Quote(dialect, "Id");
        switch (op)
        {
            case MatrixOperation.InsertAutoKey:
            {
                TEntity inserted = await db.InsertAsync(spec.Create(), ct);
                await Assert.That(spec.GetId(inserted)).IsGreaterThan(0L);
                break;
            }
            case MatrixOperation.SaveInsertBranch:
            {
                await db.SaveAsync(spec.Create(), ct);
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(1L);
                break;
            }
            case MatrixOperation.SaveUpdateBranch:
            {
                TEntity first = await db.InsertAsync(spec.Create(), ct);
                await db.SaveAsync(first, ct);
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(1L);
                break;
            }
            case MatrixOperation.UpdateAllColumns:
            {
                TEntity existing = await db.InsertAsync(spec.Create(), ct);
                int affected = await db.UpdateAsync(existing, ct);
                await Assert.That(affected).IsGreaterThanOrEqualTo(1);
                break;
            }
            case MatrixOperation.UpdateColumnsPartial:
            {
                TEntity existing = await db.InsertAsync(spec.Create(), ct);
                int affected = await db.UpdateColumnsAsync(existing, spec.UpdateColumn, ct);
                await Assert.That(affected).IsEqualTo(1);
                break;
            }
            case MatrixOperation.DeleteHard:
            {
                TEntity existing = await db.InsertAsync(spec.Create(), ct);
                await Assert.That(await db.DeleteAsync<TEntity>(spec.GetId(existing), ct)).IsEqualTo(1);
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(0L);
                break;
            }
            case MatrixOperation.GetByKey:
            {
                TEntity existing = await db.InsertAsync(spec.Create(), ct);
                await Assert.That(await db.GetAsync<TEntity>(spec.GetId(existing), ct)).IsNotNull();
                break;
            }
            case MatrixOperation.GetAll:
            {
                await db.InsertAsync(spec.Create(), ct);
                await db.InsertAsync(spec.Create(), ct);
                await Assert.That((await db.GetAllAsync<TEntity>(ct)).Count).IsEqualTo(2);
                break;
            }
            case MatrixOperation.QueryConditional:
            {
                await db.InsertAsync(spec.Create(), ct);
                string predicate = $"{qId} > 0";
                List<TEntity> rows = await db.From<TEntity>()
                    .Where(System.Runtime.CompilerServices.FormattableStringFactory.Create(predicate))
                    .ToListAsync(ct);
                await Assert.That(rows.Count).IsEqualTo(1);
                break;
            }
            case MatrixOperation.ToPage:
            {
                await db.InsertAsync(spec.Create(), ct);
                await db.InsertAsync(spec.Create(), ct);
                (List<TEntity> page, long total) = await db.From<TEntity>()
                    .ToPageAsync(1, spec.KeyExpression, descending: false, ct: ct);
                await Assert.That(total).IsEqualTo(2L);
                await Assert.That(page.Count).IsEqualTo(1);
                break;
            }
            case MatrixOperation.TransactionCommit:
            {
                System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
                await db.InsertAsync(spec.Create(), ct);
                await transaction.CommitAsync();
                await transaction.DisposeAsync();
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(1L);
                break;
            }
            case MatrixOperation.TransactionRollback:
            {
                System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
                await db.InsertAsync(spec.Create(), ct);
                await transaction.RollbackAsync();
                await transaction.DisposeAsync();
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(0L);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, "夜间子集外的操作");
        }
    }
}