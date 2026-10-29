using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>pairwise 矩阵（方案 §4.6 / §11 四轴覆盖）：27 操作 × 16 形状 = 432 对。
/// <para>每对两种处置：可执行（跑一次并断言基本语义），或不适用（写明理由）。覆盖自检用反射与
/// 枚举核对 432 对全部有处置，因此"覆盖"不是靠人记，而是可机械核对。</para>
/// <para>不适用只登记两类：主键形状不匹配（16 形状主键均为自增，显式主键操作无落点），以及
/// 编译期已被分析器拒绝的组合（PALORM031）。</para></summary>
[NotInParallel("ConformanceMatrix")]
public sealed class PairwiseMatrixTests
{
    private static readonly MatrixOperation[] Operations = [.. Enum.GetValues<MatrixOperation>()];

    private static readonly Dictionary<(MatrixOperation Op, string Shape), string> NotApplicable = BuildNotApplicable();

    private static Dictionary<(MatrixOperation, string), string> BuildNotApplicable()
    {
        var map = new Dictionary<(MatrixOperation, string), string>();
        // 16 个形状的主键全是数据库自增（[Key] 默认），显式主键插入无落点；
        // 显式主键形态由辅助表 cf_bulk_plain / cf_set_drift 承担，不在 16 形状网格内
        foreach (string shape in PairwiseShapes.ShapeIds)
            map[(MatrixOperation.InsertExplicitKey, shape)] = "该形状主键为数据库自增，显式主键形态由辅助表承担";

        // 非软删形状没有"软删"语义，其 DeleteAsync 就是物理删，已由 DeleteHard 覆盖
        foreach (string shape in PairwiseShapes.ShapeIds)
        {
            if (shape is "S02" or "S15") continue;
            map[(MatrixOperation.DeleteSoft, shape)] = "该形状无 [SoftDelete]，DeleteAsync 即物理删除（已由 DeleteHard 覆盖）";
        }

        // UPSERT 无法遵守乐观锁：SaveAsync 与 BulkMerge 对带 [ConcurrencyCheck] 的实体显式抛
        // NotSupportedException（产品行为，不是缺陷）。并发令牌形状用 UpdateAsync（会校验版本）覆盖。
        foreach (string shape in (string[])["S04", "S15"])
        {
            const string reason = "SaveAsync（UPSERT）与 [ConcurrencyCheck] 不兼容，产品显式抛 NotSupportedException";
            map[(MatrixOperation.SaveInsertBranch, shape)] = reason;
            map[(MatrixOperation.SaveUpdateBranch, shape)] = reason;
            map[(MatrixOperation.BulkMerge, shape)] = reason + "（BulkMerge 走 UPSERT）";
        }

        // PALORM031：带并发令牌的实体在 PG/MySQL 上不支持批量更新（SQLite 回退逐行），属编译期拒绝
        map[(MatrixOperation.BulkUpdateBatch, "S04")] = "PALORM031 编译期拒绝：并发令牌实体的批量更新在 PG/MySQL 不可用";
        return map;
    }

    [Test]
    public async Task Matrix_AllPairsAreClassified()
    {
        List<string> problems = [];
        int executable = 0;
        int notApplicable = 0;

        foreach (MatrixOperation op in Operations)
        {
            foreach (string shape in PairwiseShapes.ShapeIds)
            {
                bool declaredNa = NotApplicable.ContainsKey((op, shape));
                if (declaredNa) notApplicable++;
                else executable++;
            }
        }

        int pairs = Operations.Length * PairwiseShapes.ShapeIds.Count;
        if (pairs != 432) problems.Add($"对数为 {pairs}，27 操作 × 16 形状应为 432");
        if (executable + notApplicable != pairs) problems.Add("分类总和与对数不符");
        if (executable < 300) problems.Add($"可执行对数仅 {executable}，低于 300 的地板");

        await Assert.That(string.Join(" | ", problems)).IsEmpty();
        await Assert.That(executable).IsGreaterThanOrEqualTo(300);
    }

    [Test]
    public async Task Matrix_AllExecutablePairsRun()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("matrix_pairs");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();

        List<string> failures = [];
        int executed = 0;

        foreach (MatrixOperation op in Operations)
        {
            foreach (string shape in PairwiseShapes.ShapeIds)
            {
                if (NotApplicable.ContainsKey((op, shape))) continue;
                try
                {
                    await PairwiseShapes.CleanAsync(db, shape);
                    await PairwiseShapes.PrepareAsync(db, shape);
                    await ExecuteAsync(op, shape, db);
                    executed++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{shape}/{op}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // 先报失败明细（更有信息量），再核有效负载下限
        await Assert.That(string.Join("\n", failures.Take(12))).IsEmpty();
        await Assert.That(executed).IsGreaterThanOrEqualTo(300);
    }

    private static Task ExecuteAsync(
        MatrixOperation op, string shapeId, DataSession<SqliteProvider> db)
        => shapeId switch
        {
            "S01" => ExecuteAsync(op, db, PairwiseShapes.S01),
            "S02" => ExecuteAsync(op, db, PairwiseShapes.S02),
            "S03" => ExecuteAsync(op, db, PairwiseShapes.S03),
            "S04" => ExecuteAsync(op, db, PairwiseShapes.S04),
            "S05" => ExecuteAsync(op, db, PairwiseShapes.S05),
            "S06" => ExecuteAsync(op, db, PairwiseShapes.S06),
            "S07" => ExecuteAsync(op, db, PairwiseShapes.S07),
            "S08" => ExecuteAsync(op, db, PairwiseShapes.S08),
            "S09" => ExecuteAsync(op, db, PairwiseShapes.S09),
            "S10" => ExecuteAsync(op, db, PairwiseShapes.S10),
            "S11" => ExecuteAsync(op, db, PairwiseShapes.S11),
            "S12" => ExecuteAsync(op, db, PairwiseShapes.S12),
            "S13" => ExecuteAsync(op, db, PairwiseShapes.S13),
            "S14" => ExecuteAsync(op, db, PairwiseShapes.S14),
            "S15" => ExecuteAsync(op, db, PairwiseShapes.S15),
            "S16" => ExecuteAsync(op, db, PairwiseShapes.S16),
            _ => throw new ArgumentOutOfRangeException(nameof(shapeId), shapeId, "未知形状")
        };

    private static async Task ExecuteAsync<TEntity>(
        MatrixOperation op, DataSession<SqliteProvider> db, ShapeSpec<TEntity> spec)
        where TEntity : class, new()
    {
        CancellationToken ct = CancellationToken.None;
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
            case MatrixOperation.DeleteSoft:
            {
                TEntity existing = await db.InsertAsync(spec.Create(), ct);
                int affected = await db.DeleteAsync<TEntity>(spec.GetId(existing), ct);
                await Assert.That(affected).IsEqualTo(1);
                // 物理删与软删的共同可观察面：默认查询看不到该行
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(0L);
                break;
            }
            case MatrixOperation.GetByKey:
            {
                TEntity existing = await db.InsertAsync(spec.Create(), ct);
                TEntity? loaded = await db.GetAsync<TEntity>(spec.GetId(existing), ct);
                await Assert.That(loaded).IsNotNull();
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
                List<TEntity> rows = await db.From<TEntity>().Where($"\"Id\" > 0").ToListAsync(ct);
                await Assert.That(rows.Count).IsEqualTo(1);
                break;
            }
            case MatrixOperation.Aggregate:
            {
                await db.InsertAsync(spec.Create(), ct);
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(1L);
                break;
            }
            case MatrixOperation.Scalar:
            {
                await db.InsertAsync(spec.Create(), ct);
                long count = await db.ScalarAsync<long>(
                    System.Runtime.CompilerServices.FormattableStringFactory.Create(
                        $"SELECT COUNT(*) FROM \"{spec.TableName}\""),
                    ct: ct);
                await Assert.That(count).IsEqualTo(1L);
                break;
            }
            case MatrixOperation.InnerJoin:
            case MatrixOperation.LeftJoin:
            {
                await db.InsertAsync(spec.Create(), ct);
                // 关联到辅助表 cf_bulk_plain（为空）：两种 JOIN 都必须能构建并执行
                // InnerJoin/LeftJoin 的公开重载只收 onClause（FormattableString）；
                // 纯 SQL 必须用 FormattableStringFactory，插值会把表名变成参数值
                var onClause = System.Runtime.CompilerServices.FormattableStringFactory.Create(
                    $"\"cf_bulk_plain\".\"Id\" = \"{spec.TableName}\".\"Id\"");
                var builder = op == MatrixOperation.InnerJoin
                    ? db.From<TEntity>().InnerJoin<CfBulkPlainEntity>(onClause)
                    : db.From<TEntity>().LeftJoin<CfBulkPlainEntity>(onClause);
                List<TEntity> rows = await builder.ToListAsync(ct);
                await Assert.That(rows.Count).IsGreaterThanOrEqualTo(0);
                break;
            }
            case MatrixOperation.Cte:
            {
                await db.InsertAsync(spec.Create(), ct);
                List<TEntity> rows = await db.From<TEntity>()
                    .With("matrix_cte",
                        System.Runtime.CompilerServices.FormattableStringFactory.Create(
                            $"SELECT * FROM \"{spec.TableName}\""))
                    .ToListAsync(ct);
                await Assert.That(rows.Count).IsEqualTo(1);
                break;
            }
            case MatrixOperation.GroupByHaving:
            {
                await db.InsertAsync(spec.Create(), ct);
                List<TEntity> rows = await db.From<TEntity>()
                    .GroupBy(spec.IdExpression).ToListAsync(ct);
                await Assert.That(rows.Count).IsGreaterThanOrEqualTo(1);
                break;
            }
            case MatrixOperation.WindowOver:
            {
                await db.InsertAsync(spec.Create(), ct);
                List<TEntity> rows = await db.From<TEntity>()
                    .UnsafeWindowOver("ROW_NUMBER()", "ORDER BY \"Id\"").ToListAsync(ct);
                await Assert.That(rows.Count).IsEqualTo(1);
                break;
            }
            case MatrixOperation.ToPage:
            {
                await db.InsertAsync(spec.Create(), ct);
                (List<TEntity> page, long total) = await db.From<TEntity>()
                    .ToPageAsync(2, spec.KeyExpression, ct: ct);
                await Assert.That(total).IsEqualTo(1L);
                await Assert.That(page.Count).IsEqualTo(1);
                break;
            }
            case MatrixOperation.BulkInsert:
            {
                TEntity[] rows = [spec.Create(), spec.Create()];
                long affected = await db.BulkInsertAsync(rows, ct);
                await Assert.That(affected).IsEqualTo(2L);
                break;
            }
            case MatrixOperation.BulkUpdate:
            {
                await db.BulkInsertAsync([spec.Create(), spec.Create()], ct);
                // 自增形状的 BulkInsert 不回填 Id，必须先读回带主键的实体
                List<TEntity> rows = await db.GetAllAsync<TEntity>(ct);
                if (rows.Count == 0) break;
                await db.BulkUpdateAsync(rows, ct);
                break;
            }
            case MatrixOperation.BulkUpdateBatch:
            {
                await db.BulkInsertAsync([spec.Create(), spec.Create()], ct);
                List<TEntity> rows = await db.GetAllAsync<TEntity>(ct);
                if (rows.Count == 0) break;
                await db.BulkUpdateBatchAsync(rows, ct);
                break;
            }
            case MatrixOperation.BulkMerge:
            {
                TEntity first = await db.InsertAsync(spec.Create(), ct);
                await db.BulkMergeAsync([first, spec.Create()], ct);
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(2L);
                break;
            }
            case MatrixOperation.BulkDelete:
            {
                await db.BulkInsertAsync([spec.Create(), spec.Create()], ct);
                long[] keys = [.. (await db.GetAllAsync<TEntity>(ct)).Select(spec.GetId)];
                if (keys.Length == 0) break;
                long affected = await db.BulkDeleteAsync<TEntity, long>(keys, ct);
                await Assert.That(affected).IsEqualTo((long)keys.Length);
                break;
            }
            case MatrixOperation.TransactionCommit:
            {
                using System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync(ct: ct);
                await db.InsertAsync(spec.Create(), ct);
                await transaction.CommitAsync(ct);
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(1L);
                break;
            }
            case MatrixOperation.TransactionRollback:
            {
                using System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync(ct: ct);
                await db.InsertAsync(spec.Create(), ct);
                await transaction.RollbackAsync(ct);
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(0L);
                break;
            }
            case MatrixOperation.SavepointRollback:
            {
                using System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync(ct: ct);
                await db.InsertAsync(spec.Create(), ct);
                await db.SavepointAsync(transaction, "matrix_sp", ct);
                await db.InsertAsync(spec.Create(), ct);
                await db.RollbackToAsync(transaction, "matrix_sp", ct);
                await transaction.CommitAsync(ct);
                await Assert.That(await db.CountAsync<TEntity>(ct: ct)).IsEqualTo(1L);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, "未知操作");
        }
    }
}