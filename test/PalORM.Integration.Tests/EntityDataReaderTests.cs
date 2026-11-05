using System.Data.Common;
using MySqlConnector;
using PalORM.MySql;

namespace PalORM.Integration.Tests;

/// <summary><see cref="EntityDataReader"/> 的契约单测。
/// <para><b>为什么需要它</b>：读取器只在 <c>MySqlBulkCopy</c> 路径上被使用，而该路径取决于服务端
/// <c>local_infile</c>——OFF 时 provider 静默回退多值 INSERT（不经过读取器），集成用例照样通过。
/// 于是"读取器契约正确"这件事必须有**不依赖数据库能力**的确定性覆盖，否则它只是碰巧被跑过。</para>
/// <para>钉住的契约即驱动实际消费的面：列数/列名/序号定位/前置主键列恒 DBNull/逐行绑定/
/// 空值转 DBNull/行耗尽返回 false/不受影响行数语义/类型化取值/索引器/批量取值/明确拒绝的分块读取。</para>
/// <para><b>为什么挂在 Integration 而不是 Core</b>（2026-11-05）：本类不触库，放 Core 跑得更快，
/// 但 <c>PalORM.MySql</c> 的覆盖率地板只从 Integration 通道采集（cov-integration.xml）——
/// 放 Core 时这些断言对 mysql 读数贡献为 0，CI 实测该读取器二十余行长期停在 0%。
/// 断言本身不因位置改变，门禁可见性改变。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability",
    "CA1849:CallAsyncWhenInAsyncMethod",
    Justification = "被测契约就是同步 Read()/GetValue——DbDataReader 的同步面正是 MySqlBulkCopy "
        + "逐行消费的路径（读取器不实现 ReadAsync 的异步语义）。改用 ReadAsync 测的就不是驱动走的路了。")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
    "S6966:AwaitableMethodNotAwaited",
    Justification = "同 CA1849——同步 Read() 是被测契约本身。")]
public sealed class EntityDataReaderTests
{
    private static DbParameter[] NewPool(MySqlCommand owner, params object?[] values)
    {
        var pool = new DbParameter[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var parameter = owner.CreateParameter();
            parameter.Value = values[i] ?? DBNull.Value;
            pool[i] = parameter;
        }
        return pool;
    }

    [Test]
    public async Task Read_AdvancesAndRebindsEachRow_UntilExhausted()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "s0", 0m);
        int bindCalls = 0;
        using var reader = new EntityDataReader(
            0, 3, pool,
            index =>
            {
                bindCalls++;
                pool[0].Value = "s" + index;
                pool[1].Value = index * 2m;
            },
            ["id", "code", "amount"], 1);

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(bindCalls).IsEqualTo(1);
        await Assert.That(reader.GetValue(1)).IsEqualTo("s0");
        await Assert.That(reader.GetValue(2)).IsEqualTo(0m);

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(bindCalls).IsEqualTo(2);
        await Assert.That(reader.GetValue(1)).IsEqualTo("s1");
        await Assert.That(reader.GetValue(2)).IsEqualTo(2m);

        // 范围是 [0,3) —— 第 3 次读仍是有效行（此前此处漏写，是本用例自己抓出来的）
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(bindCalls).IsEqualTo(3);
        await Assert.That(reader.GetValue(1)).IsEqualTo("s2");
        await Assert.That(reader.GetValue(2)).IsEqualTo(4m);

        await Assert.That(reader.Read()).IsFalse();
        await Assert.That(bindCalls).IsEqualTo(3); // 行耗尽后不再绑定
        await Assert.That(reader.Read()).IsFalse(); // 幂等：重复读仍为 false
    }

    [Test]
    public async Task MissingPrimaryKeyColumns_AlwaysReturnDbNull()
    {
        // 前置 2 个缺失主键列（复合/自增键形态）；参数池只承载插入列
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "x");
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["id", "tenant_id", "code"], 2);

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.IsDBNull(0)).IsTrue();
        await Assert.That(reader.IsDBNull(1)).IsTrue();
        await Assert.That(reader.GetValue(2)).IsEqualTo("x");
        await Assert.That(reader.FieldCount).IsEqualTo(3);
    }

    [Test]
    public async Task NullParameterValue_IsReportedAsDbNull_NotNull()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, null, "kept");
        pool[0].Value = null; // 驱动/绑定器都可能把 NULL 写成 C# null 而非 DBNull
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["code", "note"], 0);

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.IsDBNull(0)).IsTrue();
        await Assert.That(reader.GetValue(0)).IsEqualTo(DBNull.Value);
        await Assert.That(reader.IsDBNull(1)).IsFalse();
    }

    [Test]
    public async Task ColumnLookupAndShape_MatchDriverExpectations()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "a", "b");
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["id", "first", "second"], 1);

        await Assert.That(reader.GetName(1)).IsEqualTo("first");
        await Assert.That(reader.GetOrdinal("second")).IsEqualTo(2);
        await Assert.That(reader.Depth).IsEqualTo(0);
        await Assert.That(reader.RecordsAffected).IsEqualTo(-1); // 行数由驱动回填
        await Assert.That(reader.NextResult()).IsFalse();
        await Assert.That(reader.IsClosed).IsFalse();
        await Assert.That(reader.GetFieldType(1)).IsEqualTo(typeof(object)); // 同 DataTable 路径的 typeof(object)
        await Assert.That(reader.GetDataTypeName(1)).IsEqualTo("object");
        await Assert.That(reader.HasRows).IsTrue();
    }

    [Test]
    public async Task TypedGetters_ReturnTheBoundValue_ForEveryImplementedMember()
    {
        using var owner = new MySqlCommand();
        DateTime stamp = new(2026, 11, 5, 8, 30, 0, DateTimeKind.Utc);
        Guid guid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        // 取二进制可精确表示的值：2.5f / 1.5d 无舍入，断言用等值而非容差
        DbParameter[] pool = NewPool(
            owner,
            true, (byte)7, 'Z', stamp, 12.34m, 1.5d, 2.5f, guid, (short)11, 42, 5_000_000_000L, "text");
        using var reader = new EntityDataReader(
            0, 1, pool, _ => { },
            ["b", "by", "c", "dt", "dec", "dbl", "flt", "guid", "i16", "i32", "i64", "s"], 0);

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetBoolean(0)).IsTrue();
        await Assert.That(reader.GetByte(1)).IsEqualTo((byte)7);
        await Assert.That(reader.GetChar(2)).IsEqualTo('Z');
        await Assert.That(reader.GetDateTime(3)).IsEqualTo(stamp);
        await Assert.That(reader.GetDecimal(4)).IsEqualTo(12.34m);
        await Assert.That(reader.GetDouble(5)).IsEqualTo(1.5d);
        await Assert.That(reader.GetFloat(6)).IsEqualTo(2.5f);
        await Assert.That(reader.GetGuid(7)).IsEqualTo(guid);
        await Assert.That(reader.GetInt16(8)).IsEqualTo((short)11);
        await Assert.That(reader.GetInt32(9)).IsEqualTo(42);
        await Assert.That(reader.GetInt64(10)).IsEqualTo(5_000_000_000L);
        await Assert.That(reader.GetString(11)).IsEqualTo("text");
    }

    [Test]
    public async Task Indexers_MatchGetValue_ByOrdinalAndByName()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "byName");
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["id", "code"], 1);

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader[0]).IsEqualTo(DBNull.Value);   // 前置缺失主键列
        await Assert.That(reader[1]).IsEqualTo("byName");
        await Assert.That(reader["code"]).IsEqualTo("byName");
    }

    [Test]
    public async Task GetValues_FillsUpToFieldCount_AndStopsAtBufferLength()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "a", "b");
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["id", "first", "second"], 1);

        await Assert.That(reader.Read()).IsTrue();

        var full = new object[3];
        await Assert.That(reader.GetValues(full)).IsEqualTo(3);
        await Assert.That(full[0]).IsEqualTo(DBNull.Value);
        await Assert.That(full[1]).IsEqualTo("a");
        await Assert.That(full[2]).IsEqualTo("b");

        // 缓冲区小于列数：只填前 n 个并返回 n（驱动的分批取值形态）
        var narrow = new object[2];
        await Assert.That(reader.GetValues(narrow)).IsEqualTo(2);
        await Assert.That(narrow[1]).IsEqualTo("a");
    }

    [Test]
    public async Task GetValues_NullBuffer_ThrowsArgumentNullException()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "a");
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["id", "code"], 1);

        var ex = Assert.Throws<ArgumentNullException>(() => reader.GetValues(null!));
        await Assert.That(ex.ParamName).IsEqualTo("values");
    }

    [Test]
    public async Task OutOfRangeOrdinal_ThrowsIndexOutOfRange_LikeTheArrayAccessItIs()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "a");
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["id", "code"], 1);

        await Assert.That(reader.Read()).IsTrue();
        // ADO.NET 契约：越界序号由参数数组访问抛 IndexOutOfRangeException（不是 ArgumentOutOfRange）
        Assert.Throws<IndexOutOfRangeException>(() => _ = reader.GetValue(reader.FieldCount));
        Assert.Throws<IndexOutOfRangeException>(() => _ = reader.IsDBNull(reader.FieldCount));
    }

    [Test]
    public async Task EmptyRange_HasNoRows_AndReadReturnsFalseImmediately()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "a");
        int bindCalls = 0;
        using var reader = new EntityDataReader(5, 5, pool, _ => bindCalls++, ["id", "code"], 1);

        await Assert.That(reader.HasRows).IsFalse();
        await Assert.That(reader.Read()).IsFalse();
        await Assert.That(bindCalls).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownColumnName_ThrowsArgumentException_NamingTheColumn()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "a");
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["id", "code"], 1);

        var ex = Assert.Throws<ArgumentException>(() => _ = reader.GetOrdinal("nope"));
        await Assert.That(ex.Message).Contains("nope");
    }

    [Test]
    public async Task BulkChunkedReads_AreNotSilentlySupported()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "a");
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["id", "code"], 1);

        // 不提供即明确失败——静默返回 0 会被调用方读成"读到 0 字节"
        Assert.Throws<NotSupportedException>(() => reader.GetBytes(1, 0, null, 0, 1));
        Assert.Throws<NotSupportedException>(() => reader.GetChars(1, 0, null, 0, 1));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Enumeration_IsNotSupported_WithActionableMessage()
    {
        using var owner = new MySqlCommand();
        DbParameter[] pool = NewPool(owner, "a");
        using var reader = new EntityDataReader(0, 1, pool, _ => { }, ["id", "code"], 1);

        // 静默返回空枚举器会被调用方读成"没有行"——明确抛出并指向真实取值入口
        var ex = Assert.Throws<NotSupportedException>(() => _ = reader.GetEnumerator());
        await Assert.That(ex.Message).Contains("Read()");
    }
}
