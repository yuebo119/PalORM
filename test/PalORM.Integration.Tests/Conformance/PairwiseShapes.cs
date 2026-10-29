using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>pairwise 矩阵的操作枚举（27 个，与方案 §4.6 的操作表一一对应）。</summary>
internal enum MatrixOperation
{
    InsertAutoKey,
    InsertExplicitKey,
    SaveInsertBranch,
    SaveUpdateBranch,
    UpdateAllColumns,
    UpdateColumnsPartial,
    DeleteHard,
    DeleteSoft,
    GetByKey,
    GetAll,
    QueryConditional,
    Aggregate,
    Scalar,
    InnerJoin,
    LeftJoin,
    Cte,
    GroupByHaving,
    WindowOver,
    ToPage,
    BulkInsert,
    BulkUpdate,
    BulkUpdateBatch,
    BulkMerge,
    BulkDelete,
    TransactionCommit,
    TransactionRollback,
    SavepointRollback
}

/// <summary>一个形状的执行规格：造行、读主键、表名、主键是否自增。</summary>
internal sealed class ShapeSpec<TEntity> where TEntity : class, new()
{
    public required string ShapeId { get; init; }

    public required string TableName { get; init; }

    public required Func<TEntity> Create { get; init; }

    public required Func<TEntity, long> GetId { get; init; }

    /// <summary>主键表达式（Include 的关联键、GroupBy 的分组列都用它）。</summary>
    public required System.Linq.Expressions.Expression<Func<TEntity, object?>> IdExpression { get; init; }

    /// <summary>主键的强类型表达式（ToPageAsync 的键集分页要求 TKey 而非 object）。</summary>
    public required System.Linq.Expressions.Expression<Func<TEntity, long>> KeyExpression { get; init; }

    /// <summary>UpdateColumns 用的可写列（计算列等不可写列不能选）。</summary>
    public required System.Linq.Expressions.Expression<Func<TEntity, object?>> UpdateColumn { get; init; }

    /// <summary>主键由数据库自增：显式主键操作对这类形状不适用。</summary>
    public required bool AutoIncrementKey { get; init; }

    /// <summary>插入前需要的准备动作（如外键父行）。</summary>
    public Func<DataSession<SqliteProvider>, CancellationToken, Task>? PrepareAsync { get; init; }
}

/// <summary>16 个形状的执行规格（方案 §4.6 的 S01 到 S16）。
/// <para>每个形状造一行"最小可行数据"：只填必填列，可空列留空，枚举取枚举首项。
/// 目的是让 27 个操作都能在这个形状上执行一次，而不是覆盖该形状的全部列语义（那由 A 层单形状用例承担）。</para></summary>
internal static class PairwiseShapes
{
    public static ShapeSpec<CfScalarEntity> S01 { get; } = new()
    {
        ShapeId = "S01",
        TableName = "cf_scalar",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.VInt,
        Create = () => new CfScalarEntity
        {
            VInt = 1,
            VShort = 1,
            VByte = 1,
            VString = "s",
            VChar = 'c',
            VBool = true,
            VDecimal = 1.5m,
            VDouble = 1.5,
            VFloat = 1.5f,
            VDateTime = new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc),
            VGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            VDto = new DateTimeOffset(2026, 7, 18, 10, 30, 0, TimeSpan.Zero),
            VDateOnly = new DateOnly(2026, 7, 18),
            VTimeOnly = new TimeOnly(10, 30, 0),
            VBytes = [1, 2, 3],
            VNullableInt = null,
            VNullableTimeOnly = null,
            VNullableBytes = null
        }
    };

    public static ShapeSpec<CfSoftDeleteEntity> S02 { get; } = new()
    {
        ShapeId = "S02",
        TableName = "cf_soft_delete",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Name,
        Create = () => new CfSoftDeleteEntity { Name = "s02" }
    };

    public static ShapeSpec<CfTenantEntity> S03 { get; } = new()
    {
        ShapeId = "S03",
        TableName = "cf_tenant",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Value,
        Create = () => new CfTenantEntity { TenantId = 1, Value = "s03" }
    };

    public static ShapeSpec<CfVersionedEntity> S04 { get; } = new()
    {
        ShapeId = "S04",
        TableName = "cf_versioned",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Name,
        Create = () => new CfVersionedEntity { Name = "s04", Version = 0 }
    };

    public static ShapeSpec<CfConverterEntity> S05 { get; } = new()
    {
        ShapeId = "S05",
        TableName = "cf_converter",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Code,
        Create = () => new CfConverterEntity { Code = "s05" }
    };

    public static ShapeSpec<CfOwnedJsonObjectEntity> S06 { get; } = new()
    {
        ShapeId = "S06",
        TableName = "cf_owned_json_object",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Payload,
        Create = () => new CfOwnedJsonObjectEntity
        {
            Payload = new CfJsonPayload { Key = "s06", Count = 1 }
        }
    };

    public static ShapeSpec<CfOwnedJsonStringEntity> S07 { get; } = new()
    {
        ShapeId = "S07",
        TableName = "cf_owned_json_string",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Raw,
        Create = () => new CfOwnedJsonStringEntity { Raw = "{}" }
    };

    public static ShapeSpec<CfEnumStringEntity> S08 { get; } = new()
    {
        ShapeId = "S08",
        TableName = "cf_enum_string",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Status,
        Create = () => new CfEnumStringEntity { Status = CfOrderStatus.Active }
    };

    public static ShapeSpec<CfEnumInt32Entity> S09 { get; } = new()
    {
        ShapeId = "S09",
        TableName = "cf_enum_int32",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Status,
        Create = () => new CfEnumInt32Entity { Status = CfOrderStatus.Active }
    };

    public static ShapeSpec<CfEnumInt64Entity> S10 { get; } = new()
    {
        ShapeId = "S10",
        TableName = "cf_enum_int64",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Status,
        Create = () => new CfEnumInt64Entity { Status = CfOrderStatus.Active }
    };

    public static ShapeSpec<CfInheritedEntity> S11 { get; } = new()
    {
        ShapeId = "S11",
        TableName = "cf_inherited",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Name,
        Create = () => new CfInheritedEntity { CreatedBy = "s11", Name = "s11" }
    };

    private static int _skuSequence;

    public static ShapeSpec<CfCompositeIndexEntity> S12 { get; } = new()
    {
        ShapeId = "S12",
        TableName = "cf_composite_index",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Category,
        // Sku 是 [Unique]：同形状多次造行必须给不同值，否则批量插入自撞唯一约束
        Create = () => new CfCompositeIndexEntity
        {
            Category = "s12",
            Price = 1.5m,
            Sku = $"sku-{Interlocked.Increment(ref _skuSequence)}"
        }
    };

    public static ShapeSpec<CfForeignKeyChild> S13 { get; } = new()
    {
        ShapeId = "S13",
        TableName = "cf_fk_child",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.ParentId,
        // 外键父行必须先存在，否则插入被 FK 约束拒绝
        Create = () => new CfForeignKeyChild { ParentId = 1 },
        PrepareAsync = async (db, ct) =>
        {
            if (await db.CountAsync<CfForeignKeyParent>(ct: ct) == 0)
                await db.InsertAsync(new CfForeignKeyParent { Id = 1, Name = "parent" }, ct);
        }
    };

    public static ShapeSpec<CfComputedEntity> S14 { get; } = new()
    {
        ShapeId = "S14",
        TableName = "cf_computed",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.BaseValue,
        Create = () => new CfComputedEntity { BaseValue = 10 }
    };

    public static ShapeSpec<CfComboFilterEntity> S15 { get; } = new()
    {
        ShapeId = "S15",
        TableName = "cf_combo_filter",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Name,
        Create = () => new CfComboFilterEntity { TenantId = 1, Name = "s15", Version = 0 }
    };

    public static ShapeSpec<CfComboTypesEntity> S16 { get; } = new()
    {
        ShapeId = "S16",
        TableName = "cf_combo_types",
        AutoIncrementKey = true,
        GetId = e => e.Id,
        IdExpression = e => e.Id,
        KeyExpression = e => e.Id,
        UpdateColumn = e => e.Code,
        Create = () => new CfComboTypesEntity
        {
            Code = "s16",
            Payload = new CfJsonPayload { Key = "s16", Count = 1 },
            Status = CfOrderStatus.Active
        }
    };

    public static IReadOnlyList<string> ShapeIds { get; } =
        ["S01", "S02", "S03", "S04", "S05", "S06", "S07", "S08",
         "S09", "S10", "S11", "S12", "S13", "S14", "S15", "S16"];

    public static async Task CleanAsync(
        DataSession<SqliteProvider> db, string shapeId, CancellationToken ct = default)
    {
        string? table = shapeId switch
        {
            "S01" => S01.TableName,
            "S02" => S02.TableName,
            "S03" => S03.TableName,
            "S04" => S04.TableName,
            "S05" => S05.TableName,
            "S06" => S06.TableName,
            "S07" => S07.TableName,
            "S08" => S08.TableName,
            "S09" => S09.TableName,
            "S10" => S10.TableName,
            "S11" => S11.TableName,
            "S12" => S12.TableName,
            "S13" => S13.TableName,
            "S14" => S14.TableName,
            "S15" => S15.TableName,
            "S16" => S16.TableName,
            _ => null
        };
        if (table is null) return;
        // 表名是 SQL 结构位置：插值会被参数化，SQLite 报 "no such table: {0}"。必须传纯 SQL
        await db.ExecuteAsync(
            System.Runtime.CompilerServices.FormattableStringFactory.Create($"DELETE FROM \"{table}\""), ct);
    }

    public static async Task PrepareAsync(
        DataSession<SqliteProvider> db, string shapeId, CancellationToken ct = default)
    {
        switch (shapeId)
        {
            case "S13":
                if (S13.PrepareAsync is { } prepare) await prepare(db, ct);
                break;
            default:
                break;
        }
    }
}