namespace PalORM.Core.Tests;

public sealed class PalORM_RuntimeTests
{
    private sealed class FragmentEntityOne;
    private sealed class FragmentEntityTwo;
    private sealed class DuplicateFragmentEntity;
    private sealed class InconsistentFragmentEntity;
    private sealed class MutableFragmentEntity;
    private sealed class MergedViewEntity;

    private static readonly string[] MergedViewExpectedColumns = ["merged_col"];

    [Test]
    public async Task Register_MergesMultipleFragmentsIntoFrozenSnapshots()
    {
        _ = new FragmentEntityOne();
        _ = new FragmentEntityTwo();
        PalORM_Runtime.Register(CreateFragment<FragmentEntityOne>("fragment_one", "property_one"));
        PalORM_Runtime.Register(CreateFragment<FragmentEntityTwo>("fragment_two", "property_two"));

        await Assert.That(PalORM_Runtime.TableNames[typeof(FragmentEntityOne)]).IsEqualTo("fragment_one");
        await Assert.That(PalORM_Runtime.TableNames[typeof(FragmentEntityTwo)]).IsEqualTo("fragment_two");
        await Assert.That(PalORM_Runtime.PropertyToColumn[typeof(FragmentEntityOne)]["Property"]).IsEqualTo("property_one");
        await Assert.That(PalORM_Runtime.PropertyToColumn[typeof(FragmentEntityTwo)]["Property"]).IsEqualTo("property_two");
    }

    [Test]
    public async Task Register_DuplicateEntityFailsFastWithoutPartialMerge()
    {
        _ = new DuplicateFragmentEntity();
        PalORM_Runtime.Register(CreateFragment<DuplicateFragmentEntity>("original", "original_column"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            PalORM_Runtime.Register(CreateFragment<DuplicateFragmentEntity>("duplicate", "duplicate_column"));
            return Task.CompletedTask;
        });

        await Assert.That(PalORM_Runtime.TableNames[typeof(DuplicateFragmentEntity)]).IsEqualTo("original");
        await Assert.That(PalORM_Runtime.PropertyToColumn[typeof(DuplicateFragmentEntity)]["Property"]).IsEqualTo("original_column");
    }

    [Test]
    public async Task Register_InconsistentFragmentFailsBeforePublishingAnySnapshot()
    {
        _ = new InconsistentFragmentEntity();
        RegistryFragment fragment = CreateFragment<InconsistentFragmentEntity>("inconsistent", "value");
        fragment = new RegistryFragment
        {
            RowFactories = new Dictionary<Type, object>(),
            TableNames = fragment.TableNames,
            CommandSqls = fragment.CommandSqls,
            BindInsert = fragment.BindInsert,
            BindUpdate = fragment.BindUpdate,
            BindDelete = fragment.BindDelete,
            PkColumns = fragment.PkColumns,
            ColumnNames = fragment.ColumnNames,
            PropertyToColumn = fragment.PropertyToColumn,
            CreateTableSql = fragment.CreateTableSql,
            SetIdDelegates = fragment.SetIdDelegates,
            CrudMetadatas = fragment.CrudMetadatas,
            EntityFeatures = fragment.EntityFeatures
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            PalORM_Runtime.Register(fragment);
            return Task.CompletedTask;
        });

        Type entityType = typeof(InconsistentFragmentEntity);
        await Assert.That(PalORM_Runtime.TableNames.ContainsKey(entityType)).IsFalse();
        await Assert.That(PalORM_Runtime.CommandSqls.ContainsKey(entityType)).IsFalse();
        await Assert.That(PalORM_Runtime.BindInsert.ContainsKey(entityType)).IsFalse();
        await Assert.That(PalORM_Runtime.PropertyToColumn.ContainsKey(entityType)).IsFalse();
        await Assert.That(PalORM_Runtime.CrudMetadatas.ContainsKey(entityType)).IsFalse();
    }

    [Test]
    public async Task Register_CopiesMutableColumnArraysBeforePublishing()
    {
        _ = new MutableFragmentEntity();
        string[] columnNames = ["original"];
        string[] insertColumns = ["original"];
        string[] upsertColumns = ["original"];
        RegistryFragment fragment = CreateFragment<MutableFragmentEntity>(
            "mutable", "original", columnNames, insertColumns, upsertColumns);

        PalORM_Runtime.Register(fragment);
        columnNames[0] = "changed";
        insertColumns[0] = "changed";
        upsertColumns[0] = "changed";

        await Assert.That(PalORM_Runtime.ColumnNames[typeof(MutableFragmentEntity)][0]).IsEqualTo("original");
        CrudMetadata metadata = PalORM_Runtime.CrudMetadatas[typeof(MutableFragmentEntity)];
        await Assert.That(metadata.InsertColumns[0]).IsEqualTo("original");
        await Assert.That(metadata.UpsertColumns[0]).IsEqualTo("original");
    }

    [Test]
    public async Task Register_BuildsMergedEntityInfoView_ConsistentWithSourceDictionaries()
    {
        // P1-2（2026-10-12 全量重读）：合并视图 _entityInfo 的完整性与一致性锁定——
        // From<T>/GetAsync/GetAllAsync 的热路径单查由此视图供数，键集缺失或字段错位
        // 会让三入口的行为在热路径与冷路径（旧三查）之间分叉。
        _ = new MergedViewEntity();
        PalORM_Runtime.Register(CreateFragment<MergedViewEntity>("merged_view_t", "merged_col"));

        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        await Assert.That(state._entityInfo.ContainsKey(typeof(MergedViewEntity))).IsTrue();

        EntityRuntimeInfo info = state._entityInfo[typeof(MergedViewEntity)];
        // 与源字典逐位一致：工厂/表名/列名同源，CRUD 元数据与能力标志随视图携带
        await Assert.That(info.TableName).IsEqualTo("merged_view_t");
        await Assert.That(info.ColumnNames).IsEquivalentTo(MergedViewExpectedColumns);
        await Assert.That(ReferenceEquals(info.RowFactory, PalORM_Runtime.RowFactories[typeof(MergedViewEntity)])).IsTrue();
        await Assert.That(ReferenceEquals(info.TableName, PalORM_Runtime.TableNames[typeof(MergedViewEntity)])).IsTrue();
        CrudMetadata sourceCrud = PalORM_Runtime.CrudMetadatas[typeof(MergedViewEntity)];
        await Assert.That(info.Crud.BindInsert).IsEqualTo(sourceCrud.BindInsert);
        await Assert.That(info.Crud.InsertColumns).IsEquivalentTo(sourceCrud.InsertColumns);
        await Assert.That(info.Features).IsEqualTo(PalORM_Runtime.EntityFeatures[typeof(MergedViewEntity)]);

        // 模块初始化器注册的全部实体都在合并视图内（键集 = TableNames 全集）
        await Assert.That(state._entityInfo.Count).IsEqualTo(state._tableNames.Count);
    }

    private static RegistryFragment CreateFragment<TEntity>(
        string tableName,
        string columnName,
        string[]? columnNames = null,
        string[]? insertColumns = null,
        string[]? upsertColumns = null,
        string[]? updateColumns = null)
    {
        Type entityType = typeof(TEntity);
        columnNames ??= [columnName];
        insertColumns ??= [columnName];
        upsertColumns ??= [columnName];
        updateColumns ??= [columnName];
        // S108/S1186: 测试桩绑定器--RegistryFragment 需要委托占位，实际测试不消费绑定结果。
        static void Bind(System.Data.Common.DbCommand command, object entity) { /* test stub: no-op binder */ }
        static void BindWithOffset(System.Data.Common.DbCommand command, object entity, int offset) { /* test stub */ }

        return new RegistryFragment
        {
            RowFactories = new Dictionary<Type, object> { [entityType] = new object() },
            TableNames = new Dictionary<Type, string> { [entityType] = tableName },
            CommandSqls = new Dictionary<Type, CommandSqlSet> { [entityType] = new("I", "U", "D", "IR", "UR", "UM", "IL") },
            BindInsert = new Dictionary<Type, Action<System.Data.Common.DbCommand, object, int>> { [entityType] = BindWithOffset },
            BindUpdate = new Dictionary<Type, Action<System.Data.Common.DbCommand, object>> { [entityType] = Bind },
            BindDelete = new Dictionary<Type, Action<System.Data.Common.DbCommand, object>> { [entityType] = Bind },
            PkColumns = new Dictionary<Type, string> { [entityType] = "id" },
            ColumnNames = new Dictionary<Type, string[]> { [entityType] = columnNames },
            PropertyToColumn = new Dictionary<Type, IReadOnlyDictionary<string, string>>
            {
                [entityType] = new Dictionary<string, string> { ["Property"] = columnName }
            },
            CreateTableSql = new Dictionary<Type, string> { [entityType] = "DDL" },
            SetIdDelegates = new Dictionary<Type, Action<object, long>> { [entityType] = static (_, _) => { } },
            CrudMetadatas = new Dictionary<Type, CrudMetadata>
            {
                [entityType] = new(
                    new("I", "U", "D", "IR", "UR", "UM", "IL"),
                    new CrudBindings(BindWithOffset, null, Bind, Bind, new object()),
                    new CrudColumns(insertColumns, upsertColumns, updateColumns),
                    null, static _ => false)
            },
            EntityFeatures = new Dictionary<Type, EntityFeatures>
            {
                [entityType] = EntityFeatures.None
            }
        };
    }

    [Test]
    public async Task RuntimeFields_ArePopulated_AfterModuleInit()
    {
        // 验证注册表属性全部可访问且含数据（模块初始化器填充后）。
        // 评审 2026-09-02：新生成器片段不再发射 legacy CommandSqls/CreateTableSql（方言 SQL/DDL
        // 唯一真源）；本套件其他测试可能手工 Register 携带该载荷的片段，故不断言其全局计数——
        // "新生成物不含 legacy 载荷"由 SourceGenIntegrationTests 的 *_IsGenerated 测试锁定。
        await Assert.That(PalORM_Runtime.RowFactories.Count).IsGreaterThan(0);
        await Assert.That(PalORM_Runtime.TableNames.Count).IsGreaterThan(0);
        await Assert.That(PalORM_Runtime.CommandSqlsByDialect.Count).IsGreaterThan(0);
        await Assert.That(PalORM_Runtime.BindInsert.Count).IsGreaterThan(0);
        await Assert.That(PalORM_Runtime.BindUpdate.Count).IsGreaterThan(0);
        await Assert.That(PalORM_Runtime.BindDelete.Count).IsGreaterThan(0);
        await Assert.That(PalORM_Runtime.PkColumns.Count).IsGreaterThan(0);
        await Assert.That(PalORM_Runtime.ColumnNames.Count).IsGreaterThan(0);
        await Assert.That(PalORM_Runtime.CreateTableSqlByDialect.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task CommandSqlSet_IsRecordStruct()
    {
        var set = new CommandSqlSet("I", "U", "D", "IR", "UR", "UM", "IL");
        await Assert.That(set.Insert).IsEqualTo("I");
        await Assert.That(set.Update).IsEqualTo("U");
        await Assert.That(set.Delete).IsEqualTo("D");
        await Assert.That(set.InsertReturning).IsEqualTo("IR");
        await Assert.That(set.UpsertReturning).IsEqualTo("UR");
        await Assert.That(set.UpsertMySql).IsEqualTo("UM");
    }
}
