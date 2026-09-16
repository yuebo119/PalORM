using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

// 三个测试共用 ext_bulk_entities 表（DROP/CREATE），必须串行
[NotInParallel("ExtBulkTable")]
public sealed class ExternalDatabaseBulkTests
{
    private static DbOptions PgOpts => new()
    {
        ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
    };

    private static DbOptions MySqlOpts => new()
    {
        ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
    };

    private static ExtBulkEntity[] SampleRows() =>
    [
        new()
        {
            Code = "A1", Note = "note-a", Amount = 12.345678m,
            CreatedAt = new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc), OptionalCount = 7,
            Payload = [0x00, 0x01, 0xFF, 0x00]
        },
        // 可空列全 null 行——PG COPY 的 DBNull→NpgsqlDbType 推断疑点（ITM-318）
        new()
        {
            Code = "B2", Note = null, Amount = 0.000001m,
            CreatedAt = new DateTime(2026, 7, 18, 11, 30, 0, DateTimeKind.Utc), OptionalCount = null,
            Payload = null
        },
    ];

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_MigrateAndBinaryCopy_NullableAndUtcDateTime_RoundTrip()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        await db.MigrateAsync();
        try
        {
            long inserted = await db.BulkInsertAsync(SampleRows());
            await Assert.That(inserted).IsEqualTo(2);

            var rows = (await db.GetAllAsync<ExtBulkEntity>()).OrderBy(r => r.Code).ToList();
            await Assert.That(rows.Count).IsEqualTo(2);
            await Assert.That(rows[0].Note).IsEqualTo("note-a");
            await Assert.That(rows[0].Amount).IsEqualTo(12.345678m);
            await Assert.That(rows[0].CreatedAt).IsEqualTo(new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc));
            await Assert.That(rows[1].Note).IsNull();
            await Assert.That(rows[1].OptionalCount).IsNull();
            await Assert.That(rows[1].Amount).IsEqualTo(0.000001m);
            // byte[] 列经 Binary COPY 往返（含 0x00 字节——TEXT 兜底会在此静默变形）
            await Assert.That(rows[0].Payload!.SequenceEqual((byte[])[0x00, 0x01, 0xFF, 0x00])).IsTrue();
            await Assert.That(rows[1].Payload).IsNull();
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        }
    }

    /// <summary>可空列「首行为 null、后续行非 null」的 COPY 参数复用回归。
    /// <para><b>为什么单列一条</b>：v5.6 把 COPY 的参数对象改为每批建一次、逐行只写 Value，
    /// 参数类型由<b>首个</b>绑定行的值推断。现有 <c>PG_MigrateAndBinaryCopy_...</c> 的行序是
    /// 「先全非空、后全 null」，覆盖不到反向顺序——若首行可空列为 null、后续行有值，
    /// 类型推断可能停在首行的形态（ITM-318/ITM-527 登记的 DBNull→NpgsqlDbType 疑点族）。
    /// 本用例把顺序倒过来，是该风险的唯一可证伪入口。</para></summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BinaryCopy_NullFirstThenValue_KeepsTypeInference()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        await db.MigrateAsync();
        try
        {
            // 顺序刻意反转：全 null 行在前，有值行在后
            ExtBulkEntity[] rows =
            [
                new()
                {
                    Code = "NULLS", Note = null, Amount = 0m,
                    CreatedAt = new DateTime(2026, 7, 18, 9, 0, 0, DateTimeKind.Utc),
                    OptionalCount = null, Payload = null
                },
                new()
                {
                    Code = "VALUED", Note = "note-v", Amount = 12.345678m,
                    CreatedAt = new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc),
                    OptionalCount = 7, Payload = [0x00, 0x01, 0xFF, 0x00]
                },
            ];

            long inserted = await db.BulkInsertAsync(rows);
            await Assert.That(inserted).IsEqualTo(2);

            List<ExtBulkEntity> read = [.. (await db.GetAllAsync<ExtBulkEntity>()).OrderBy(r => r.Code)];
            await Assert.That(read.Count).IsEqualTo(2);
            // 前一行（首行、全 null）必须原样还原为 null
            await Assert.That(read[0].Note).IsNull();
            await Assert.That(read[0].OptionalCount).IsNull();
            await Assert.That(read[0].Payload).IsNull();
            // 后一行（非首行、有值）必须完整还原——参数类型若停在首行的 null 形态，此处退化
            await Assert.That(read[1].Note).IsEqualTo("note-v");
            await Assert.That(read[1].OptionalCount).IsEqualTo(7);
            await Assert.That(read[1].Amount).IsEqualTo(12.345678m);
            await Assert.That(read[1].Payload.SequenceEqual((byte[])[0x00, 0x01, 0xFF, 0x00])).IsTrue();
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        }
    }

    /// <summary>MySQL 批量插入的**值保真**用例——覆盖 <see cref="AllTypesEntity"/> 的全部白名单类型。
    /// <para><b>为什么需要它</b>：v5.6 把 BulkCopy 的取值载体从 DataTable 换成
    /// <c>EntityDataReader</c>，值统一经 <c>GetValue</c> 以 <c>object</c> 交给驱动按运行时类型
    /// 格式化为文本。DataTable 路径按列声明 <c>typeof(object)</c> 时的行为与读取器一致，
    /// 但这是一条**新代码路径**：bool/Guid/DateTimeOffset/DateOnly/TimeOnly/float 等类型
    /// 一旦被驱动按错误格式写出，就是静默的数据损坏（列值看起来"有值"）。
    /// 既有 BulkCopy 用例只覆盖 string/decimal/DateTime/byte[]/可空。</para>
    /// <para><b>覆盖率自述</b>：服务端 <c>local_infile=OFF</c> 时 provider 会静默回退多值 INSERT，
    /// 本用例就不经过读取器（值往返仍被验证）。读取器自身的契约由 Core.Tests 的
    /// <c>EntityDataReaderTests</c> 确定性覆盖，不依赖服务端能力。</para></summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkCopy_AllWhitelistedTypes_RoundTripPreservesValues()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(MySqlOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS all_types_entities");
        await db.MigrateAsync();

        var sample = new AllTypesEntity
        {
            VInt = 42,
            VShort = 7,
            VByte = 255,
            VString = "端到端",
            VChar = 'Z',
            VBool = true,
            VDecimal = 12.34m,
            VDouble = 3.14159,
            VFloat = 2.5f,
            VDateTime = new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc),
            VGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            VDto = new DateTimeOffset(2026, 7, 18, 10, 30, 0, TimeSpan.FromHours(8)),
            VDateOnly = new DateOnly(2026, 7, 18),
            VTimeOnly = new TimeOnly(10, 30, 45),
            VBytes = [0x00, 0x01, 0xFF, 0x00, 0x41],
            VNullableInt = null,
            VNullableTimeOnly = new TimeOnly(23, 59, 59),
            VNullableBytes = [9, 8, 7],
        };

        long inserted = await db.BulkInsertAsync([sample]);

        await Assert.That(inserted).IsEqualTo(1);
        var read = (await db.GetAllAsync<AllTypesEntity>()).Single();
        await Assert.That(read.Id).IsGreaterThan(0); // 自增主键由 MySQL 生成（读取器返回 DBNull）
        await Assert.That(read.VInt).IsEqualTo(sample.VInt);
        await Assert.That(read.VShort).IsEqualTo(sample.VShort);
        await Assert.That(read.VByte).IsEqualTo(sample.VByte);
        await Assert.That(read.VString).IsEqualTo(sample.VString);
        await Assert.That(read.VChar).IsEqualTo(sample.VChar);
        await Assert.That(read.VBool).IsEqualTo(sample.VBool);
        await Assert.That(read.VDecimal).IsEqualTo(sample.VDecimal);
        await Assert.That(read.VDouble).IsEqualTo(sample.VDouble);
        await Assert.That(read.VFloat).IsEqualTo(sample.VFloat);
        await Assert.That(read.VDateTime).IsEqualTo(sample.VDateTime);
        await Assert.That(read.VGuid).IsEqualTo(sample.VGuid);
        await Assert.That(read.VDto.UtcDateTime).IsEqualTo(sample.VDto.UtcDateTime);
        await Assert.That(read.VDateOnly).IsEqualTo(sample.VDateOnly);
        await Assert.That(read.VTimeOnly).IsEqualTo(sample.VTimeOnly);
        await Assert.That(read.VBytes.SequenceEqual(sample.VBytes)).IsTrue();
        await Assert.That(read.VNullableInt).IsNull();
        await Assert.That(read.VNullableTimeOnly).IsEqualTo(sample.VNullableTimeOnly);
        await Assert.That(read.VNullableBytes.SequenceEqual(sample.VNullableBytes)).IsTrue();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_MigrateWithUniqueIndexOnString_AndDecimalPrecision_RoundTrip()
    {
        // r19/T-P3-20 观察登记：本测试是一个真库往返链（迁移→幂等→decimal→唯一冲突），
        // 三检查共享同一建表成本——真库不可本地验证时保持单测试形态，拆分留待 CI 环境。
        // ITM-201 真库验证：被索引 string 列 VARCHAR(255)，首次迁移不得报 1170；
        // ITM-303 真库验证：DECIMAL(18,6) 下小数不被截断为整数
        await using var db = await DataSession<MySqlProvider>.CreateAsync(MySqlOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        await db.MigrateAsync();
        try
        {
            // 迁移幂等：二次执行经 1061 兜底不抛
            await db.MigrateAsync();

            long inserted = await db.BulkInsertAsync(SampleRows());
            await Assert.That(inserted).IsEqualTo(2);

            var rows = (await db.GetAllAsync<ExtBulkEntity>()).OrderBy(r => r.Code).ToList();
            await Assert.That(rows[0].Amount).IsEqualTo(12.345678m);
            await Assert.That(rows[1].Amount).IsEqualTo(0.000001m);
            await Assert.That(rows[1].Note).IsNull();
            await Assert.That(rows[1].OptionalCount).IsNull();
            // byte[] 列经 BulkCopy/多值 INSERT 回退往返（local_infile=OFF 时走参数化回退）
            await Assert.That(rows[0].Payload!.SequenceEqual((byte[])[0x00, 0x01, 0xFF, 0x00])).IsTrue();
            await Assert.That(rows[1].Payload).IsNull();

            // 唯一索引真实生效 + IsUniqueViolation 统一判定（ITM-314 真库验证）
            try
            {
                await db.InsertAsync(new ExtBulkEntity
                {
                    Code = "A1", Amount = 1m,
                    CreatedAt = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc)
                });
                throw new InvalidOperationException("重复 code 应触发唯一约束冲突");
            }
            catch (Exception ex)
            {
                await Assert.That(MySqlProvider.IsUniqueViolation(ex)).IsTrue();
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_UniqueViolation_IsUniformlyDetected()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        await db.MigrateAsync();
        try
        {
            await db.InsertAsync(new ExtBulkEntity
            {
                Code = "DUP", Amount = 1m,
                CreatedAt = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc)
            });
            try
            {
                await db.InsertAsync(new ExtBulkEntity
                {
                    Code = "DUP", Amount = 2m,
                    CreatedAt = new DateTime(2026, 7, 18, 13, 0, 0, DateTimeKind.Utc)
                });
                throw new InvalidOperationException("重复 code 应触发唯一约束冲突");
            }
            catch (Exception ex)
            {
                await Assert.That(PostgreSqlProvider.IsUniqueViolation(ex)).IsTrue();
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        }
    }
}

#region Test Entities
// ITM-317/318 真库验证实体：可空列 + UTC DateTime + decimal 精度 + 唯一索引 string 列。
// PG Binary COPY 对 NpgsqlDbType 推断的两个疑点（DBNull 无法推断 / UTC DateTime 与
// TIMESTAMP 列错配）与 MySQL 索引 DDL(1170)/DECIMAL 截断都在此覆盖。
[Table("ext_bulk_entities")]
[Index("ux_ext_bulk_entities_code", "code", Unique = true)]
public partial class ExtBulkEntity
{
    [Key] public long Id { get; set; }
    [Column("code")] [Required] public string Code { get; set; } = "";
    [Column("note")] public string? Note { get; set; }
    [Column("amount")] public decimal Amount { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("optional_count")] public int? OptionalCount { get; set; }
    // CA1819 误报：ORM 实体列需要可变数组读写
#pragma warning disable CA1819
    [Column("payload")] public byte[]? Payload { get; set; }
#pragma warning restore CA1819
}
#endregion
