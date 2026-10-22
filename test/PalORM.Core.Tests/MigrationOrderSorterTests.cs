namespace PalORM.Core.Tests;

/// <summary>P2-12：跨程序集建表序——<see cref="MigrationOrderSorter"/> 按 DDL 的
/// REFERENCES 边做稳定拓扑排序的纯逻辑覆盖（不触库）。类型键用 BCL 类型占位，
/// 排序只看 Type 身份与表名映射。</summary>
internal sealed class MigrationOrderSorterTests
{
    private static readonly Type Parent = typeof(int);
    private static readonly Type Child = typeof(long);
    private static readonly Type GrandChild = typeof(string);
    private static readonly Type CycleA = typeof(double);
    private static readonly Type CycleB = typeof(decimal);
    private static readonly Type SelfRef = typeof(Guid);
    private static readonly Type ExternalRef = typeof(DateTime);

    private static List<(Type Type, string Ddl, IReadOnlyList<string> IndexDdls)> Entries(
        params (Type Type, string Ddl)[] items)
        => items
            .Select(item => (item.Type, item.Ddl, (IReadOnlyList<string>)Array.Empty<string>()))
            .ToList();

    private static Dictionary<Type, string> TableNames(params (Type Type, string Table)[] items)
        => items.ToDictionary(item => item.Type, item => item.Table);

    [Test]
    public async Task ChildRegisteredBeforeParent_IsReorderedParentFirst()
    {
        var entries = Entries(
            (Child,
                "CREATE TABLE IF NOT EXISTS \"child\" (\n" +
                "    \"parent_id\" BIGINT NOT NULL,\n" +
                "    FOREIGN KEY (\"parent_id\") REFERENCES \"parent\" (\"id\") ON DELETE NO ACTION\n)"),
            (Parent, "CREATE TABLE IF NOT EXISTS \"parent\" (\n    \"id\" INTEGER NOT NULL\n)"));
        var names = TableNames((Parent, "parent"), (Child, "child"));

        var ordered = MigrationOrderSorter.SortByReferences(entries, names);

        await Assert.That(ordered[0].Type).IsEqualTo(Parent);
        await Assert.That(ordered[1].Type).IsEqualTo(Child);
    }

    [Test]
    public async Task ReversedThreeLevelChain_IsFullyReordered_WithBacktickQuoting()
    {
        var entries = Entries(
            (GrandChild,
                "CREATE TABLE IF NOT EXISTS `grand` (\n    FOREIGN KEY (`child_id`) REFERENCES `child` (`id`) ON DELETE CASCADE\n)"),
            (Child,
                "CREATE TABLE IF NOT EXISTS `child` (\n    FOREIGN KEY (`parent_id`) REFERENCES `parent` (`id`) ON DELETE CASCADE\n)"),
            (Parent, "CREATE TABLE IF NOT EXISTS `parent` (\n    `id` INTEGER NOT NULL\n)"));
        var names = TableNames((Parent, "parent"), (Child, "child"), (GrandChild, "grand"));

        var ordered = MigrationOrderSorter.SortByReferences(entries, names);

        string sequence = string.Join(",", ordered.Select(entry => entry.Type.Name));
        await Assert.That(sequence).IsEqualTo("Int32,Int64,String");
    }

    [Test]
    public async Task CyclicReferences_TerminateAndKeepInputOrder()
    {
        var entries = Entries(
            (CycleA,
                "CREATE TABLE IF NOT EXISTS \"a\" (\n    FOREIGN KEY (\"b_id\") REFERENCES \"b\" (\"id\")\n)"),
            (CycleB,
                "CREATE TABLE IF NOT EXISTS \"b\" (\n    FOREIGN KEY (\"a_id\") REFERENCES \"a\" (\"id\")\n)"));
        var names = TableNames((CycleA, "a"), (CycleB, "b"));

        var ordered = MigrationOrderSorter.SortByReferences(entries, names);

        string sequence = string.Join(",", ordered.Select(entry => entry.Type.Name));
        await Assert.That(sequence).IsEqualTo("Double,Decimal");
    }

    [Test]
    public async Task UnregisteredAndSelfReferences_DoNotCreateEdges()
    {
        var entries = Entries(
            (SelfRef,
                "CREATE TABLE IF NOT EXISTS \"self\" (\n" +
                "    \"parent_id\" BIGINT,\n" +
                "    FOREIGN KEY (\"parent_id\") REFERENCES \"self\" (\"id\") ON DELETE NO ACTION\n)"),
            (ExternalRef,
                "CREATE TABLE IF NOT EXISTS \"child\" (\n" +
                "    FOREIGN KEY (\"external_id\") REFERENCES \"external_table\" (\"id\")\n)"));
        var names = TableNames((SelfRef, "self"), (ExternalRef, "child"));

        var ordered = MigrationOrderSorter.SortByReferences(entries, names);

        string sequence = string.Join(",", ordered.Select(entry => entry.Type.Name));
        await Assert.That(sequence).IsEqualTo("Guid,DateTime");
    }
}
