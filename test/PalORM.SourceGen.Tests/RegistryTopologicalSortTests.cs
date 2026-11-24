using PalORM.SourceGen;

namespace PalORM.SourceGen.Tests;

/// <summary>RegistryEmitter 按外键拓扑排序时的索引命名空间一致性（2026-11-24，片5-E6-01）。
/// 同表名多实体按注释契约应视为同一节点；旧实现 dependsOn 用实体索引作键、ready 只枚举表名
/// 代表索引，非代表实体（同表第二个且带 FK）被加入 ready 后访问不存在的 dependents 键，
/// 抛 KeyNotFoundException。这里锁定 Generate 整体不抛且注册序列完整。</summary>
public sealed class RegistryTopologicalSortTests
{
    [Test]
    public async Task SameTableName_MultipleEntities_WithForeignKey_DoesNotThrow()
    {
        // 实体 A、B 同表名 t；B 带 FK 指向 u；C 为 u。
        // 修复前：实体 B 索引 1 不在 indexByTable 的代表集，拓扑图键集混用 → KeyNotFoundException。
        var models = new EquatableArray<TableModel>(new[]
        {
            Model("A", "t", HasForeignKey: false),
            Model("B", "t", HasForeignKey: true),
            Model("C", "u", HasForeignKey: false),
        });

        string generated = string.Empty;
        Exception? failure = null;
        try
        {
            generated = RegistryEmitter.Generate(models, DialectSelection.All);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await Assert.That(failure).IsNull()
            .Because($"同表名多实体 + FK 不应在拓扑排序时抛异常，实际：{failure?.GetType().Name} {failure?.Message}");
        // 三个实体都必须出现在注册表（一个都不能因排序被丢弃）
        await Assert.That(generated).Contains("typeof(global::T.A)");
        await Assert.That(generated).Contains("typeof(global::T.B)");
        await Assert.That(generated).Contains("typeof(global::T.C)");
    }

    private static TableModel Model(string entity, string table, bool HasForeignKey)
        => new(
            Namespace: "T",
            ClassName: entity,
            EntityTypeName: $"global::T.{entity}",
            GeneratedTypeSuffix: entity,
            TableName: table,
            IsSoftDelete: false,
            IsTenantAware: false,
            Columns: new EquatableArray<ColumnModel>(new[]
            {
                new ColumnModel(
                    PropertyName: "Id", ColumnName: "id", ClrTypeName: "long", ProviderClrTypeName: "long",
                    DbTypeName: "BIGINT", IsPrimaryKey: true, IsAutoIncrement: false, IsNullable: false,
                    IsRequired: true, IgnoreOnInsert: false, IsConcurrencyToken: false, IsTimestamp: false,
                    ComputedExpression: null, IsOwnedJson: false, OwnedJsonContextTypeName: null,
                    ConverterTypeName: null),
            }),
            Indexes: new EquatableArray<IndexModel>(Array.Empty<IndexModel>()),
            ForeignKeys: new EquatableArray<ForeignKeyModel>(HasForeignKey
                ? new[] { new ForeignKeyModel("Parent", "u", "Id", 0) }
                : Array.Empty<ForeignKeyModel>()));
}
