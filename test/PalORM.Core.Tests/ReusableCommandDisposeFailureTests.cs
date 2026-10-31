using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>T1.4 回归：旧复用命令 Dispose 抛异常时，槽必须置空，后续操作不得拿到已释放命令。
/// <para>用反射把“EntityType 不同 + Dispose 抛异常”的槽注入会话，触发第三次插入的晋升路径；
/// 旧实现只释放新命令并重抛，槽仍指向已释放命令，后续插入持续失败。</para></summary>
public sealed class ReusableCommandDisposeFailureTests
{
    [Test]
    [SuppressMessage("Security", "S3011",
        Justification = "测试用反射构造私有嵌套记录并注入槽位，只验证异常路径的槽清理，不改变生产可见面。")]
    [SuppressMessage("Reliability", "CA2000",
        Justification = "ThrowOnDisposeCommand 故意交给槽位释放路径，其 Dispose 抛异常是测试目标。")]
    public async Task Insert_PromotionDisposeFailure_ClearsSlotAndNextInsertSucceeds()
    {
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=slotdispose_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.MigrateAsync();

        var throwingCommand = new ThrowOnDisposeCommand();
        Type slotType = typeof(DataSession<SqliteProvider>).GetNestedType(
            "ReusableCrudCommand", BindingFlags.NonPublic)!;
        if (slotType.ContainsGenericParameters)
            slotType = slotType.MakeGenericType(typeof(SqliteProvider));
        object slot = Activator.CreateInstance(
            slotType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [typeof(SlotDisposeOther), throwingCommand, Array.Empty<DbParameter>(), null],
            culture: null)!;
        FieldInfo slotField = typeof(DataSession<SqliteProvider>).GetField(
            "_reusableInsert", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo counterField = typeof(DataSession<SqliteProvider>).GetField(
            "_insertWriteOps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        slotField.SetValue(session, slot);
        counterField.SetValue(session, 2);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InsertAsync(new SlotDisposeProbe { Name = "first" }));

        await Assert.That(slotField.GetValue(session)).IsNull();

        SlotDisposeProbe second = await session.InsertAsync(new SlotDisposeProbe { Name = "second" });
        await Assert.That(second.Id).IsGreaterThan(0);
    }
}

[Table("slot_dispose_probe")]
internal sealed class SlotDisposeProbe
{
    [Key] public long Id { get; set; }

    [Column("name")] public string Name { get; set; } = "";
}

[Table("slot_dispose_other")]
internal sealed class SlotDisposeOther
{
    [Key] public long Id { get; set; }

    [Column("name")] public string Name { get; set; } = "";
}

/// <summary>Dispose 必抛的 DbCommand 桩，只用于触发槽替换路径。</summary>
internal sealed class ThrowOnDisposeCommand : DbCommand
{
    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection { get; } = new NoopParameterCollection();
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() => throw new NotSupportedException();
    protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(false);
            return;
        }

        base.Dispose(true);
        throw new InvalidOperationException("simulated old-slot dispose failure");
    }
}

internal sealed class NoopParameterCollection : DbParameterCollection
{
    public override int Count => 0;
    public override object SyncRoot => this;
    public override int Add(object value) => throw new NotSupportedException();
    public override void AddRange(Array values) => throw new NotSupportedException();
    public override void Clear() { }
    public override bool Contains(object value) => false;
    public override bool Contains(string value) => false;
    public override void CopyTo(Array array, int index) => throw new NotSupportedException();
    public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    public override int IndexOf(object value) => -1;
    public override int IndexOf(string parameterName) => -1;
    public override void Insert(int index, object value) => throw new NotSupportedException();
    public override void Remove(object value) => throw new NotSupportedException();
    public override void RemoveAt(int index) => throw new NotSupportedException();
    public override void RemoveAt(string parameterName) => throw new NotSupportedException();
    protected override DbParameter GetParameter(int index) => throw new NotSupportedException();
    protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();
    protected override void SetParameter(int index, DbParameter value) => throw new NotSupportedException();
    protected override void SetParameter(string parameterName, DbParameter value) => throw new NotSupportedException();
}
