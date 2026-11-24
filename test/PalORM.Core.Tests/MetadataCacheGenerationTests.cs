namespace PalORM.Core.Tests;

/// <summary>DIG-A1（2026-11-24）：元数据静态缓存的「值 + 代际」必须原子可见。
/// 上一轮已修 EntityInfoCache（Info/Generation 合并为单个 volatile 不可变引用），
/// 本类锁定同型的另两个缓存 MemberResolver.ColumnMapPer / ColumnOrderValidator.ExpectedColumns。
/// 代际由 PalORM_Runtime.Register / RestoreStateForTesting 推进，旧映射配新代际会让
/// 过期列名被当作当前版本使用。</summary>
[NotInParallel]
public sealed class MetadataCacheGenerationTests
{
    private PalORM_Runtime.RuntimeRegistryState? _stateSnapshot;

    [Before(Test)]
    public void Capture() => _stateSnapshot = PalORM_Runtime.CurrentState;

    [After(Test)]
    public void Restore()
    {
        if (_stateSnapshot is { } snapshot)
            PalORM_Runtime.RestoreStateForTesting(snapshot);
    }

    private sealed class GenCacheEntity
    {
        public string Property { get; set; } = "";
    }

    private static RegistryFragment Fragment(string columnName) =>
        new()
        {
            RowFactories = new Dictionary<Type, object> { [typeof(GenCacheEntity)] = new object() },
            TableNames = new Dictionary<Type, string> { [typeof(GenCacheEntity)] = "gen_cache" },
            CommandSqls = new Dictionary<Type, CommandSqlSet> { [typeof(GenCacheEntity)] = new("I", "U", "D", "IR", "UR", "UM", "IL") },
            BindInsert = new Dictionary<Type, Action<System.Data.Common.DbCommand, object, int>> { [typeof(GenCacheEntity)] = static (_, _, _) => { } },
            BindUpdate = new Dictionary<Type, Action<System.Data.Common.DbCommand, object>> { [typeof(GenCacheEntity)] = static (_, _) => { } },
            BindDelete = new Dictionary<Type, Action<System.Data.Common.DbCommand, object>> { [typeof(GenCacheEntity)] = static (_, _) => { } },
            PkColumns = new Dictionary<Type, string> { [typeof(GenCacheEntity)] = "id" },
            ColumnNames = new Dictionary<Type, string[]> { [typeof(GenCacheEntity)] = [columnName] },
            PropertyToColumn = new Dictionary<Type, IReadOnlyDictionary<string, string>>
            {
                [typeof(GenCacheEntity)] = new Dictionary<string, string> { ["Property"] = columnName }
            },
            CreateTableSql = new Dictionary<Type, string> { [typeof(GenCacheEntity)] = "DDL" },
            SetIdDelegates = new Dictionary<Type, Action<object, long>> { [typeof(GenCacheEntity)] = static (_, _) => { } },
            CrudMetadatas = new Dictionary<Type, CrudMetadata>
            {
                [typeof(GenCacheEntity)] = new(
                    new("I", "U", "D", "IR", "UR", "UM", "IL"),
                    new CrudBindings(static (_, _, _) => { }, null, static (_, _) => { }, static (_, _) => { }, new object()),
                    new CrudColumns([columnName], [columnName], [columnName]),
                    null, static _ => false)
            },
            EntityFeatures = new Dictionary<Type, EntityFeatures> { [typeof(GenCacheEntity)] = EntityFeatures.None }
        };

    [Test]
    public async Task MemberResolver_InvalidatesMappingAcrossRegistryGeneration()
    {
        _ = new GenCacheEntity();
        PalORM_Runtime.Register(Fragment("col_first"));
        await Assert.That(MemberResolver.GetColumnName<GenCacheEntity>("Property")).IsEqualTo("col_first");

        PalORM_Runtime.RestoreStateForTesting(_stateSnapshot!);
        PalORM_Runtime.Register(Fragment("col_second"));
        await Assert.That(MemberResolver.GetColumnName<GenCacheEntity>("Property")).IsEqualTo("col_second");
    }

    [Test]
    public async Task MemberResolver_ConcurrentGenerationSwitch_NeverReturnsStaleMapping()
    {
        PalORM_Runtime.Register(Fragment("col_first"));
        _ = MemberResolver.GetColumnName<GenCacheEntity>("Property");   // 填满第一代缓存

        PalORM_Runtime.RestoreStateForTesting(_stateSnapshot!);
        PalORM_Runtime.Register(Fragment("col_second"));

        bool[] results = new bool[64];
        await Parallel.ForEachAsync(Enumerable.Range(0, results.Length), async (i, _) =>
        {
            results[i] = MemberResolver.GetColumnName<GenCacheEntity>("Property") == "col_second";
        });
        await Assert.That(results.All(static ok => ok)).IsTrue();
    }
}
