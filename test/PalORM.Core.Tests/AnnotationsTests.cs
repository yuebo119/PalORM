namespace PalORM.Core.Tests;

public sealed class AnnotationsTests
{
    [Test]
    public async Task TableAttribute_SetsName()
    {
        var attr = new TableAttribute("users") { Schema = "public" };
        await Assert.That(attr.Name).IsEqualTo("users");
        await Assert.That(attr.Schema).IsEqualTo("public");
    }

    [Test]
    public async Task ColumnAttribute_SetsNameAndOptions()
    {
        var attr = new ColumnAttribute("email") { Length = 255, Precision = 18, Scale = 2 };
        await Assert.That(attr.Name).IsEqualTo("email");
        await Assert.That(attr.Length).IsEqualTo(255);
        await Assert.That(attr.Precision).IsEqualTo(18);
        await Assert.That(attr.Scale).IsEqualTo(2);
    }

    [Test]
    public async Task KeyAttribute_CanBeInstantiated()
    {
        var attr = new KeyAttribute();
        // r19/T-P3-17：原 GetType()==typeof(KeyAttribute) 恒真——改锁 AttributeUsage 契约
        //（仅属性、单次、不可继承——这些是生成器主键收集的实际判据）
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            attr.GetType(), typeof(AttributeUsageAttribute))!;
        await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Property);
        await Assert.That(usage.AllowMultiple).IsFalse();
        await Assert.That(usage.Inherited).IsFalse();
    }

    [Test]
    public async Task ForeignKey_DefaultsToNoAction()
    {
        var attr = new ForeignKeyAttribute("departments", "id");
        await Assert.That(attr.ReferencedTable).IsEqualTo("departments");
        await Assert.That(attr.ReferencedColumn).IsEqualTo("id");
        await Assert.That(attr.OnDelete).IsEqualTo(DeleteAction.NoAction);
    }

    [Test]
    public async Task ForeignKey_Cascade()
    {
        var attr = new ForeignKeyAttribute("parent", "id") { OnDelete = DeleteAction.Cascade };
        await Assert.That(attr.OnDelete).IsEqualTo(DeleteAction.Cascade);
    }

    [Test]
    public async Task AllAttributes_CanBeInstantiated()
    {
        // T-P3-10：有状态特性补属性断言，纯标记特性（无状态）保留构造冒烟——
        // 其全部行为面就是 AttributeUsage 元数据（由分析器测试族覆盖）。
        _ = new NotMappedAttribute();
        _ = new ConcurrencyCheckAttribute();
        _ = new IgnoreOnInsertAttribute();
        _ = new RequiredAttribute();

        var defaultValue = new DefaultValueAttribute("NOW()");
        await Assert.That(defaultValue.Expression).IsEqualTo("NOW()");

        _ = new TimestampAttribute();
        _ = new SoftDeleteAttribute();

        var sensitive = new SensitiveDataAttribute { Mask = "***" };
        await Assert.That(sensitive.Mask).IsEqualTo("***");

        var computed = new ComputedAttribute("price * quantity");
        await Assert.That(computed.Expression).IsEqualTo("price * quantity");

        var converter = new ConverterAttribute(typeof(string));
        await Assert.That(converter.ConverterType).IsEqualTo(typeof(string));

        _ = new TenantAwareAttribute();

        var rawJson = new OwnedJsonAttribute();
        await Assert.That(rawJson.ContextType).IsNull();
        var ownedJson = new OwnedJsonAttribute(typeof(string));
        await Assert.That(ownedJson.ContextType).IsEqualTo(typeof(string));

        var index = new IndexAttribute("ix_test", "col1", "col2") { Unique = true };
        await Assert.That(index.Name).IsEqualTo("ix_test");
        await Assert.That(index.Columns).IsEquivalentTo(["col1", "col2"]);
        await Assert.That(index.Unique).IsTrue();

        _ = new UniqueAttribute();

        var sqlFile = new SqlFileAttribute("queries/get_user.sql");
        await Assert.That(sqlFile.Path).IsEqualTo("queries/get_user.sql");

        var schema = new SchemaAttribute("public");
        await Assert.That(schema.Name).IsEqualTo("public");

        var database = new DatabaseAttribute("analytics");
        await Assert.That(database.Name).IsEqualTo("analytics");

        var sqlTemplate = new SqlTemplateAttribute("GetById");
        await Assert.That(sqlTemplate.Name).IsEqualTo("GetById");
    }
}
